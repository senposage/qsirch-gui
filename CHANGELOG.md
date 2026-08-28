# Changelog

## 1.0.1 - Reliability Update

- Fixed Copy full path when another Windows application temporarily owns the clipboard. PyQsirchgui now retries automatically and shows a clear status message only if the clipboard remains unavailable.
- Fixed Qsirch searches and optional thumbnail requests after a NAS login session expires. A 401 response now triggers one automatic sign-in and retry instead of leaving subsequent searches unauthorized.

## 1.0.0 - First Production Release

### Desktop experience

- Completed the migration to the native WPF desktop application and promoted it to the primary project branch.
- Delivered Explorer-style Details, List, Small Icons, and Large Icons result views with Windows shell icons, folders-first ordering, column visibility, multi-column sorting, folder grouping, and multi-select file-type filtering.
- Added search tabs with per-tab query, view, sorting, filter, result, stop, and pin state. Pinned tabs persist per machine and refresh when revisited.
- Added Favorites, user-private group folders, saved searches, recent searches, bulk favorite actions, and contextual removal/deletion actions.
- Added familiar Open and Show actions, right-click menus, exact filename matching, matching-parent folder rows, and optional collapsed folder results.
- Added a native Windows preview pane for compatible registered shell preview handlers. Qsirch is not used to download preview content; videos are excluded.

### Configuration and deployment

- Kept the application self-contained and portable for Windows x64 deployments without a separately installed .NET runtime.
- Moved portable settings to `config\config.json` and package assets into `config`, `data`, `logs`, and `resources` folders.
- Preserved a shared deployment NAS connection while storing overrides, behavior, mappings, pinned tabs, and non-global rules by computer.
- Moved user-private Favorites, groups, saved searches, and recent searches to a local SQLite database under `%LOCALAPPDATA%\PyQsirchgui\cache`.
- Retired the legacy PySide GUI prototype and its PyInstaller build path from the supported application surface. The upstream-compatible Python Qsirch CLI remains as a reference implementation.
- Added cleanup for stale self-contained .NET extraction folders after abnormal exits.

### Search, paths, and safety

- Added progressive Qsirch paging: a small initial request paints promptly, later pages continue until the server returns no more results, and visible-result limits pause further retrieval until Load more is chosen.
- Added cancellation/version safeguards, background filtering and sorting, deferred refreshes, bounded asynchronous shell icon loading, and hardened handling for malformed or duplicate Qsirch results.
- Added mapped-drive discovery and deeper configured path mappings. Interface actions now prefer the normal Windows drive path, fall back to UNC when necessary, and keep Qsirch internal paths out of ordinary UI.
- Added wildcard visibility rules, optional global rules, user/computer targeting, safe QNAP/Qsync default exclusions, and explicit assurance that rules never grant file access.
- Added HTTP/HTTPS settings, self-signed certificate control, a common HTTPS-port recovery path, and file-name-first searching with an opt-in content search.

### Appearance, help, and diagnostics

- Added light, dark, and Follow Windows themes with improved selection, hover, control, scrollbar, and dropdown contrast.
- Added optional taskbar presence, minimize-to-tray and exit-to-tray behavior, always-on-top, a configurable global show/hide shortcut, a working tray click restore action, and one-instance-per-machine focus behavior.
- Added visible in-app Help, current GitHub and donation links, and a compact version/author footer.
- Split diagnostics into session, search, and Qsirch client logs so operational activity is easier to inspect.

## 0.9.0 - Native Preview and Search Improvements

- Added shell-preview integration, progressively painted search results, first-page search tuning, native file icons, and stronger Explorer-style navigation.
- Introduced the WPF application while the earlier PySide interface was still retained for migration testing.

## 0.8b - Technical Test Build

- Introduced initial WPF themes, search tabs, favorites, path mapping, tray behavior, visibility rules, and portable deployment support.
