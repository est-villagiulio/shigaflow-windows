(() => {
  if (window.__shigaflowCaptureInstalled) return;
  window.__shigaflowCaptureInstalled = true;

  // Only capture instantly when the link is clearly a file. Anything ambiguous (for example a
  // "/download" landing page) is left alone: if it really turns into a download, Chrome reports it
  // and the service worker hands it to Shigaflow (see handleGenericDownload there).
  // pdf/txt/csv are deliberately NOT listed: Chrome shows them in the tab, so clicking one means "view".
  const FILE_EXTENSION = /\.(?:7z|aac|avi|bin|bz2|dmg|docx?|exe|flac|gz|iso|m4a|mkv|mov|mp3|mp4|mpeg|mpg|msi|pptx?|rar|tar|torrent|wav|webm|xlsx?|zip)$/i;
  const DIRECT_DOWNLOAD_QUERY = /[?&](?:export=download|download=1|dl=1)(?:&|$)/i;

  function isDirectDownloadHost(parsed) {
    const host = parsed.hostname.toLowerCase();
    if (host === "drive.usercontent.google.com") return parsed.pathname === "/download";
    if (/^download\d*\.mediafire\.com$/.test(host)) return true;
    return host === "dl.dropboxusercontent.com";
  }

  function sendToShigaflow(payload, source) {
    chrome.runtime.sendMessage({ type: "SHIGAFLOW_CAPTURE_LINK", payload }, (response) => {
      const error = chrome.runtime.lastError;
      if (error || !response?.ok) {
        const reason = error?.message || response?.error || "Shigaflow did not acknowledge the download.";
        console.error(`Shigaflow capture failed (${source}):`, reason);
        chrome.runtime.sendMessage({ type: "SHIGAFLOW_CAPTURE_ERROR", error: reason });
      }
    });
  }

  function validDriveId(value) {
    return typeof value === "string" && /^[A-Za-z0-9_-]{10,}$/.test(value) ? value : null;
  }

  function getDriveSelectedFileIds() {
    const ids = new Set();
    // Drive's selection semantics vary between list/grid layouts and account
    // rollouts. Some layouts expose aria-selected, while others expose the
    // checkbox state with aria-checked. Merge the signals and deduplicate IDs.
    const selectedNodes = document.querySelectorAll(
      '[aria-selected="true"], [data-selected="true"], [aria-checked="true"], [role="checkbox"][checked]'
    );
    for (const selected of selectedNodes) {
      const row = selected.matches('[data-id], [data-file-id]')
        ? selected
        : selected.closest('[data-id], [data-file-id]') || selected.querySelector('[data-id], [data-file-id]');
      const id = validDriveId(row?.getAttribute('data-id') || row?.getAttribute('data-file-id'));
      if (id) ids.add(id);
    }
    return [...ids];
  }

  function isDriveDownloadButton(target) {
    // Drive renders its menu items as role=menuitem (not always as buttons).
    // Search the nearest actionable element and its immediate ancestors because
    // the actual click target is often an SVG or nested span inside the menu row.
    const selector = 'button, [role="button"], [role="menuitem"], [role="menuitemcheckbox"], [aria-label], [data-tooltip], [title]';
    let control = target.closest(selector);
    for (let depth = 0; control && depth < 3; depth++, control = control.parentElement?.closest(selector) || null) {
      const aria = control.getAttribute("aria-label") || "";
      const tooltip = control.getAttribute("data-tooltip") || "";
      const title = control.getAttribute("title") || "";
      const text = (control.innerText || control.textContent || "").replace(/\s+/g, " ").trim();
      if (/\bdownload\b/i.test(aria) || /\bdownload\b/i.test(tooltip) || /\bdownload\b/i.test(title)) return true;
      if (/^download(?:\s|$)/i.test(text) && text.length < 100) return true;
    }
    return false;
  }

  function looksLikeDownload(anchor, url) {
    if (anchor.hasAttribute("download")) return true;
    let parsed;
    try { parsed = new URL(url); } catch { return false; }
    // Judge the path only, never the whole URL, so "?next=file.zip" or a ".zip" domain does not match.
    if (FILE_EXTENSION.test(parsed.pathname)) return true;
    if (DIRECT_DOWNLOAD_QUERY.test(parsed.search)) return true;
    return isDirectDownloadHost(parsed);
  }

  document.addEventListener("click", (event) => {
    if (event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
    if (!(event.target instanceof Element)) return;
    // Google Drive's Download command (single file, several files, or a folder) is a JavaScript
    // action, so its real download URL is only known once Drive makes Chrome start the download.
    // Do NOT block it: just tell the service worker to expect it. The service worker then catches
    // that download before Chrome picks a save path, hands it to Shigaflow with the Google
    // session cookies, and cancels Chrome's copy. Because the click is left alone, one code path
    // handles single files, zipped multi-file selections and zipped folders alike.
    if (/(?:^|\.)drive\.google\.com$/i.test(window.location.hostname) && isDriveDownloadButton(event.target)) {
      chrome.runtime.sendMessage({
        type: "SHIGAFLOW_EXPECT_DRIVE_DOWNLOAD",
        selectedCount: getDriveSelectedFileIds().length
      }, () => { void chrome.runtime.lastError; });
      return;
    }

    const anchor = event.target.closest("a[href]");
    if (!anchor || anchor.hasAttribute("data-shigaflow-bypass")) return;

    const rawHref = anchor.getAttribute("href") || "";
    if (!rawHref) return;

    // Magnet links use the same native-messaging handoff as ordinary captured downloads.
    // Prevent the browser's registered torrent handler from opening another application.
    if (/^magnet:\?/i.test(rawHref.trim())) {
      event.preventDefault();
      event.stopImmediatePropagation();
      sendToShigaflow({ url: rawHref.trim(), fileName: "" }, "magnet link");
      return;
    }

    // Resolve relative HTTP links against the current page, preserving existing capture behavior.
    const url = anchor.href;
    if (!url || !/^https?:\/\//i.test(url) || !looksLikeDownload(anchor, url)) return;

    // Stop the browser's native download before it can show its own Save As prompt.
    event.preventDefault();
    event.stopImmediatePropagation();
    sendToShigaflow({ url, fileName: anchor.getAttribute("download") || "" }, "download link");
  }, true);
})();
