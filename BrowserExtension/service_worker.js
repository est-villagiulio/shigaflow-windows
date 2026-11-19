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
