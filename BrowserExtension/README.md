# Shigaflow Browser Integration

This Chrome/Edge Manifest V3 extension sends supported download links to the locally installed Shigaflow app.

## Google Drive authenticated downloads (v1.0.5)

When you choose Download for a selected file in Google Drive, the content script identifies the selected Drive file ID. The service worker reads only Google-domain cookies using the extension's `cookies` permission and passes them over Chrome Native Messaging to the local Shigaflow app. The app uses a domain-scoped cookie container for Google download requests and redirects. Cookie values are not saved to Shigaflow settings, download history, or extension storage, and the app rejects cookie domains outside `google.com`.

The Drive request is intercepted before Chrome starts its own download. The app then shows Shigaflow's Save As dialog. Single-file downloads continue to use the direct authenticated Drive endpoint. For Drive Download actions, the extension arms a one-time capture for that Drive tab, lets Drive create its download (including server-generated ZIP archives), and intercepts the resulting Google-hosted download during Chrome's filename-determination stage. It does not rely on Drive's changing selected-row DOM to decide whether the action is a single file or an archive. It hands the archive URL and Google-domain cookies to Shigaflow before Chrome selects a save location. If Shigaflow does not acknowledge the handoff, Chrome is allowed to continue the download.

## Install / update

1. Open `chrome://extensions` (or `edge://extensions`).
2. Turn on Developer mode.
3. Click **Load unpacked** and select the installed `BrowserExtension` folder. If already installed, use **Reload** after replacing the extension files.
4. Ensure the native host is installed and its manifest allows this extension ID. If you re-run `Install-BrowserIntegration.ps1`, enter the same extension ID.
5. Completely restart the browser after changing extension permissions.

## Google Drive multi-file ZIP capture (v1.0.7)

When the Drive Download command is used, the extension does not try to construct a ZIP itself or guess a private Drive API endpoint. It lets Drive generate the archive, then watches for the resulting Google-hosted download in that same tab. During `downloads.onDeterminingFilename`, before Chrome chooses a destination, it passes the authenticated download URL and Google-domain cookies to Shigaflow. Chrome cancels its own download only after Shigaflow acknowledges the handoff. If capture fails, Chrome continues the download as a fallback.

This relies on Drive producing a normal HTTP(S) Google-hosted download item. If Google changes the bulk-download flow to a blob URL or a different browser mechanism, the extension will leave Chrome's download intact and report/fallback rather than silently discarding it.

## Limitations

Google Drive may require a confirmation page or special handling for some very large files, resource keys, or organization-managed accounts. Google can change its UI and download endpoints. Other sites retain the existing direct-link interception behavior.


## Magnet-link capture

Magnet links clicked on web pages are intercepted in the capture phase and forwarded through Chrome Native Messaging to Shigaflow. Shigaflow opens its Torrents section and asks the user to choose the destination folder before adding the magnet to the libtorrent engine. After replacing the extension files, click **Reload** on `chrome://extensions`; the existing extension ID and native host registration remain unchanged.


## v1.0.9 native-message compatibility fix

The Shigaflow native host now deserializes browser request JSON case-insensitively. This
ensures the extension's camelCase `googleDriveBulk`, `fileName`, and `cookies` fields
are read by the C# PascalCase request model. After updating the app and extension,
reload the unpacked extension from `chrome://extensions` before testing Drive bulk ZIPs.
