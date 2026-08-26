from pathlib import Path
s = Path("qsirch_gui.py").read_text(encoding="utf-8")
required = [
    'APP_VERSION = "v10.4"',
    'COMPACT_HEIGHT = 132',
    'self.hint=QLabel(',
    'hasattr(self, "hint")',
    'self.tabs.addTab(conn, "Connection")',
    'self.tabs.addTab(behavior, "Appearance / Behavior")',
    'self.tabs.addTab(paths, "Path Mapping")',
    'self.tabs.addTab(excl, "Exclusions")',
    'self.tabs.addTab(hist, "History")',
    'class HistoryStore:',
    'class HotkeyManager:',
    'RegisterHotKey',
    '"show_in_taskbar"',
    'add_results',
    'import_machine_to_current',
    'def ssl_toggled',
    'display_path',
    'QSystemTrayIcon',
    'setSizeHint',
]
missing = [x for x in required if x not in s]
if missing:
    raise SystemExit("Missing required v10 source markers: " + repr(missing))
print("v10.4 source verification passed.")
