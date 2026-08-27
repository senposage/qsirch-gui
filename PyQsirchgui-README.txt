PyQsirchgui v10

License and attribution:
- This GUI is part of a fork of the upstream Qsirch CLI/API project:
  https://github.com/iios-co/qsirch
- Upstream CLI/API implementation copyright (c) 2026 IIOS Pty Ltd.
- Upstream and GUI additions are distributed under the MIT License.
- The PayPal donation button in Settings > About is optional donationware support and does not change the license.

Build:
Run native-build.bat.

Native package layout (run native-build.bat):
  dist\PyQsirchgui\PyQsirchgui.exe    self-contained application and .NET runtime
  dist\PyQsirchgui\config\config.json
  dist\PyQsirchgui\data\history.json
  dist\PyQsirchgui\logs\
  dist\PyQsirchgui\resources\

Portable deployment:
- Put the whole PyQsirchgui folder on the share.
- Create workstation shortcuts to that EXE.
- Only one PyQsirchgui instance may run on a computer at a time; a second launch shows a warning and exits.
- Shared history defaults to data\history.json.
- History is enabled by default and stores saved result records with hostname, local IPv4, last-used time, and use count.
- The visible history filter defaults to This machine, so each workstation sees its own hostname's saved results first.
- Empty search shows saved results. Double-click a saved result to open it.
- Typed searches check saved result history first by result name/path before querying the NAS.
- Saved result history is also the shared local result cache. Cache hits are global across machines, then local visibility rules decide what this user sees.
- NAS pages are merged into the cache so repeated searches can paint cache hits before Qsirch is queried again.
- Shared cache writes use a small lock file beside history.json and fail through if another instance is writing.
- Settings > History can clear saved result history for only the current workstation.

Appearance / behavior:
- Settings has an Appearance / Behavior tab.
- Show in taskbar is enabled by default. Disable it for tray-only/tool-window behavior.
- The global hide/unhide shortcut defaults to Ctrl+Space and can be changed without restarting.
- If Windows reports the shortcut is already owned by another app, Settings shows a warning.
- The native app has a project icon under Assets\app.ico.
- Search tabs keep separate query text, result lists, view mode, sort, filter, and status.
- Pinned search tabs are stored under the current machine's host record and do not appear on other workstations.
- Result icons use fast Windows shell file/folder icons by default. Qsirch thumbnails can be enabled in Settings if richer icons are worth the extra NAS calls.
- Details columns can be shown/hidden from the header right-click menu.
- Normal header click sorts by one column. Ctrl+click a Details header to add/toggle another sort column, such as Name asc + Date desc.
- Portable settings are host-aware. The root NAS address, HTTPS settings, username, and password provide the shared deployment default; a machine can override them in Settings, which stores its connection, behavior, history settings, mappings, pinned tabs, and always-on-top state under hosts\<computer name>. Only rules marked Global apply to every host.
- Qsirch search requests a small first page, paints it, then keeps requesting later pages until Qsirch returns an empty page.
- The first page size, later page size, and timeout can be changed in Settings > Behavior.
- Stop cancels the active search and leaves already painted results in place. Opening a result also cancels the active search; preview does not.
- Result painting and sort/filter refreshes use bulk UI updates to reduce CPU spikes.
- Optional startup cache refresh is available in Settings > Behavior, but defaults off while CPU usage is being tuned.

HTTPS:
- For HTTPS, use the NAS HTTPS port.
- HTTPS certificate verification defaults off because many QNAP systems use self-signed certificates.
- If HTTPS is accidentally saved on the normal HTTP port, the app retries port 443 once and saves it if login succeeds.

Logging:
- The native Windows app writes a rotating debug log to logs\PyQsirchgui.log.
- Search logging records cache count, NAS response count, visibility-filter count, retry attempts, paint completion, and errors without logging passwords or session IDs.

Path mapping:
- No drive/share mapping is hard-coded.
- Add mappings in Settings > Path Mapping.
- Add mappings in Settings > Path Mapping. No sample server name or drive letter is shipped as a default.

Exclusions:
- Safe QNAP/Qsync defaults are included.
- Folder and file exclusions support wildcards.
- Folder wildcard patterns such as @recycle\* exclude anything under that folder.

Tray and window behavior:
- Close and minimize hide the app to the tray.
- Tray menu has Show Search, Settings, and Exit.
- The Pin button controls whether the floating window stays always on top.
- Drag the window from the background/status/hint area.

Startup speed and portability:
- The native build is a self-contained portable folder for Windows x64. It includes the .NET runtime beside the EXE, so target PCs do not need a separate .NET installation.
- It remains a folder build instead of a one-file EXE, avoiding unpack-on-launch delays.

Future TODO:
- Investigate how the native Qsirch PC app requests full-resolution previews. If that private API can be identified reliably, add it as a real preview provider rather than mixing guessed preview endpoints into the UI.
- Explore Explorer integration or shell extension packaging after the portable build stabilizes.
- Investigate SMB Multichannel benefits for mapped-drive file opening and any future SMB-side cache/enrichment layer. Qsirch search queries themselves use the NAS HTTP API, so SMB Multichannel does not directly accelerate the current query call.
