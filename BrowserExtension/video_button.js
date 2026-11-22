// Shigaflow on-video "Download" button (like IDM's "Download this video" panel).
// Hover a video to get a small button in its top-right corner. Clicking it sends the video
// to Shigaflow: a plain video file goes straight to the download engine; YouTube, Facebook,
// HLS/DASH streams and script-loaded (blob:) players go to Shigaflow's video engine (yt-dlp + FFmpeg).
// The toolbar icon switches the button on or off.
(() => {
  "use strict";
  if (window.__shigaflowVideoButtonInstalled) return;
  window.__shigaflowVideoButtonInstalled = true;
  if (location.protocol !== "http:" && location.protocol !== "https:") return;
  // googlevideo.com only serves the raw chunks; the real player lives on youtube.com.
  if (/(^|\.)googlevideo\.com$/i.test(location.hostname)) return;

  const ENABLED_KEY = "videoButtonEnabled";
  const MIN_WIDTH = 220;
  const MIN_HEIGHT = 120;
  const MAX_MENU_ROWS = 8;
  // Sites whose players stream in pieces (DASH/MSE) or sign their media URLs: always handled by the video engine.
  const ENGINE_SITES = /(^|\.)(youtube\.com|youtu\.be|youtube-nocookie\.com|facebook\.com|fb\.watch|instagram\.com|twitter\.com|x\.com|tiktok\.com|vimeo\.com|dailymotion\.com|twitch\.tv|reddit\.com)$/i;
  // Addresses of a single video page (what the engine needs), as opposed to a home page or feed.
  const VIDEO_PAGE = /(?:youtube(?:-nocookie)?\.com\/(?:watch\?|shorts\/|embed\/|live\/)|youtu\.be\/|facebook\.com\/(?:watch\/?\?|reel\/|share\/[rv]\/|video\.php|[^/?#]+\/videos\/)|fb\.watch\/|instagram\.com\/(?:reels?|p|tv)\/|(?:twitter|x)\.com\/[^/?#]+\/status\/|tiktok\.com\/[^?#]*video\/|vimeo\.com\/\d|dailymotion\.com\/video\/|twitch\.tv\/(?:videos|[^/?#]+\/clip)\/|reddit\.com\/r\/[^/]+\/comments\/)/i;

  let enabled = true;
  let currentVideo = null;
  let hideTimer = 0;
  let toastTimer = 0;
  let rafId = 0;
  let overUi = false;
  let menuOpen = false;
  let busy = false;

  // ----- messaging (the extension may be reloaded while a page stays open) -----
  function send(message) {
    return new Promise((resolve) => {
      try {
        chrome.runtime.sendMessage(message, (response) => {
          void chrome.runtime.lastError;
          resolve(response);
        });
      } catch { resolve(undefined); }
    });
  }

  try {
    chrome.storage.local.get(ENABLED_KEY, (result) => {
      void chrome.runtime.lastError;
      enabled = result?.[ENABLED_KEY] !== false;
      if (!enabled) hideNow();
    });
    chrome.storage.onChanged.addListener((changes, area) => {
      if (area !== "local" || !changes[ENABLED_KEY]) return;
      enabled = changes[ENABLED_KEY].newValue !== false;
      if (!enabled) hideNow();
    });
  } catch { /* no extension context */ }

  // ----- UI (inside a shadow root so page styles cannot touch it) -----
  const CSS = `
    :host { all: initial; }
    .wrap { position: fixed; display: none; left: 0; top: 0; font: 600 12px/1.2 system-ui, "Segoe UI", Roboto, sans-serif; }
    .dl { all: unset; box-sizing: border-box; display: flex; align-items: center; gap: 6px; height: 30px; padding: 0 13px 0 9px;
          border-radius: 15px; background: #68d5c4; color: #101318; box-shadow: 0 2px 10px rgba(0,0,0,.45);
          cursor: pointer; white-space: nowrap; font: inherit; font-weight: 700; }
    .dl:hover { background: #82e4d4; }
    .dl[disabled] { opacity: .6; cursor: progress; }
    svg { width: 16px; height: 16px; fill: currentColor; flex: none; }
    .menu { position: absolute; right: 0; top: 36px; min-width: 270px; max-width: 380px; padding: 4px; border-radius: 10px;
            background: #1b2029; color: #e8edf5; border: 1px solid #384152; box-shadow: 0 8px 24px rgba(0,0,0,.5); }
    .row { all: unset; box-sizing: border-box; display: block; width: 100%; padding: 8px 10px; border-radius: 7px; cursor: pointer; }
    .row:hover { background: #2b3445; }
    .row .name { display: block; font-weight: 700; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .row .meta { display: block; margin-top: 3px; font-weight: 500; color: #9aa6b8; }
    .toast { position: absolute; right: 0; top: 36px; max-width: 340px; padding: 9px 12px; border-radius: 9px; background: #1b2029;
             color: #e8edf5; border: 1px solid #384152; box-shadow: 0 6px 18px rgba(0,0,0,.5); font-weight: 600; line-height: 1.35; }
    .toast.error { border-color: #b4535a; }
  `;

  const host = document.createElement("div");
  host.style.cssText = "all:initial;position:fixed;left:0;top:0;width:0;height:0;z-index:2147483647;";
  const root = host.attachShadow({ mode: "closed" });
  try {
    const sheet = new CSSStyleSheet();
    sheet.replaceSync(CSS);
    root.adoptedStyleSheets = [sheet];
  } catch {
    const style = document.createElement("style");
    style.textContent = CSS;
    root.appendChild(style);
  }

  const wrap = document.createElement("div");
  wrap.className = "wrap";
  const button = document.createElement("button");
  button.className = "dl";
  button.type = "button";
  button.setAttribute("aria-label", "Download this video with Shigaflow");
  const icon = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  icon.setAttribute("viewBox", "0 0 24 24");
  for (const d of ["M11 3h2v9.17l3.59-3.58L18 10l-6 6-6-6 1.41-1.41L11 12.17z", "M5 18h14v2H5z"]) {
    const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
    path.setAttribute("d", d);
    icon.appendChild(path);
  }
  const label = document.createElement("span");
  label.textContent = "Download video";
  button.append(icon, label);
  wrap.appendChild(button);
  root.appendChild(wrap);

  // Keep clicks on our UI away from the player underneath (it must not pause or seek).
  for (const type of ["click", "mousedown", "mouseup", "pointerdown", "pointerup", "dblclick", "contextmenu", "touchstart"]) {
    host.addEventListener(type, (event) => event.stopPropagation());
  }
  host.addEventListener("mouseenter", () => { overUi = true; cancelHide(); });
  host.addEventListener("mouseleave", () => { overUi = false; scheduleHide(); });

  function ensureAttached() {
    // Inside the fullscreen element when something is fullscreen, otherwise it would not be visible.
    const parent = document.fullscreenElement && !(document.fullscreenElement instanceof HTMLVideoElement)
      ? document.fullscreenElement
      : document.documentElement;
    if (host.parentNode !== parent) parent.appendChild(host);
  }

  // ----- showing / hiding / positioning -----
  function qualifies(video) {
    if (!(video instanceof HTMLVideoElement) || !video.isConnected) return false;
    const rect = video.getBoundingClientRect();
    return rect.width >= MIN_WIDTH && rect.height >= MIN_HEIGHT && rect.bottom > 0 && rect.top < window.innerHeight;
  }

  function cancelHide() { if (hideTimer) { clearTimeout(hideTimer); hideTimer = 0; } }
  function scheduleHide(delay = 900) {
    if (hideTimer || overUi || menuOpen) return;
    hideTimer = setTimeout(() => { hideTimer = 0; if (!overUi && !menuOpen) hideNow(); }, delay);
  }
  function hideNow() {
    cancelHide();
    closeMenu();
    clearToast();
    wrap.style.display = "none";
    currentVideo = null;
  }

  function reposition() {
    rafId = 0;
    if (!currentVideo || wrap.style.display === "none") return;
    if (!qualifies(currentVideo)) { hideNow(); return; }
    const rect = currentVideo.getBoundingClientRect();
    const width = wrap.offsetWidth || 130;
    const left = Math.max(4, Math.min(window.innerWidth - width - 4, rect.right - width - 10));
    const top = Math.max(4, rect.top + 10);
    wrap.style.left = `${Math.round(left)}px`;
    wrap.style.top = `${Math.round(top)}px`;
    rafId = requestAnimationFrame(reposition);
  }

  function show(video) {
    if (!enabled) return;
    cancelHide();
    if (currentVideo !== video) { closeMenu(); clearToast(); }
    currentVideo = video;
    ensureAttached();
    wrap.style.display = "block";
    if (!rafId) rafId = requestAnimationFrame(reposition);
  }

  // ----- finding the video under the pointer -----
  function videosFromEvent(event) {
    const found = new Set();
    const path = typeof event.composedPath === "function" ? event.composedPath() : [];
    for (const node of path) {
      if (node instanceof HTMLVideoElement) found.add(node);
      else if (node instanceof Element && node.shadowRoot) {
        for (const video of node.shadowRoot.querySelectorAll("video")) found.add(video);
      }
    }
    for (const video of document.getElementsByTagName("video")) found.add(video);
    return found;
  }

  let moveQueued = false;
  let lastMove = null;
  function onMove(event) {
    if (!enabled) return;
    if (event.target === host) { overUi = true; cancelHide(); return; }
    lastMove = event;
    if (moveQueued) return;
    moveQueued = true;
    requestAnimationFrame(() => {
      moveQueued = false;
      const e = lastMove;
      if (!e || overUi) return;
      for (const video of videosFromEvent(e)) {
        if (!qualifies(video)) continue;
        const rect = video.getBoundingClientRect();
        if (e.clientX >= rect.left && e.clientX <= rect.right && e.clientY >= rect.top && e.clientY <= rect.bottom) {
          show(video);
          return;
        }
      }
      if (!menuOpen) scheduleHide();
    });
  }
  document.addEventListener("mousemove", onMove, { capture: true, passive: true });
  document.documentElement.addEventListener("mouseleave", () => scheduleHide(), { passive: true });
  document.addEventListener("fullscreenchange", () => { if (currentVideo) { ensureAttached(); } }, { passive: true });
  document.addEventListener("click", (event) => { if (menuOpen && event.target !== host) closeMenu(); }, true);

  // ----- toast and menu -----
  let toastEl = null;
  let menuEl = null;

  function clearToast() {
    if (toastTimer) { clearTimeout(toastTimer); toastTimer = 0; }
    if (toastEl) { toastEl.remove(); toastEl = null; }
  }
  function toast(text, isError = false) {
    closeMenu();
    clearToast();
    toastEl = document.createElement("div");
    toastEl.className = isError ? "toast error" : "toast";
    toastEl.textContent = text;
    wrap.appendChild(toastEl);
    toastTimer = setTimeout(() => { clearToast(); scheduleHide(300); }, isError ? 7000 : 4500);
  }
  function closeMenu() {
    menuOpen = false;
    if (menuEl) { menuEl.remove(); menuEl = null; }
  }

  function formatSize(bytes) {
    if (!Number.isFinite(bytes) || bytes <= 0) return "";
    const units = ["B", "KB", "MB", "GB"];
    let value = bytes, unit = 0;
    while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
    return `${value.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`;
  }

  function showMenu(items) {
    closeMenu();
    clearToast();
    menuEl = document.createElement("div");
    menuEl.className = "menu";
    for (const item of items.slice(0, MAX_MENU_ROWS)) {
      const row = document.createElement("button");
      row.className = "row";
      row.type = "button";
      const name = document.createElement("span");
      name.className = "name";
      name.textContent = item.name || nameFromUrl(item.url) || "video";
      const meta = document.createElement("span");
      meta.className = "meta";
      let hostName = "";
      try { hostName = new URL(item.url).hostname; } catch { }
      meta.textContent = [item.kind === "audio" ? "Audio" : "Video", (item.ext || "").toUpperCase(), formatSize(item.size), hostName]
        .filter(Boolean).join(" \u00b7 ");
      row.append(name, meta);
      row.addEventListener("click", (event) => { event.stopPropagation(); closeMenu(); void sendMedia(item); });
      menuEl.appendChild(row);
    }
    wrap.appendChild(menuEl);
    menuOpen = true;
  }

  // ----- collecting what can be downloaded -----
  function extFromUrl(url) {
    try { return (/\.([a-z0-9]{2,5})$/i.exec(new URL(url).pathname) || [])[1]?.toLowerCase() || ""; } catch { return ""; }
  }
  function nameFromUrl(url) {
    try { return decodeURIComponent(new URL(url).pathname.split("/").filter(Boolean).pop() || ""); } catch { return ""; }
  }
  function kindFromUrl(url) {
    const ext = extFromUrl(url);
    if (ext === "m3u8") return "hls";
    if (ext === "mpd") return "dash";
    return "video";
  }

  // Addresses the player itself exposes (plain <video src> / <source>). blob: and data: URLs are not downloadable.
  function directSources(video) {
    const urls = [];
    const add = (value) => {
      if (!value) return;
      try {
        const absolute = new URL(value, location.href);
        if (absolute.protocol === "http:" || absolute.protocol === "https:") urls.push(absolute.href);
      } catch { }
    };
    if (video.currentSrc && /^https?:/i.test(video.currentSrc)) {
      add(video.currentSrc);
    } else {
      add(video.getAttribute("src"));
      for (const source of video.querySelectorAll("source")) add(source.src);
    }
    return [...new Set(urls)];
  }

  async function collectCandidates(video) {
    const reply = await send({ type: "SHIGAFLOW_GET_MEDIA" });
    const registry = reply?.ok ? (reply.media || []) : [];
    const direct = directSources(video);
    const candidates = direct.map((url) => registry.find((m) => m.url === url) ||
      { url, kind: kindFromUrl(url), type: "", size: null, ext: extFromUrl(url), name: nameFromUrl(url) });
    const directUsable = candidates.filter((c) => c.kind === "video" || c.kind === "audio");
    if (directUsable.length === 0) {
      // Players that load their video from a script (blob: URL): fall back to what the network showed.
      for (const entry of registry) if (!candidates.some((c) => c.url === entry.url)) candidates.push(entry);
    }
    // HLS / DASH playlists seen on the network, oldest first (the master playlist is requested before its variants).
    const manifests = registry.filter((m) => m.kind === "hls" || m.kind === "dash").sort((a, b) => (a.time || 0) - (b.time || 0));
    return { candidates, directUsable, manifests, streaming: Boolean(reply?.segmented) || manifests.length > 0 };
  }

  async function sendMedia(item) {
    const reply = await send({
      type: "SHIGAFLOW_DOWNLOAD_MEDIA",
      media: { url: item.url, kind: item.kind, type: item.type || "", ext: item.ext || "", name: item.name || "" }
    });
    if (reply?.ok) toast("Sent to Shigaflow. Choose where to save it in the Shigaflow window.");
    else toast(reply?.error || "Could not reach Shigaflow. Is it installed and running?", true);
  }

  // The page address of the video itself. On a feed (Facebook, Instagram...) the tab address is not the video,
  // so the closest link around the player that points at a video page is used instead.
  function findPermalink(video) {
    let node = video;
    for (let depth = 0; node && depth < 16; depth++) {
      node = node.parentElement || (node.getRootNode && node.getRootNode().host) || null;
      if (!node || node === document.body || node === document.documentElement) break;
      if (node.getAttribute && node.getAttribute("role") === "feed") break;
      const links = node.querySelectorAll ? node.querySelectorAll("a[href]") : [];
      for (let i = 0; i < links.length && i < 300; i++) {
        const href = links[i].href;
        if (href && /^https?:/i.test(href) && VIDEO_PAGE.test(href)) return href;
      }
    }
    return "";
  }

  function pageUrlsFor(video) {
    const urls = [];
    if (VIDEO_PAGE.test(location.href)) urls.push(location.href);
    else {
      const link = findPermalink(video);
      if (link) urls.push(link);
      urls.push(location.href);
    }
    return urls;
  }

  // Hand the video to Shigaflow's video engine (yt-dlp + FFmpeg): it understands YouTube, Facebook and the
  // other big sites, HLS (.m3u8) and DASH (.mpd) playlists, and joins separate video and audio streams.
  const QUALITIES = [
    { id: "best", name: "Best quality available", meta: "Highest resolution the video offers" },
    { id: "h2160", name: "2160p (4K)", meta: "Video + audio, up to this height" },
    { id: "h1440", name: "1440p (2K)", meta: "Video + audio, up to this height" },
    { id: "h1080", name: "1080p (Full HD)", meta: "Video + audio, up to this height" },
    { id: "h720", name: "720p (HD)", meta: "Video + audio, up to this height" },
    { id: "h480", name: "480p", meta: "Video + audio, up to this height" },
    { id: "h360", name: "360p", meta: "Video + audio, up to this height" },
    { id: "audio-mp3", name: "Audio only \u00b7 MP3", meta: "No video, converted to MP3" },
    { id: "audio-m4a", name: "Audio only \u00b7 M4A", meta: "No video, original quality" }
  ];

  // Dropdown shown when the button is clicked on a video the engine handles. If the video has no stream at
  // the chosen height, the best one below it is used.
  function showQualityMenu(video, manifests) {
    closeMenu();
    clearToast();
    menuEl = document.createElement("div");
    menuEl.className = "menu";
    const top = wrap.getBoundingClientRect().top;
    menuEl.style.maxHeight = `${Math.max(140, window.innerHeight - top - 50)}px`;
    menuEl.style.overflowY = "auto";
    for (const quality of QUALITIES) {
      const row = document.createElement("button");
      row.className = "row";
      row.type = "button";
      const name = document.createElement("span");
      name.className = "name";
      name.textContent = quality.name;
      const meta = document.createElement("span");
      meta.className = "meta";
      meta.textContent = quality.meta;
      row.append(name, meta);
      row.addEventListener("click", (event) => { event.stopPropagation(); closeMenu(); void sendStream(video, manifests, quality.id); });
      menuEl.appendChild(row);
    }
    wrap.appendChild(menuEl);
    menuOpen = true;
  }

  async function sendStream(video, manifests, quality) {
    const known = ENGINE_SITES.test(location.hostname);
    const pages = pageUrlsFor(video);
    const manifest = manifests.length ? [manifests[0].url] : [];
    const urls = known ? [...pages, ...manifest] : [...manifest, ...pages];
    const reply = await send({ type: "SHIGAFLOW_DOWNLOAD_STREAM", urls, title: document.title || "", quality: quality || "best" });
    if (reply?.ok) toast("Sent to Shigaflow. It is checking the video and will ask where to save it.");
    else toast(reply?.error || "Could not reach Shigaflow. Is it installed and running?", true);
  }

  async function onDownloadClick() {
    if (busy) return;
    const video = currentVideo;
    if (!video) return;
    busy = true;
    button.setAttribute("disabled", "");
    try {
      const { candidates, directUsable, manifests } = await collectCandidates(video);
      const usable = candidates.filter((c) => c.kind === "video" || c.kind === "audio");
      const engineSite = ENGINE_SITES.test(location.hostname);
      if (!engineSite && directUsable.length === 1) await sendMedia(directUsable[0]);
      else if (!engineSite && usable.length === 1) await sendMedia(usable[0]);
      else if (!engineSite && usable.length > 1) showMenu(usable);
      else showQualityMenu(video, manifests);
    } finally {
      busy = false;
      button.removeAttribute("disabled");
    }
  }

  button.addEventListener("click", (event) => {
    event.preventDefault();
    event.stopPropagation();
    if (menuOpen) { closeMenu(); return; }
    void onDownloadClick();
  });
})();
