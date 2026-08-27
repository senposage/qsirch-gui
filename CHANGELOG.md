# Changelog

## 0.8b - Technical Test Build

### Platform and architecture

- Continued the migration from the PySide6 floating UI to the native `net9.0-windows` WPF application in `src\PyQsirchgui.Windows`.
- Retained compatibility with the Qsirch REST client and the portable JSON result-history format while moving the primary desktop UX to WPF.
- Added WPF resource-based theme palettes for light, dark, and Windows-following modes.
- Replaced browser-control preview experiments with Windows shell preview plumbing; no embedded web browser is required for normal preview operation.
- Added a named machine-wide Windows mutex, `Global\PyQsirchgui.SingleInstance`, to prevent concurrent local application instances.

### Explorer-style search experience

- Reworked the native Windows app around an Explorer-like results experience.
- Added Details, List, Small Icons, and Large Icons result views.
- Added Windows file and folder icons, with an optional Qsirch thumbnail mode.
- Added sortable Details columns for Location, Name, Date, Size, and Type.
- Added multi-column sorting with Ctrl+click on Details headers.
- Added a simple file-type filter with common Office formats first, followed by media, archives, and code files.
- Folders are shown before files in every result view.
- Added file location display for views that do not show a Location column.
- Long paths now wrap instead of running through the result layout.
- Added optional highlighting of the search text in result names and paths.

### Search, history, and tabs

- Added Explorer-style search tabs with individual query text, filters, sorting, view mode, status, and results.
- Added a browser-style new-tab button and close button on each tab.
- Added per-tab Stop controls for long-running searches.
- Added pinned tabs that persist per workstation and automatically rerun their saved search the first time they are selected after launch.
- Added a configurable option to keep results visible when search text is cleared; it is off by default.
- Added shared JSON result history and local cache with workstation name, machine/IP identity, last-used timestamp, use count, raw Qsirch item data, and starred state.
- Searches show matching saved results first while checking the NAS for fresh results.
- Added current-workstation history clearing, with an option to also clear starred items.
- Added a Favorites panel for saved files and folders.
- Added star controls directly on result items; starred results remain available until explicitly cleared.
- Added double-click to open history, favorites, and normal search results.
- Added rotating `logs\PyQsirchgui.log` logging for search requests, results, cancellation, filtering, result painting, configuration, and startup activity.

### Search pipeline and rendering

- Search state is isolated per `SearchTabState`, including cancellation token, search version, busy state, query, type filter, view key, sort keys, and result collections.
- Added search-version checks and cancellation-token propagation to prevent a canceled or older request from painting over a newer search.
- Added a local cache pass before the NAS pass. Cache hits are painted first, then Qsirch is queried for fresh results.
- Added first-page and subsequent-page limits in configuration. The initial request favors recent results and a fast first paint; later pages continue until Qsirch returns no additional results.
- Added batch result painting in groups of ten with dispatcher yields between batches to reduce UI stalls on large result sets.
- Added deferred/coalesced view refreshes and bulk observable collection updates to reduce repeated layout and collection notifications.
- Added asynchronous shell-icon loading with caching and a bounded concurrent icon-load gate.
- Added local sort/filter passes on background tasks, with folders-first ordering applied before user-selected sort keys.

### File actions and previews

- Added Open to launch a result in its normal Windows application.
- Added Show to reveal a result in File Explorer.
- Added path mapping settings and automatic mapped-drive discovery.
- Added an optional preview pane with Windows shell-based previews when available.
- Added a preview-pane toggle and resizable panes for favorites, results, and preview content.

### Qsirch transport and path resolution

- Added HTTP/HTTPS connection settings, certificate-verification control, self-signed certificate support, and a retry path for common HTTPS-port mismatches.
- Added filename-first searching by default; content searching is an explicit behavior setting.
- Added Qsirch thumbnail retrieval as an optional icon source, with Windows shell icons as the default fast path.
- Added configured share-root to mapped-root path mappings.
- Added `net use` parsing as a fallback to resolve mapped Windows drive letters automatically.
- Kept Open and Show as distinct shell actions: Open uses the Windows default handler, while Show selects the target in Explorer.

### Appearance and behavior

- Added light mode, dark mode, and Follow Windows appearance options.
- Improved dark-theme colors, selection visibility, controls, and dropdown rendering.
- Added taskbar visibility control and minimize-to-tray behavior.
- Added a configurable global hide/unhide shortcut with runtime registration feedback.
- Moved Always on top into Settings to reduce toolbar clutter.
- Added optional Always on top behavior.
- Added a compact version and author credit in the footer.
- Added a custom application icon.

### Rules, permissions, and safety

- Added wildcard folder and file exclusions, including safe QNAP/Qsync defaults.
- Added visibility rules for Windows and domain identities, with allow and deny behavior.
- Added Global checkboxes to make selected rules apply to every workstation.
- Preserved existing file-share permissions: search visibility rules do not grant access to files users cannot open.

### Configuration, persistence, and shared deployment behavior

- Kept `config.json` portable and human-readable with indented JSON serialization.
- The root configuration retains the shared deployment NAS endpoint: `host`, `port`, `ssl`, and `ssl_verify`.
- Machine-specific configuration is stored under `hosts.<UPPERCASE_MACHINE_NAME>` and includes credentials, path mappings, behavior, history preferences, local rules, always-on-top state, and pinned tabs.
- A machine can override the root NAS endpoint through Settings without modifying the shared deployment default.
- Root-level pinned tabs are migrated into the active host record on the next save, preventing tabs from leaking to another workstation.
- Exclusion and visibility rules marked `global` remain at the root. Rules without that flag are saved under the current host.
- Result history is intentionally shared at `data\history.json`; records are machine-tagged and writes use a lock file plus merge/deduplication to support multiple instances.
- Configuration writes now acquire `config.json.lock`, re-read the latest config while locked, merge the current host record, write a temporary file, and replace the shared config. This avoids one workstation discarding other host records during concurrent saves.
- Settings which write immediately from the main UI, including view mode, visible Details columns, content search, preview state, and pinned tabs, all use the same host-aware save path.

### Performance and reliability

- Improved result painting to show incoming results in small batches instead of waiting for a full result set.
- Added progressive Qsirch paging: a small recent page paints first, then later pages continue until no more results are returned.
- Reduced UI work during sorting, filtering, view changes, icon loading, and background tabs.
- Added cancellation handling to prevent stale searches from replacing newer results.
- Added self-signed certificate handling and HTTPS fallback behavior for QNAP connections.
- Added a single-instance guard so a second launch on the same computer warns and exits.

### Portable configuration and packaging

- Reorganized portable output into `config`, `data`, `logs`, and `resources` folders beside the application executable.
- Updated `native-build.bat` and the WPF project to publish a self-contained Windows x64 single-file executable with bundled native libraries and no generated PDB in Release output.
- The executable resolves `config\config.json` relative to the package root, with compatibility fallbacks for the older layout and source-tree execution.
- History and logs resolve from the portable root rather than the executable's extraction/runtime directory.
- Added upstream attribution and donationware information to the About and project documentation.

### Test focus

- Exercise search tab creation, pinning, reopening, and first-focus refresh.
- Exercise results in all four view modes and with different sort/filter combinations.
- Test Open and Show against mapped drives and UNC paths.
- Test light, dark, and Follow Windows themes.
- Test Settings changes from more than one workstation using the shared configuration file.
- Test QNAP connections using both HTTP and HTTPS, including self-signed certificates.
