const HOST_NAME = "com.shigaflow.host";
const pendingKeys = new Set();
const RECENT_HANDOFF_MS = 5000;
const STORAGE_KEY = "recentShigaflowHandoffs";

function notify(title, message) {
  chrome.notifications.create({
    type: "basic",
    iconUrl: "icon.png",
    title,
    message
  }, () => void chrome.runtime.lastError);
}

function makeKey(url, fileName) {
  return `${url}\n${fileName || ""}`;
}

async function loadRecent() {
  const result = await chrome.storage.local.get(STORAGE_KEY);
  const now = Date.now();
  const recent = result[STORAGE_KEY] && typeof result[STORAGE_KEY] === "object"
    ? result[STORAGE_KEY] : {};
  for (const [key, timestamp] of Object.entries(recent)) {
    if (typeof timestamp !== "number" || now - timestamp > RECENT_HANDOFF_MS || timestamp > now + 60000) delete recent[key];
  }
  return recent;
}


async function getGoogleDriveCookies() {
  // Read only Google-domain cookies and pass them to the locally installed Shigaflow
  // native host. They are not stored in extension storage or written to logs.
  const domains = ["google.com", "drive.google.com", "googleusercontent.com"];
  const batches = await Promise.all(domains.map((domain) => new Promise((resolve) => {
    chrome.cookies.getAll({ domain }, (cookies) => {
      const error = chrome.runtime.lastError;
      if (error) resolve([]);
      else resolve(cookies || []);
    });
  })));
  const unique = new Map();
  for (const cookie of batches.flat()) {
    if (!cookie || !cookie.name || !cookie.domain) continue;
    const domain = cookie.domain.toLowerCase();
    if (!(domain === "google.com" || domain.endsWith(".google.com") || domain === "googleusercontent.com" || domain.endsWith(".googleusercontent.com"))) continue;
    const key = `${cookie.name}|${cookie.domain}|${cookie.path}`;
    unique.set(key, {
      name: cookie.name,
      value: cookie.value,
      domain: cookie.domain,
      path: cookie.path || "/",
      secure: Boolean(cookie.secure),
      httpOnly: Boolean(cookie.httpOnly),
      expirationDate: typeof cookie.expirationDate === "number" ? cookie.expirationDate : null
    });
  }
  return [...unique.values()];
}

async function performHandoff(payload) {
  if (!payload || !payload.url || !( /^https?:\/\//i.test(payload.url) || /^magnet:\?/i.test(payload.url) )) {
    return { ok: false, error: "Only HTTP/HTTPS download links and magnet links are supported." };
  }

  const key = makeKey(payload.url, payload.fileName);
  if (pendingKeys.has(key)) return { ok: true, duplicate: true };
  pendingKeys.add(key);
  try {
    const recent = await loadRecent();
    if (recent[key]) return { ok: true, duplicate: true };

    const nativePayload = { ...payload };
    if (nativePayload.googleDriveAuth === true) {
      delete nativePayload.googleDriveAuth;
      nativePayload.cookies = await getGoogleDriveCookies();
      if (nativePayload.cookies.length === 0) {
        return { ok: false, error: "No Google Drive session cookies were available. Make sure you are signed in to Google Drive and reload the Shigaflow extension." };
      }
    }

    const response = await new Promise((resolve) => {
      chrome.runtime.sendNativeMessage(HOST_NAME, nativePayload, (result) => {
        const error = chrome.runtime.lastError;
        if (error) resolve({ ok: false, error: error.message });
        else if (!result || result.ok !== true) resolve({ ok: false, error: result?.error || "Shigaflow did not acknowledge the download." });
        else resolve({ ok: true });
      });
    });
    if (!response.ok) return response;

    try {
      const latest = await loadRecent();
      latest[key] = Date.now();
      await chrome.storage.local.set({ [STORAGE_KEY]: latest });
    } catch (error) {
      console.warn("Could not persist Shigaflow handoff history:", error);
    }
    return response;
  } finally {
    pendingKeys.delete(key);
  }
}


// ---------------------------------------------------------------------------
// Links Shigaflow has captured (or is capturing). If the page ALSO makes Chrome
// start a download of the same target (for example a site that starts the download
// on mouse-up as well as on click), that browser download is cancelled once Shigaflow
// has acknowledged the handoff, so Chrome's own Save As dialog never appears.
// ---------------------------------------------------------------------------
const CAPTURED_TARGET_MS = 20000;
const capturedTargets = new Map(); // targetKey -> { promise }

function targetKeyFor(value) {
  try {
    const url = new URL(value);
    const host = url.hostname.toLowerCase();
    const id = url.searchParams.get("id");
    const isGoogle = host === "google.com" || host.endsWith(".google.com") ||
      host === "googleusercontent.com" || host.endsWith(".googleusercontent.com");
    // Drive builds slightly different URLs for the same file (extra confirm/uuid/authuser
    // parameters), so identify those by file id only.
    if (isGoogle && id) return `gdrive:${id}`;
    return `${url.origin}${url.pathname}${url.search}`;
  } catch { return null; }
}

function handoffToShigaflow(payload) {
  const promise = performHandoff(payload);
  const targetKey = payload?.url ? targetKeyFor(payload.url) : null;
  if (targetKey && !capturedTargets.has(targetKey)) {
    capturedTargets.set(targetKey, { promise });
    const forget = (delay) => setTimeout(() => capturedTargets.delete(targetKey), delay);
    // A failed handoff must not stop the browser from downloading the same link itself.
    promise.then((result) => forget(result?.ok ? CAPTURED_TARGET_MS : 0), () => forget(0));
  }
  return promise;
}

function cancelBrowserDownload(id) {
  return new Promise((resolve) => {
    chrome.downloads.cancel(id, () => {
      void chrome.runtime.lastError;
      // Remove the cancelled entry so it does not linger in Chrome's download list.
      chrome.downloads.erase({ id }, () => { void chrome.runtime.lastError; resolve(); });
    });
  });
}

async function suppressDuplicateBrowserDownload(item) {
  const keys = [item?.url, item?.finalUrl].filter(Boolean).map(targetKeyFor).filter(Boolean);
  for (const key of keys) {
    const entry = capturedTargets.get(key);
    if (!entry) continue;
    const result = await entry.promise.catch(() => null);
    if (result?.ok) {
      await cancelBrowserDownload(item.id);
      return true;
    }
  }
  return false;
}

// Google Drive: the content script "arms" this marker when the Drive Download command is used.
// Drive then makes Chrome start the real download (a single file, several files zipped, or a
// folder zipped). The service worker captures that download.
// NOTE: chrome.downloads.DownloadItem has no tabId, so the marker is global, not per-tab.
const DRIVE_PENDING_KEY = "pendingDriveDownload";
const DRIVE_WINDOW_MS = 300000;
let driveMarkerInMemory = null;

function isGoogleDriveDownload(item) {
  const candidates = [item?.url, item?.finalUrl].filter(Boolean);
  return candidates.some((value) => {
    try {
      const url = new URL(value);
      const host = url.hostname.toLowerCase();
      // Drive bulk ZIPs commonly come from drive.usercontent.google.com,
      // not drive.google.com. Restrict capture to Google-owned download domains.
      return host === "google.com" || host.endsWith(".google.com") ||
        host === "googleusercontent.com" || host.endsWith(".googleusercontent.com");
    } catch { return false; }
  });
}

function fileNameFromDownloadPath(path) {
  if (!path) return "";
  return String(path).split(/[\\/]/).pop() || "";
}

function looksLikeDriveArchiveDownload(item) {
  const name = fileNameFromDownloadPath(item?.filename).toLowerCase();
  if (name.endsWith(".zip")) return true;
  const mime = String(item?.mime || "").toLowerCase().split(";")[0].trim();
  if (mime === "application/zip" || mime === "application/x-zip-compressed") return true;
  const urls = [item?.url, item?.finalUrl].filter(Boolean);
  return urls.some((value) => {
    try {
      const url = new URL(value);
      const host = url.hostname.toLowerCase();
      const isDriveHost = host === "drive.google.com" || host.endsWith(".google.com") ||
        host === "googleusercontent.com" || host.endsWith(".googleusercontent.com");
      return isDriveHost && (/(?:\/download(?:[/?]|$)|v2internal\/download)/i.test(url.pathname) ||
        url.searchParams.get("export") === "download" || url.searchParams.get("download") === "1");
    } catch { return false; }
  });
}

async function readDriveMarker() {
  let marker = driveMarkerInMemory;
  if (!marker) {
    try { marker = (await chrome.storage.session.get(DRIVE_PENDING_KEY))[DRIVE_PENDING_KEY]; } catch { marker = null; }
  }
  if (!marker || typeof marker.expiresAt !== "number" || marker.expiresAt < Date.now()) {
    await clearDriveMarker();
    return null;
  }
  return marker;
}

async function clearDriveMarker() {
  driveMarkerInMemory = null;
  try { await chrome.storage.session.remove(DRIVE_PENDING_KEY); } catch { }
}

async function handleDriveDownload(item) {
  if (!isGoogleDriveDownload(item)) return false;
  const marker = await readDriveMarker();
  if (!marker) return false;

  const downloadUrl = item.finalUrl && /^https?:\/\//i.test(item.finalUrl) ? item.finalUrl : item.url;
  if (!/^https?:\/\//i.test(downloadUrl || "")) {
    await clearDriveMarker();
    notify("Shigaflow could not capture this Google Drive download", "Drive created a download URL that Shigaflow cannot import. Chrome will continue the download.");
    return true;
  }

  // Name and type of what Drive is about to download. Drive's own name for a multi-file
  // selection is a "drive-download-....zip"; the name can be empty while it prepares the archive.
  let archiveName = fileNameFromDownloadPath(item.filename);
  const mime = String(item?.mime || "").toLowerCase().split(";")[0].trim();
  const hasExtension = /\.[a-z0-9]{1,12}$/i.test(archiveName);
  const selectedCount = Number(marker.selectedCount) || 0;
  const isArchive = selectedCount > 1 ||
    /\.zip$/i.test(archiveName) ||
    mime === "application/zip" ||
    mime === "application/x-zip-compressed" ||
    (!hasExtension && selectedCount !== 1);

  if (!archiveName) archiveName = isArchive ? "Google-Drive-Download.zip" : "Google-Drive-Download";
  if (isArchive && !/\.zip$/i.test(archiveName)) archiveName = archiveName.replace(/\.[^.]+$/, "") + ".zip";

  const response = await handoffToShigaflow({
    url: downloadUrl,
    fileName: archiveName,
    googleDriveAuth: true,
    googleDriveBulk: isArchive
  });
  if (!response.ok) {
    await clearDriveMarker();
    notify("Shigaflow could not capture this Google Drive download", response.error || "Chrome will continue the download.");
    return true;
  }

  // Cancel Chrome's copy only after Shigaflow has acknowledged the handoff.
  await cancelBrowserDownload(item.id);
  await clearDriveMarker();
  notify("Google Drive download sent to Shigaflow", "Choose its save location in Shigaflow to start the download.");
  return true;
}

// Any other real browser download (the server sent Content-Disposition: attachment, or a
// non-renderable type). This is how IDM-style capture should work: Chrome decides from the
// response, so ordinary page loads never reach here.
async function handleGenericDownload(item) {
  if (item?.byExtensionId) return false; // started by another extension
  const downloadUrl = item?.finalUrl || item?.url || "";
  if (!/^https?:\/\//i.test(downloadUrl)) return false; // blob:, data:, file: stay with Chrome
  const response = await handoffToShigaflow({
    url: downloadUrl,
    fileName: fileNameFromDownloadPath(item.filename)
  });
  if (!response.ok) return false; // not handed off (e.g. extension not allowed): Chrome continues
  await cancelBrowserDownload(item.id);
  return true;
}

async function handleBrowserDownload(item, suggest) {
  let suggested = false;
  const suggestOnce = () => {
    if (suggested) return;
    suggested = true;
    try { suggest(); } catch (error) { console.warn("Shigaflow filename suggestion failed:", error); }
  };
  try {
    // 1. Chrome started a download for a link Shigaflow already captured: drop Chrome's copy.
    if (await suppressDuplicateBrowserDownload(item)) return;
    // 2. Google Drive download armed by the content script (single file, zip of several files, folder).
    if (await handleDriveDownload(item)) return;
    // 3. Everything else Chrome itself decided is a download.
    await handleGenericDownload(item);
  } catch (error) {
    console.error("Shigaflow download handling failed:", error);
  } finally {
    // Always release Chrome's filename decision, even when the item was cancelled above.
    suggestOnce();
  }
}

chrome.downloads.onDeterminingFilename.addListener((downloadItem, suggest) => {
  // Chrome requires `true` when suggest() will be called asynchronously.
  void handleBrowserDownload(downloadItem, suggest);
  return true;
});

// ---------------------------------------------------------------------------
// Video detection for the on-video "Download" button (video_button.js).
// Players often load their video from a script (blob: URLs), so the page itself does not
// reveal the real file URL. Watching the tab's own network responses does.
// Only response headers are observed; nothing is blocked or modified.
// ---------------------------------------------------------------------------
const VIDEO_BUTTON_KEY = "videoButtonEnabled";
const MEDIA_STORAGE_KEY = "detectedMediaByTab";
const MEDIA_MIN_BYTES = 200 * 1024;     // ignore tiny responses (ads, previews, init segments)
const MEDIA_MAX_PER_TAB = 40;
const VIDEO_EXT = new Set(["mp4", "m4v", "webm", "mkv", "mov", "avi", "flv", "wmv", "mpg", "mpeg", "ogv", "3gp"]);
const AUDIO_EXT = new Set(["mp3", "m4a", "aac", "ogg", "oga", "opus", "wav", "flac"]);
const SEGMENT_EXT = new Set(["ts", "m4s", "cmfv", "cmfa", "aac_seg"]);
const TYPE_TO_EXT = {
  "video/mp4": "mp4", "video/webm": "webm", "video/x-matroska": "mkv", "video/quicktime": "mov",
  "video/x-flv": "flv", "video/x-msvideo": "avi", "video/ogg": "ogv", "video/3gpp": "3gp",
  "audio/mpeg": "mp3", "audio/mp3": "mp3", "audio/mp4": "m4a", "audio/aac": "aac",
  "audio/ogg": "ogg", "audio/wav": "wav", "audio/x-wav": "wav", "audio/flac": "flac"
};

const mediaByTab = new Map();           // tabId -> Map(url -> entry)
let mediaPersistTimer = null;
const mediaReady = (async () => {
  try {
    const stored = (await chrome.storage.session.get(MEDIA_STORAGE_KEY))[MEDIA_STORAGE_KEY] || {};
    for (const [tabId, entries] of Object.entries(stored)) {
      mediaByTab.set(Number(tabId), new Map(entries.map((entry) => [entry.url, entry])));
    }
  } catch { /* session storage unavailable: keep an in-memory registry only */ }
})();

function scheduleMediaPersist() {
  if (mediaPersistTimer) return;
  mediaPersistTimer = setTimeout(async () => {
    mediaPersistTimer = null;
    try {
      const out = {};
      for (const [tabId, entries] of mediaByTab) out[tabId] = [...entries.values()];
      await chrome.storage.session.set({ [MEDIA_STORAGE_KEY]: out });
    } catch { }
  }, 600);
}

function headerValue(headers, name) {
  const wanted = name.toLowerCase();
  const found = (headers || []).find((header) => header?.name?.toLowerCase() === wanted);
  return found?.value || "";
}

// URLs that return only a slice of a stream (YouTube-style range=..., byte offsets, seek positions)
// would download as a broken fragment, so they are never offered.
function isPartialStreamUrl(url) {
  try {
    const params = new URL(url).searchParams;
    if (params.has("range") || params.has("byterange") || params.has("bytestart")) return true;
    const start = params.get("start");
    return start !== null && Number(start) > 0;
  } catch { return false; }
}

function fileNameFromDisposition(disposition) {
  if (!disposition) return "";
  const star = /filename\*\s*=\s*(?:UTF-8'')?([^;]+)/i.exec(disposition);
  const plain = /filename\s*=\s*("([^"]+)"|[^;]+)/i.exec(disposition);
  let raw = star ? star[1] : plain ? (plain[2] || plain[1]) : "";
  raw = raw.trim().replace(/^"|"$/g, "");
  try { raw = decodeURIComponent(raw); } catch { }
  return raw;
}

function classifyMedia(details) {
  let url;
  try { url = new URL(details.url); } catch { return null; }
  if (isPartialStreamUrl(details.url)) return null;
  const host = url.hostname.toLowerCase();
  // YouTube's player streams video as separate signed chunks that cannot be saved as one file.
  if (host.endsWith("googlevideo.com") || host.endsWith("youtube.com") || host.endsWith("youtube-nocookie.com")) return null;

  const type = headerValue(details.responseHeaders, "content-type").split(";")[0].trim().toLowerCase();
  const path = url.pathname.toLowerCase();
  const ext = (/\.([a-z0-9]{2,5})$/.exec(path) || [])[1] || "";
  const vague = type === "" || type === "application/octet-stream" || type === "binary/octet-stream";

  let kind = null;
  if (ext === "m3u8" || type === "application/vnd.apple.mpegurl" || type === "application/x-mpegurl" || type === "audio/mpegurl") kind = "hls";
  else if (ext === "mpd" || type === "application/dash+xml") kind = "dash";
  else if (SEGMENT_EXT.has(ext) || type === "video/mp2t") return null;       // pieces of a streaming video
  else if (type.startsWith("video/") || (vague && VIDEO_EXT.has(ext))) kind = "video";
  else if (type.startsWith("audio/") || (vague && AUDIO_EXT.has(ext))) kind = "audio";
  else return null;

  // Real size: the total in Content-Range for partial (206) answers, else Content-Length.
  let size = null;
  const range = /\/(\d+)\s*$/.exec(headerValue(details.responseHeaders, "content-range"));
  if (range) size = Number(range[1]);
  else if (/^\d+$/.test(headerValue(details.responseHeaders, "content-length"))) size = Number(headerValue(details.responseHeaders, "content-length"));
  if ((kind === "video" || kind === "audio") && size !== null && size < MEDIA_MIN_BYTES) return null;

  const disposition = fileNameFromDisposition(headerValue(details.responseHeaders, "content-disposition"));
  let name = disposition;
  if (!name) {
    try { name = decodeURIComponent(url.pathname.split("/").filter(Boolean).pop() || ""); } catch { name = ""; }
  }
  return {
    url: details.url,
    kind,
    type,
    size,
    ext: (/\.([a-z0-9]{2,5})$/i.exec(name) || [])[1]?.toLowerCase() || ext || TYPE_TO_EXT[type] || "",
    name,
    frameId: details.frameId,
    time: Date.now()
  };
}

async function recordMedia(tabId, entry) {
  await mediaReady;
  let entries = mediaByTab.get(tabId);
  if (!entries) { entries = new Map(); mediaByTab.set(tabId, entries); }
  const existing = entries.get(entry.url);
  if (existing) {
    existing.time = entry.time;
    if (entry.size !== null && (existing.size === null || entry.size > existing.size)) existing.size = entry.size;
  } else {
    entries.set(entry.url, entry);
    while (entries.size > MEDIA_MAX_PER_TAB) entries.delete(entries.keys().next().value);
  }
  scheduleMediaPersist();
}

chrome.webRequest.onHeadersReceived.addListener((details) => {
  if (details.tabId < 0 || details.method !== "GET") return;
  if (details.statusCode !== 200 && details.statusCode !== 206) return;
  const entry = classifyMedia(details);
  if (entry) void recordMedia(details.tabId, entry);
}, { urls: ["http://*/*", "https://*/*"], types: ["media", "xmlhttprequest", "other"] }, ["responseHeaders"]);

chrome.tabs.onRemoved.addListener((tabId) => {
  mediaByTab.delete(tabId);
  scheduleMediaPersist();
});

// A new page in the same tab: forget older detections (keep the last few seconds, which may belong to the new page).
chrome.tabs.onUpdated.addListener((tabId, changeInfo) => {
  if (!changeInfo.url) return;
  const entries = mediaByTab.get(tabId);
  if (!entries) return;
  const cutoff = Date.now() - 4000;
  for (const [url, entry] of entries) if (entry.time < cutoff) entries.delete(url);
  scheduleMediaPersist();
});

// Streaming videos are delivered as dozens of numbered pieces (segment_1.mp4, segment_2.mp4, ...).
// Entries from the same folder that differ only by number are treated as pieces, not as the video.
function splitSegments(list) {
  const groups = new Map();
  for (const entry of list) {
    let key;
    try { const u = new URL(entry.url); key = u.host + u.pathname.replace(/\d+/g, "#"); } catch { key = entry.url; }
    groups.set(key, (groups.get(key) || 0) + 1);
  }
  const files = [];
  let segmented = false;
  for (const entry of list) {
    let key;
    try { const u = new URL(entry.url); key = u.host + u.pathname.replace(/\d+/g, "#"); } catch { key = entry.url; }
    if ((entry.kind === "video" || entry.kind === "audio") && groups.get(key) >= 4) { segmented = true; continue; }
    files.push(entry);
  }
  return { files, segmented };
}

async function getMediaForTab(tabId) {
  await mediaReady;
  const entries = [...(mediaByTab.get(tabId)?.values() || [])].sort((a, b) => b.time - a.time);
  const { files, segmented } = splitSegments(entries);
  return {
    media: files.map(({ url, kind, type, size, ext, name, frameId, time }) => ({ url, kind, type, size, ext, name, frameId, time })),
    segmented: segmented || files.some((entry) => entry.kind === "hls" || entry.kind === "dash")
  };
}

// Choose the saved file name. A meaningful name from the URL is kept; opaque ones
// (hashes, "videoplayback", numbers) are replaced by the page title.
function chooseMediaFileName(media, pageTitle) {
  const ext = (media.ext || TYPE_TO_EXT[media.type] || "mp4").replace(/[^a-z0-9]/gi, "").toLowerCase() || "mp4";
  let base = (media.name || "").replace(/\.[a-z0-9]{2,5}$/i, "").trim();
  const opaque = !base || /^[a-f0-9_-]{16,}$/i.test(base) || /^\d+$/.test(base) ||
    /^(videoplayback|video|index|master|playlist|chunk|stream|download|media|file)$/i.test(base);
  if (opaque) {
    const title = String(pageTitle || "").replace(/[<>:"/\\|?*\u0000-\u001f]/g, " ").replace(/\s+/g, " ").trim().slice(0, 120);
    if (title) base = title;
  }
  base = (base || "video").replace(/[<>:"/\\|?*\u0000-\u001f]/g, "_").replace(/[. ]+$/, "").slice(0, 150);
  return `${base || "video"}.${ext}`;
}

// The toolbar button switches the on-video Download button on or off.
async function applyVideoButtonState() {
  let enabled = true;
  try { enabled = (await chrome.storage.local.get(VIDEO_BUTTON_KEY))[VIDEO_BUTTON_KEY] !== false; } catch { }
  try {
    await chrome.action.setBadgeText({ text: enabled ? "" : "OFF" });
    await chrome.action.setBadgeBackgroundColor({ color: "#6b7280" });
    await chrome.action.setTitle({ title: enabled ? "Shigaflow integration - video button ON (click to turn off)" : "Shigaflow integration - video button OFF (click to turn on)" });
  } catch { }
}
chrome.action.onClicked.addListener(async () => {
  const current = (await chrome.storage.local.get(VIDEO_BUTTON_KEY))[VIDEO_BUTTON_KEY] !== false;
  await chrome.storage.local.set({ [VIDEO_BUTTON_KEY]: !current });
  await applyVideoButtonState();
});
void applyVideoButtonState();

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.type === "SHIGAFLOW_EXPECT_DRIVE_DOWNLOAD") {
    const marker = {
      selectedCount: Math.max(0, Number(message.selectedCount) || 0),
      armedAt: Date.now(),
      expiresAt: Date.now() + DRIVE_WINDOW_MS
    };
    // Set synchronously so a fast single-file download cannot beat the storage write.
    driveMarkerInMemory = marker;
    chrome.storage.session.set({ [DRIVE_PENDING_KEY]: marker })
      .then(() => sendResponse({ ok: true }))
      .catch(() => sendResponse({ ok: true }));
    return true;
  }
  if (message?.type === "SHIGAFLOW_GET_MEDIA") {
    getMediaForTab(sender.tab?.id)
      .then((result) => sendResponse({ ok: true, ...result }))
      .catch(() => sendResponse({ ok: true, media: [], segmented: false }));
    return true;
  }
  if (message?.type === "SHIGAFLOW_DOWNLOAD_MEDIA") {
    const media = message.media || {};
    if (!/^https?:\/\//i.test(media.url || "")) {
      sendResponse({ ok: false, error: "This video has no direct HTTP address Shigaflow can download." });
      return false;
    }
    const payload = { url: media.url, fileName: chooseMediaFileName(media, sender.tab?.title) };
    // The page the video was played on: many video servers only answer requests that name it.
    if (/^https?:\/\//i.test(sender.url || "")) payload.referer = sender.url;
    handoffToShigaflow(payload)
      .then(sendResponse)
      .catch((error) => sendResponse({ ok: false, error: error?.message || "Capture failed." }));
    return true;
  }
  if (message?.type === "SHIGAFLOW_DOWNLOAD_STREAM") {
    // Video the engine (yt-dlp + FFmpeg) should fetch: a video page address and/or an HLS/DASH playlist address.
    // The first address is tried first; Shigaflow falls back to the others if it cannot read the first one.
    const tabUrl = /^https?:\/\//i.test(sender.tab?.url || "") ? sender.tab.url : "";
    const urls = [...new Set([...(Array.isArray(message.urls) ? message.urls : []), tabUrl]
      .filter((value) => typeof value === "string" && /^https?:\/\//i.test(value)))].slice(0, 4);
    if (urls.length === 0) {
      sendResponse({ ok: false, error: "This page has no video address Shigaflow can use." });
      return false;
    }
    const title = String(message.title || sender.tab?.title || "")
      .replace(/^\(\d+\)\s*/, "")
      .replace(/\s*[-|\u2013]\s*(YouTube|Facebook|Instagram|X|Twitter|TikTok)\s*$/i, "")
      .replace(/[<>:"/\\|?*\u0000-\u001f]/g, " ").replace(/\s+/g, " ").trim().slice(0, 120);
    const quality = /^(best|h\d{3,4}|audio-mp3|audio-m4a)$/.test(message.quality || "") ? message.quality : "best";
    const payload = { url: urls[0], alternateUrls: urls.slice(1), mode: "stream", quality, fileName: title, referer: tabUrl || urls[0] };
    handoffToShigaflow(payload)
      .then(sendResponse)
      .catch((error) => sendResponse({ ok: false, error: error?.message || "Capture failed." }));
    return true;
  }
  if (message?.type === "SHIGAFLOW_CAPTURE_ERROR") {
    notify("Shigaflow could not capture this download", message.error || "The download was not handed off.");
    return false;
  }
  if (message?.type !== "SHIGAFLOW_CAPTURE_LINK") return false;
  handoffToShigaflow(message.payload)
    .then(sendResponse)
    .catch((error) => sendResponse({ ok: false, error: error?.message || "Capture failed." }));
  return true;
});

// Deliberately no chrome.downloads.onCreated fallback: it runs after Chrome has
// started the download and cannot reliably suppress Chrome's Save As dialog.
