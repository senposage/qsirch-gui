# PyQsirchgui

PyQsirchgui is a portable Windows desktop search application for QNAP Qsirch. It gives staff an Explorer-style way to search shared files, open them with their normal Windows applications, reveal them in File Explorer, and keep useful files and searches close at hand.

> **Status:** v1.0.0 production release.

## Highlights

- Explorer-style Details, List, Small Icons, and Large Icons views
- Familiar folders-first results, file-type filtering, column sorting, and multi-column sorting
- **Open** with the default Windows application or **Show** in File Explorer
- Search tabs, pinned searches, Favorites, saved searches, and private favorite groups
- Windows shell previews for supported document types, with video previews deliberately disabled
- Light, dark, and Follow Windows appearance; optional taskbar presence, tray controls, and global show/hide shortcut
- Per-workstation settings and path mappings while retaining a shared NAS connection default
- Wildcard visibility rules that can apply to a specific user, computer, or every workstation
- Portable self-contained Windows deployment with no separate .NET installation

## Build and Run

Build the native Windows application:

```bat
native-build.bat
```

The portable package is created at:

```text
dist\PyQsirchgui\PyQsirchgui.exe
dist\PyQsirchgui\config\config.json
dist\PyQsirchgui\data\
dist\PyQsirchgui\logs\
dist\PyQsirchgui\resources\
```

Deploy the complete `dist\PyQsirchgui` folder to the shared location and create workstation shortcuts to `PyQsirchgui.exe`.

Portable connection and behavior settings are stored in `config\config.json`. Per-user saved data, including Favorites, groups, saved searches, and recent searches, is stored in a small local SQLite database under `%LOCALAPPDATA%\PyQsirchgui\cache`. This keeps shared-deployment startup quick and prevents one Windows user's saved items appearing for another.

The application permits one running instance per computer. Starting it again brings the existing window forward.

## Repository Contents

- `src\PyQsirchgui.Windows` - active WPF desktop application
- `native-build.bat` - builds the portable self-contained Windows package
- `config.json` - editable sample deployment configuration
- `qsirch.py` - upstream-compatible Qsirch CLI/API reference

## Logs

The package writes separate rolling logs under `logs\`:

- `PyQsirchgui.sessions.log` - application start, exit, and active Windows users
- `PyQsirchgui.search.log` - searches, filtering, result painting, and rules
- `PyQsirchgui.client.log` - Qsirch connection and API activity

## Upstream Qsirch Reference

The original Python Qsirch client remains in this repository as an upstream-compatible API reference. It is not part of the PyQsirchgui desktop package. Its dependencies are listed in `requirements.txt`.

PyQsirchgui acknowledges the upstream [iios-co/qsirch](https://github.com/iios-co/qsirch) project. See [NOTICE](NOTICE) and [LICENSE](LICENSE) for attribution and licensing details.

See [CHANGELOG.md](CHANGELOG.md) for technical release notes and `PyQsirchgui-README.txt` for deployment notes.
