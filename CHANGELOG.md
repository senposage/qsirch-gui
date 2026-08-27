# Changelog

## 0.8b - Test Build

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
- Added shared JSON result history and local cache with workstation and IP tagging.
- Searches show matching saved results first while checking the NAS for fresh results.
- Added current-workstation history clearing, with an option to also clear starred items.
- Added a Favorites panel for saved files and folders.
- Added star controls directly on result items; starred results remain available until explicitly cleared.
- Added double-click to open history, favorites, and normal search results.
- Added logging for search requests, results, cancellation, filtering, and paint activity to help diagnose search delays.

### File actions and previews

- Added Open to launch a result in its normal Windows application.
- Added Show to reveal a result in File Explorer.
- Added path mapping settings and automatic mapped-drive discovery.
- Added an optional preview pane with Windows shell-based previews when available.
- Added a preview-pane toggle and resizable panes for favorites, results, and preview content.

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

### Performance and reliability

- Improved result painting to show incoming results in small batches instead of waiting for a full result set.
- Added progressive Qsirch paging: a small recent page paints first, then later pages continue until no more results are returned.
- Reduced UI work during sorting, filtering, view changes, icon loading, and background tabs.
- Added cancellation handling to prevent stale searches from replacing newer results.
- Added self-signed certificate handling and HTTPS fallback behavior for QNAP connections.
- Added a single-instance guard so a second launch on the same computer warns and exits.

### Portable configuration and packaging

- Added per-workstation configuration under `hosts.<COMPUTERNAME>` for user settings, credentials, mappings, history preferences, behavior, and pinned tabs.
- Kept the root NAS address and HTTPS settings as shared deployment defaults; a workstation can override them independently.
- Added locked, merged config saves so one workstation does not overwrite another workstation's settings on a shared deployment.
- Reorganized portable output into separate `config`, `data`, `logs`, and `resources` folders.
- Updated the native build to produce a self-contained single executable without requiring a separate .NET installation.
- Added upstream attribution and donationware information to the About and project documentation.

### Test focus

- Exercise search tab creation, pinning, reopening, and first-focus refresh.
- Exercise results in all four view modes and with different sort/filter combinations.
- Test Open and Show against mapped drives and UNC paths.
- Test light, dark, and Follow Windows themes.
- Test Settings changes from more than one workstation using the shared configuration file.
- Test QNAP connections using both HTTP and HTTPS, including self-signed certificates.
