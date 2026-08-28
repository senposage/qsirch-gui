PyQsirchgui v1.0.0
Written by Robert J Crane

PyQsirchgui is a portable Windows search window for QNAP Qsirch. It is designed to behave like a familiar Explorer search: search, open a file with its normal Windows application, or show it in File Explorer.

INSTALLATION

Keep the entire PyQsirchgui folder together. Run PyQsirchgui.exe from that folder. The application is self-contained and does not require a separate .NET installation.

The first launch opens Settings so the Qsirch server and any required path mappings can be entered. Settings are stored in config\config.json beside the application. The shared root connection can be overridden for a specific computer without changing the deployment default.

USING RESULTS

- Double-click a result, or select Open, to open it normally in Windows.
- Select Show to open File Explorer with the result selected.
- The displayed path is the normal Windows drive or UNC path. Qsirch's internal NAS path stays out of the interface unless advanced path display is explicitly enabled.
- Use the file-type menu to choose one or more types. Clear the selection to search all types.
- Use Exact match for whole-word filename searches.
- Use the View and Arrange controls to choose the Explorer-style presentation and sorting you prefer.

FAVORITES AND SEARCHES

Use the star on a result to add it to Favorites. Add several selected results to a group from the right-click menu. Favorites and groups are private to the current Windows user. Saved searches can be run again from the Favorites pane. Pinned tabs are saved per computer and are refreshed when you return to them.

PREVIEWS

Preview uses the Windows preview handler already installed on the computer. Supported Office documents, PDFs, and similar files can be previewed when Windows has a compatible handler. Video previews are intentionally unavailable. Preview never downloads file content through Qsirch.

TRAY, TASKBAR, AND SHORTCUTS

Minimize hides the window to the notification area when the option is enabled. The tray icon restores the window on click and provides Settings and Exit. The global show/hide shortcut is configured in Settings and defaults to Ctrl+S. A second launch on the same computer focuses the existing instance.

PATH MAPPING AND RULES

Path Mapping connects a Qsirch share path to the drive letter or UNC path Windows uses. Map as deeply as needed; a mapping for a share folder covers folders beneath it. Rules hide results only; they never grant access. Wildcards are supported, for example @recycle\* hides items under that folder. A rule can be local, global, limited to a Windows user, or limited to a computer.

SAVED DATA AND LOGS

Portable connection and behavior settings live in config\config.json. Favorites, groups, saved searches, and recent searches live in a small local SQLite database under %LOCALAPPDATA%\PyQsirchgui\cache for the current Windows user. Logs are written under logs\:

- PyQsirchgui.sessions.log: application launches, exits, and active users
- PyQsirchgui.search.log: searches, filters, rules, and result rendering
- PyQsirchgui.client.log: Qsirch connection and API activity

SUPPORT

Use the visible Help button in the application for a guide to every control and setting. The Help window links to the current project page:
https://github.com/senposage/qsirch-gui/tree/main

PyQsirchgui acknowledges the upstream iios-co/qsirch project. See NOTICE and LICENSE for attribution and licensing.
