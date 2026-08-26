import sys, os, base64, json, webbrowser, fnmatch, subprocess, socket, uuid, tempfile, ctypes
from ctypes import wintypes
import xml.etree.ElementTree as ET
from pathlib import Path
from datetime import datetime
import requests

from PySide6.QtCore import Qt, QThread, Signal, QUrl, QSize, QTimer, QEvent
from PySide6.QtGui import QIcon, QDesktopServices, QKeySequence
from PySide6.QtWidgets import (
    QApplication, QWidget, QLineEdit, QListWidget, QListWidgetItem, QLabel,
    QVBoxLayout, QHBoxLayout, QPushButton, QFrame, QDialog, QFormLayout,
    QSpinBox, QCheckBox, QFileDialog, QMessageBox, QMenu, QAbstractItemView,
    QTabWidget, QTableWidget, QTableWidgetItem, QHeaderView, QGroupBox,
    QComboBox, QSystemTrayIcon, QStyle, QProgressBar
)

APP_NAME = "Qsirch Floating Search"
APP_VERSION = "v10.3"
COMPACT_HEIGHT = 132
BASE_DIR = Path(sys.executable).resolve().parent if getattr(sys, "frozen", False) else Path(__file__).resolve().parent
CONFIG = BASE_DIR / "config.json"

WM_HOTKEY = 0x0312
HOTKEY_ID = 0x5153
MOD_ALT = 0x0001
MOD_CONTROL = 0x0002
MOD_SHIFT = 0x0004
MOD_WIN = 0x0008
MOD_NOREPEAT = 0x4000

VK_NAMES = {
    "SPACE": 0x20,
    "TAB": 0x09,
    "ESC": 0x1B,
    "ESCAPE": 0x1B,
    "ENTER": 0x0D,
    "RETURN": 0x0D,
    "BACKSPACE": 0x08,
    "DELETE": 0x2E,
    "INSERT": 0x2D,
    "HOME": 0x24,
    "END": 0x23,
    "PAGEUP": 0x21,
    "PAGEDOWN": 0x22,
    "UP": 0x26,
    "DOWN": 0x28,
    "LEFT": 0x25,
    "RIGHT": 0x27,
}
for i in range(1, 25):
    VK_NAMES[f"F{i}"] = 0x70 + i - 1

class QsirchClient:
    def __init__(self, host, port=8080, ssl=False):
        self.base = f"{'https' if ssl else 'http'}://{host}:{port}"
        self.s = requests.Session()
        self.user = self.pw = None

    def login(self, user, pw):
        self.user, self.pw = user, pw
        b64 = base64.b64encode(pw.encode()).decode()
        try:
            r = self.s.post(self.base + "/cgi-bin/authLogin.cgi",
                            data={"user": user, "pwd": b64}, timeout=10)
        except requests.exceptions.SSLError as e:
            raise RuntimeError(self.ssl_error_message(e))
        r.raise_for_status()
        x = ET.fromstring(r.text)
        if x.findtext("authPassed") != "1":
            raise RuntimeError("QNAP authentication failed")
        sid = x.findtext("authSid")
        if not sid:
            raise RuntimeError("QNAP did not return an authSid")
        self.s.cookies.set("NAS_SID", sid)

    def request(self, method, path, **kw):
        try:
            r = self.s.request(method, self.base + path, timeout=15, **kw)
        except requests.exceptions.SSLError as e:
            raise RuntimeError(self.ssl_error_message(e))
        if r.status_code == 401:
            try:
                if r.json().get("error", {}).get("code") == 101:
                    self.login(self.user, self.pw)
                    try:
                        r = self.s.request(method, self.base + path, timeout=15, **kw)
                    except requests.exceptions.SSLError as e:
                        raise RuntimeError(self.ssl_error_message(e))
            except RuntimeError:
                raise
            except Exception:
                pass
        r.raise_for_status()
        return r

    def ssl_error_message(self, err):
        msg = str(err)
        hint = (
            "HTTPS failed during the SSL handshake. This usually means HTTPS is enabled "
            "in Settings but the selected port is serving plain HTTP. Uncheck HTTPS for "
            "the HTTP port, or keep HTTPS enabled and use the NAS HTTPS port."
        )
        if "WRONG_VERSION_NUMBER" in msg.upper() or "wrong version number" in msg.lower():
            return f"{hint}\n\nTechnical details: {msg}"
        return f"{hint}\n\nTechnical details: {msg}"

    @staticmethod
    def path(item):
        for x in item.get("preview", {}).get("info", []):
            if x.get("key") == "path":
                return x.get("value", "")
        return item.get("path", "")

    def search(self, q, limit=100, mode=0):
        p = {"q": q, "limit": limit, "offset": 0, "advanced_mode": str(mode)}
        r = self.request("GET", "/qsirch/latest/api/search", params=p)
        return r.json()

    def download(self, item, folder):
        action = item.get("actions", {}).get("download")
        if not action:
            raise RuntimeError("Qsirch did not provide a download action")
        r = self.request("GET", action, stream=True, timeout=60)
        name = item.get("name", "download")
        ext = item.get("extension", "")
        if ext and not name.lower().endswith("." + ext.lower()):
            name += "." + ext
        out = Path(folder) / name
        with out.open("wb") as f:
            for chunk in r.iter_content(1024 * 64):
                f.write(chunk)
        return out

    def preview(self, item):
        action = item.get("actions", {}).get("preview")
        if not action:
            raise RuntimeError("No preview action supplied")
        return self.request("GET", action, timeout=30).json()

class Worker(QThread):
    done = Signal(object)
    fail = Signal(str)
    def __init__(self, fn, *args):
        super().__init__()
        self.fn, self.args = fn, args
    def run(self):
        try: self.done.emit(self.fn(*self.args))
        except Exception as e: self.fail.emit(str(e))

def local_ipv4():
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.settimeout(0.2)
        s.connect(("8.8.8.8", 80))
        ip = s.getsockname()[0]
        s.close()
        if ip and not ip.startswith("127."):
            return ip
    except Exception:
        pass
    try:
        for ip in socket.gethostbyname_ex(socket.gethostname())[2]:
            if ip and not ip.startswith("127."):
                return ip
    except Exception:
        pass
    return ""

def history_defaults(cfg):
    raw = cfg.get("history", {}) or {}
    return {
        "enabled": bool(raw.get("enabled", True)),
        "file": raw.get("file") or "history.json",
        "max_entries": int(raw.get("max_entries", raw.get("maxEntries", 200)) or 200),
    }

def behavior_defaults(cfg):
    raw = cfg.get("behavior", {}) or {}
    return {
        "show_in_taskbar": bool(raw.get("show_in_taskbar", raw.get("showInTaskbar", True))),
        "global_hotkey": str(raw.get("global_hotkey", raw.get("globalHotkey", "Ctrl+Space")) or "Ctrl+Space"),
    }

def normalise_hotkey_text(text):
    parts = [p.strip() for p in str(text or "").replace("-", "+").split("+") if p.strip()]
    mods = []
    key = ""
    seen = set()
    aliases = {"CONTROL": "Ctrl", "CTRL": "Ctrl", "ALT": "Alt", "SHIFT": "Shift", "WIN": "Win", "WINDOWS": "Win", "META": "Win"}
    for part in parts:
        upper = part.upper()
        if upper in aliases:
            mod = aliases[upper]
            if mod not in seen:
                mods.append(mod)
                seen.add(mod)
        elif not key:
            key = part.upper() if len(part) == 1 else part[:1].upper() + part[1:].lower()
        else:
            return ""
    if not key or not mods:
        return ""
    if len(key) == 1 and key.isalnum():
        key = key.upper()
    else:
        lookup = key.upper().replace(" ", "")
        key = "Space" if lookup == "SPACE" else key
        key = "PageUp" if lookup == "PAGEUP" else key
        key = "PageDown" if lookup == "PAGEDOWN" else key
    return "+".join(mods + [key])

def parse_hotkey(text):
    normalised = normalise_hotkey_text(text)
    if not normalised:
        raise ValueError("Use a modifier plus one key, such as Ctrl+Space, Alt+Q, or Ctrl+Shift+F.")
    mods = 0
    key_code = None
    for part in normalised.split("+"):
        upper = part.upper()
        if upper == "CTRL":
            mods |= MOD_CONTROL
        elif upper == "ALT":
            mods |= MOD_ALT
        elif upper == "SHIFT":
            mods |= MOD_SHIFT
        elif upper == "WIN":
            mods |= MOD_WIN
        elif len(part) == 1 and part.isalnum():
            key_code = ord(part.upper())
        else:
            key_code = VK_NAMES.get(upper.replace(" ", ""))
    if not mods or not key_code:
        raise ValueError("Use a supported shortcut like Ctrl+Space, Alt+Q, or Ctrl+Shift+F.")
    return normalised, mods | MOD_NOREPEAT, key_code

def display_path(path, max_line=92):
    text = str(path or "")
    if len(text) <= max_line:
        return text
    parts = text.replace("/", "\\").split("\\")
    lines = []
    current = ""
    for part in parts:
        piece = part if not current else "\\" + part
        if current and len(current) + len(piece) > max_line:
            lines.append(current)
            current = part
        else:
            current += piece
    if current:
        lines.append(current)
    return "\n".join(lines)

class HistoryStore:
    def __init__(self, cfg):
        self.machine = socket.gethostname()
        self.ip = local_ipv4()
        self.machine_id = self._machine_id()
        self.configure(cfg)
        self.entries = []
        self.load()

    def _machine_id(self):
        seed = f"{socket.gethostname()}|{uuid.getnode()}"
        return str(uuid.uuid5(uuid.NAMESPACE_DNS, seed))

    def configure(self, cfg):
        self.settings = history_defaults(cfg)
        raw_file = Path(self.settings["file"])
        self.path = raw_file if raw_file.is_absolute() else BASE_DIR / raw_file
        self.max_entries = max(1, int(self.settings["max_entries"]))
        self.enabled = bool(self.settings["enabled"])

    def load(self):
        if not self.enabled:
            self.entries = []
            return
        try:
            data = json.loads(self.path.read_text(encoding="utf-8"))
            entries = data.get("results", []) if isinstance(data, dict) else []
        except Exception:
            entries = []
        self.entries = self._normalise(entries)

    def _normalise(self, entries):
        merged = {}
        for entry in entries or []:
            if not isinstance(entry, dict):
                continue
            item = entry.get("item") if isinstance(entry.get("item"), dict) else dict(entry)
            if item.get("_history"):
                item = item.get("item") if isinstance(item.get("item"), dict) else item
            path = str(entry.get("path") or QsirchClient.path(item) or "").strip()
            name = str(entry.get("name") or item.get("name") or "").strip()
            ext = str(entry.get("extension") or item.get("extension") or "").strip()
            if not path and not name:
                continue
            machine_id = str(entry.get("machineId") or entry.get("machine_id") or "").strip()
            machine = str(entry.get("machine") or "").strip()
            key = (path.casefold(), name.casefold(), machine_id or machine.casefold())
            current = dict(entry)
            current["item"] = item
            current["name"] = name
            current["extension"] = ext
            current["path"] = path
            current["machine"] = machine
            current["machineId"] = machine_id
            current["ip"] = str(entry.get("ip") or "").strip()
            current["lastUsed"] = str(entry.get("lastUsed") or entry.get("last_used") or "")
            current["uses"] = int(entry.get("uses", 1) or 1)
            old = merged.get(key)
            if not old or current["lastUsed"] >= old.get("lastUsed", ""):
                if old:
                    current["uses"] = max(current["uses"], int(old.get("uses", 1) or 1))
                merged[key] = current
        out = list(merged.values())
        out.sort(key=lambda x: x.get("lastUsed", ""), reverse=True)
        return out[:self.max_entries]

    def add_results(self, items):
        if not self.enabled:
            return

        now = datetime.now().isoformat(timespec="seconds")
        current_entries = []
        for item in items or []:
            if not isinstance(item, dict):
                continue
            path = QsirchClient.path(item)
            name = str(item.get("name", "") or "")
            ext = str(item.get("extension", "") or "")
            if not path and not name:
                continue
            current_entries.append({
                "name": name,
                "extension": ext,
                "path": path,
                "size": item.get("size", 0),
                "lastUsed": now,
                "machine": self.machine,
                "machineId": self.machine_id,
                "ip": self.ip,
                "uses": 1,
                "item": item,
            })
        if not current_entries:
            return

        try:
            data = json.loads(self.path.read_text(encoding="utf-8"))
            entries = data.get("results", []) if isinstance(data, dict) else []
        except Exception:
            entries = []

        incoming = {
            (
                str(x.get("path", "")).casefold(),
                str(x.get("name", "")).casefold(),
                self.machine_id,
            ): x
            for x in current_entries
        }
        merged = []
        for entry in entries:
            if not isinstance(entry, dict):
                continue
            item = entry.get("item") if isinstance(entry.get("item"), dict) else entry
            existing_path = str(entry.get("path") or QsirchClient.path(item) or "").strip()
            existing_name = str(entry.get("name") or item.get("name") or "").strip()
            existing_mid = str(entry.get("machineId") or entry.get("machine_id") or "").strip()
            existing_machine = str(entry.get("machine") or "").strip()
            existing_key = (existing_path.casefold(), existing_name.casefold(), existing_mid or existing_machine.casefold())
            if existing_key in incoming:
                current = incoming.pop(existing_key)
                current["uses"] = int(entry.get("uses", 0) or 0) + 1
                merged.append(current)
            else:
                merged.append(entry)
        merged.extend(incoming.values())

        self.entries = self._normalise(merged)
        self._write()

    def _write(self):
        self.path.parent.mkdir(parents=True, exist_ok=True)
        data = {"version": 2, "results": self.entries}
        fd, temp_name = tempfile.mkstemp(prefix=self.path.name + ".", suffix=".tmp", dir=str(self.path.parent))
        try:
            with os.fdopen(fd, "w", encoding="utf-8") as f:
                json.dump(data, f, indent=2)
            os.replace(temp_name, self.path)
        finally:
            try:
                if os.path.exists(temp_name):
                    os.remove(temp_name)
            except Exception:
                pass

    def filtered(self, mode):
        if not self.enabled:
            return []
        self.load()
        if mode == "__this__":
            return [x for x in self.entries if x.get("machine") == self.machine]
        if mode and mode not in ("__all__", "__this__"):
            return [x for x in self.entries if x.get("machine") == mode]
        return list(self.entries)

    def machines(self):
        self.load()
        names = sorted({x.get("machine") for x in self.entries if x.get("machine")})
        return names

    def clear(self):
        self.entries = []
        try:
            self._write()
        except Exception:
            pass

    def clear_current_machine(self):
        self.load()
        self.entries = [
            x for x in self.entries
            if x.get("machineId") != self.machine_id and x.get("machine") != self.machine
        ]
        try:
            self._write()
        except Exception:
            pass

    def import_machine_to_current(self, source_machine):
        source_machine = str(source_machine or "").strip()
        if not source_machine or source_machine == self.machine:
            return 0
        self.load()
        now = datetime.now().isoformat(timespec="seconds")
        imported = []
        for entry in self.entries:
            if entry.get("machine") != source_machine:
                continue
            clone = dict(entry)
            clone["machine"] = self.machine
            clone["machineId"] = self.machine_id
            clone["ip"] = self.ip
            clone["lastImported"] = now
            imported.append(clone)
        if not imported:
            return 0
        self.entries = self._normalise(self.entries + imported)
        self._write()
        return len(imported)

    def search_results(self, text, mode="__this__"):
        needle = str(text or "").strip().casefold()
        if not needle or not self.enabled:
            return []
        matches = []
        for entry in self.filtered(mode):
            item = entry.get("item") if isinstance(entry.get("item"), dict) else entry
            haystack = " ".join([
                str(entry.get("name") or item.get("name") or ""),
                str(entry.get("extension") or item.get("extension") or ""),
                str(entry.get("path") or QsirchClient.path(item) or ""),
            ]).casefold()
            if needle in haystack:
                matches.append(item)
        return matches

class ShortcutEdit(QLineEdit):
    def keyPressEvent(self, event):
        key = event.key()
        if key in (Qt.Key_Control, Qt.Key_Shift, Qt.Key_Alt, Qt.Key_Meta):
            return
        if key in (Qt.Key_Backspace, Qt.Key_Delete):
            self.clear()
            return
        parts = []
        mods = event.modifiers()
        if mods & Qt.ControlModifier:
            parts.append("Ctrl")
        if mods & Qt.AltModifier:
            parts.append("Alt")
        if mods & Qt.ShiftModifier:
            parts.append("Shift")
        if mods & Qt.MetaModifier:
            parts.append("Win")
        key_name = QKeySequence(key).toString(QKeySequence.NativeText)
        if key == Qt.Key_Space:
            key_name = "Space"
        if key_name:
            parts.append(key_name)
            self.setText(normalise_hotkey_text("+".join(parts)) or "+".join(parts))

class HotkeyManager:
    def __init__(self, window):
        self.window = window
        self.registered = False
        self.text = ""
        self.warning = ""

    def register(self, text):
        self.unregister()
        self.text = normalise_hotkey_text(text)
        self.warning = ""
        if sys.platform != "win32":
            self.warning = "Global shortcuts are only available on Windows."
            return False, self.warning
        try:
            normalised, modifiers, key_code = parse_hotkey(text)
        except ValueError as e:
            self.warning = str(e)
            return False, self.warning
        hwnd = int(self.window.winId())
        ok = ctypes.windll.user32.RegisterHotKey(wintypes.HWND(hwnd), HOTKEY_ID, modifiers, key_code)
        if not ok:
            self.warning = f"{normalised} could not be registered. Another app may already be using it."
            return False, self.warning
        self.registered = True
        self.text = normalised
        return True, ""

    def unregister(self):
        if self.registered and sys.platform == "win32":
            try:
                hwnd = int(self.window.winId())
                ctypes.windll.user32.UnregisterHotKey(wintypes.HWND(hwnd), HOTKEY_ID)
            except Exception:
                pass
        self.registered = False

class Settings(QDialog):
    def __init__(self, parent, cfg):
        super().__init__(parent)
        self.cfg = json.loads(json.dumps(cfg))
        self.setWindowTitle(f"Qsirch Floating Search Settings {APP_VERSION}")
        self.resize(720, 560)
        self.setStyleSheet("""
        QDialog, QWidget {
            background: #15171a;
            color: #e7e9eb;
        }
        QTabWidget::pane {
            border: 1px solid #30343a;
            background: #15171a;
        }
        QTabBar::tab {
            background: #22262b;
            color: #cfd3d8;
            padding: 9px 14px;
            border: 1px solid #30343a;
            border-bottom: none;
        }
        QTabBar::tab:selected {
            background: #2b3138;
            color: #ffffff;
        }
        QLineEdit, QSpinBox, QListWidget, QTableWidget, QComboBox {
            background: #101214;
            color: #f0f2f4;
            border: 1px solid #3a4048;
            border-radius: 6px;
            selection-background-color: #26364d;
        }
        QHeaderView::section {
            background: #22262b;
            color: #d8dbe0;
            border: 1px solid #30343a;
            padding: 6px;
        }
        QPushButton {
            background: #252a30;
            color: #e7e9eb;
            border: 1px solid #3a4048;
            border-radius: 7px;
            padding: 7px 12px;
        }
        QPushButton:hover {
            background: #30363d;
        }
        QGroupBox {
            border: 1px solid #30343a;
            border-radius: 8px;
            margin-top: 10px;
            padding-top: 10px;
        }
        QGroupBox::title {
            subcontrol-origin: margin;
            left: 10px;
            padding: 0 4px;
            color: #d7dbe0;
        }
        QCheckBox { color: #e7e9eb; }
        QLabel { color: #c7ccd2; }
        """)

        root = QVBoxLayout(self)
        self.tabs = QTabWidget()
        root.addWidget(self.tabs, 1)

        conn = QWidget()
        cf = QFormLayout(conn)
        self.host = QLineEdit(self.cfg.get("host",""))
        self.port = QSpinBox()
        self.port.setRange(1,65535)
        self.port.setValue(self.cfg.get("port",8080))
        self.user = QLineEdit(self.cfg.get("user",""))
        self.pw = QLineEdit(self.cfg.get("password",""))
        self.pw.setEchoMode(QLineEdit.Password)
        self.ssl = QCheckBox("Use HTTPS")
        self.ssl.setChecked(self.cfg.get("ssl",False))
        self.ssl_warning = QLabel("")
        self.ssl_warning.setWordWrap(True)
        self.ssl_warning.setStyleSheet("color: #ffcf7a;")
        self.ssl.stateChanged.connect(self.update_ssl_warning)
        self.port.valueChanged.connect(self.update_ssl_warning)
        cf.addRow("NAS host / IP", self.host)
        cf.addRow("Port", self.port)
        cf.addRow("Username", self.user)
        cf.addRow("Password", self.pw)
        cf.addRow("", self.ssl)
        cf.addRow("", self.ssl_warning)
        self.update_ssl_warning()
        self.tabs.addTab(conn, "Connection")

        behavior = QWidget()
        bf = QFormLayout(behavior)
        bcfg = behavior_defaults(self.cfg)
        self.show_taskbar = QCheckBox("Show the main window in the Windows taskbar")
        self.show_taskbar.setChecked(bcfg["show_in_taskbar"])
        self.hotkey_edit = ShortcutEdit(normalise_hotkey_text(bcfg["global_hotkey"]))
        self.hotkey_edit.setPlaceholderText("Ctrl+Space")
        self.hotkey_warning = QLabel("")
        self.hotkey_warning.setWordWrap(True)
        self.hotkey_warning.setStyleSheet("color: #ffcf7a;")
        if getattr(parent, "hotkey_warning", ""):
            self.hotkey_warning.setText(parent.hotkey_warning)
        bf.addRow("", self.show_taskbar)
        bf.addRow("Hide / unhide shortcut", self.hotkey_edit)
        bf.addRow("", self.hotkey_warning)
        self.tabs.addTab(behavior, "Appearance / Behavior")

        paths = QWidget()
        pv = QVBoxLayout(paths)
        help_lbl = QLabel(
            "Map a Qsirch result path or share root to the drive path used on this workstation."
        )
        help_lbl.setWordWrap(True)
        pv.addWidget(help_lbl)

        self.map_table = QTableWidget(0, 2)
        self.map_table.setHorizontalHeaderLabels(["Qsirch path / share root", "Mapped path"])
        self.map_table.horizontalHeader().setSectionResizeMode(0, QHeaderView.Stretch)
        self.map_table.horizontalHeader().setSectionResizeMode(1, QHeaderView.ResizeToContents)
        self.map_table.verticalHeader().setVisible(False)
        self.map_table.setSelectionBehavior(QAbstractItemView.SelectRows)
        self.map_table.setSelectionMode(QAbstractItemView.SingleSelection)
        pv.addWidget(self.map_table, 1)

        mb = QHBoxLayout()
        add_map = QPushButton("Add")
        edit_map = QPushButton("Edit")
        remove_map = QPushButton("Remove")
        add_map.clicked.connect(self.add_mapping)
        edit_map.clicked.connect(self.edit_mapping)
        remove_map.clicked.connect(self.remove_mapping)
        mb.addWidget(add_map)
        mb.addWidget(edit_map)
        mb.addWidget(remove_map)
        mb.addStretch()
        pv.addLayout(mb)

        for m in self.cfg.get("path_mappings", []) or []:
            source = m.get("share_root") or m.get("unc_prefix") or m.get("qsirch_prefix") or ""
            target = m.get("mapped_root","")
            self._append_mapping(source, target)
        self.tabs.addTab(paths, "Path Mapping")

        excl = QWidget()
        ev = QVBoxLayout(excl)

        folder_box = QGroupBox("Excluded folders")
        fv = QVBoxLayout(folder_box)
        self.folder_list = QListWidget()
        fv.addWidget(self.folder_list)
        fbtn = QHBoxLayout()
        fa = QPushButton("Add")
        fr = QPushButton("Remove")
        fa.clicked.connect(lambda: self.add_exclusion(self.folder_list, "Folder / path pattern"))
        fr.clicked.connect(lambda: self.remove_selected(self.folder_list))
        fbtn.addWidget(fa); fbtn.addWidget(fr); fbtn.addStretch()
        fv.addLayout(fbtn)
        ev.addWidget(folder_box, 1)

        file_box = QGroupBox("Excluded files")
        fiv = QVBoxLayout(file_box)
        self.file_list = QListWidget()
        fiv.addWidget(self.file_list)
        fibtn = QHBoxLayout()
        fia = QPushButton("Add")
        fir = QPushButton("Remove")
        fia.clicked.connect(lambda: self.add_exclusion(self.file_list, "File / wildcard pattern"))
        fir.clicked.connect(lambda: self.remove_selected(self.file_list))
        fibtn.addWidget(fia); fibtn.addWidget(fir); fibtn.addStretch()
        fiv.addLayout(fibtn)
        ev.addWidget(file_box, 1)

        for x in (self.cfg.get("exclude", {}) or {}).get("folders", []) or []:
            self.folder_list.addItem(x)
        for x in (self.cfg.get("exclude", {}) or {}).get("files", []) or []:
            self.file_list.addItem(x)
        self.tabs.addTab(excl, "Exclusions")

        hist = QWidget()
        hf = QFormLayout(hist)
        hcfg = history_defaults(self.cfg)
        self.history_enabled = QCheckBox("Keep shared result history")
        self.history_enabled.setChecked(hcfg["enabled"])
        self.history_file = QLineEdit(hcfg["file"])
        self.history_max = QSpinBox()
        self.history_max.setRange(1, 5000)
        self.history_max.setValue(hcfg["max_entries"])
        self.clear_history = QCheckBox("Clear this machine's history when saving")
        clear_this_machine = QPushButton("Clear This Machine's History")
        clear_this_machine.clicked.connect(self.clear_current_machine_history)
        import_machine = QPushButton("Import Another Machine's History")
        import_machine.clicked.connect(self.import_machine_history)
        hf.addRow("", self.history_enabled)
        hf.addRow("History file", self.history_file)
        hf.addRow("Maximum entries", self.history_max)
        hf.addRow("", self.clear_history)
        hf.addRow("", clear_this_machine)
        hf.addRow("", import_machine)
        self.tabs.addTab(hist, "History")

        buttons = QHBoxLayout()
        buttons.addStretch()
        cancel = QPushButton("Cancel")
        save = QPushButton("Save")
        cancel.clicked.connect(self.reject)
        save.clicked.connect(self.accept)
        buttons.addWidget(cancel)
        buttons.addWidget(save)
        root.addLayout(buttons)

    def _append_mapping(self, source, target):
        row = self.map_table.rowCount()
        self.map_table.insertRow(row)
        self.map_table.setItem(row, 0, QTableWidgetItem(source))
        self.map_table.setItem(row, 1, QTableWidgetItem(target))

    def update_ssl_warning(self):
        if self.ssl.isChecked() and self.port.value() == 8080:
            self.ssl_warning.setText("HTTPS is enabled on port 8080. If the NAS uses 8080 for HTTP, either uncheck HTTPS or choose the NAS HTTPS port.")
        else:
            self.ssl_warning.clear()

    def _mapping_dialog(self, source="", target=""):
        d = QDialog(self)
        d.setStyleSheet(self.styleSheet())
        d.setWindowTitle("Path Mapping")
        f = QFormLayout(d)
        src = QLineEdit(source)
        src.setPlaceholderText("Qsirch path prefix or share root")
        dst = QLineEdit(target)
        dst.setPlaceholderText("Mapped drive or folder path")
        f.addRow("Qsirch path / share root", src)
        f.addRow("Mapped path", dst)
        b = QHBoxLayout()
        b.addStretch()
        c = QPushButton("Cancel")
        o = QPushButton("OK")
        c.clicked.connect(d.reject)
        o.clicked.connect(d.accept)
        b.addWidget(c); b.addWidget(o)
        f.addRow("", b)
        if d.exec():
            return src.text().strip(), dst.text().strip()
        return None

    def add_mapping(self):
        result = self._mapping_dialog()
        if result:
            self._append_mapping(*result)

    def edit_mapping(self):
        row = self.map_table.currentRow()
        if row < 0:
            return
        source = self.map_table.item(row, 0).text() if self.map_table.item(row,0) else ""
        target = self.map_table.item(row, 1).text() if self.map_table.item(row,1) else ""
        result = self._mapping_dialog(source, target)
        if result:
            self.map_table.setItem(row, 0, QTableWidgetItem(result[0]))
            self.map_table.setItem(row, 1, QTableWidgetItem(result[1]))

    def remove_mapping(self):
        row = self.map_table.currentRow()
        if row >= 0:
            self.map_table.removeRow(row)

    def add_exclusion(self, widget, title):
        d = QDialog(self)
        d.setStyleSheet(self.styleSheet())
        d.setWindowTitle(title)
        f = QFormLayout(d)
        edit = QLineEdit()
        f.addRow(title, edit)
        b = QHBoxLayout()
        b.addStretch()
        c = QPushButton("Cancel")
        o = QPushButton("Add")
        c.clicked.connect(d.reject)
        o.clicked.connect(d.accept)
        b.addWidget(c); b.addWidget(o)
        f.addRow("", b)
        if d.exec() and edit.text().strip():
            widget.addItem(edit.text().strip())

    def clear_current_machine_history(self):
        parent = self.parent()
        if parent and hasattr(parent, "history"):
            parent.history.clear_current_machine()
            parent.refresh_history_view()
            QMessageBox.information(
                self,
                "History cleared",
                "Saved result history for this workstation was cleared."
            )

    def import_machine_history(self):
        parent = self.parent()
        if not parent or not hasattr(parent, "history"):
            return
        machines = [m for m in parent.history.machines() if m != parent.history.machine]
        if not machines:
            QMessageBox.information(self, "No other history", "No saved result history from another workstation was found.")
            return
        d = QDialog(self)
        d.setStyleSheet(self.styleSheet())
        d.setWindowTitle("Import Machine History")
        f = QFormLayout(d)
        picker = QComboBox()
        picker.addItems(machines)
        note = QLabel("Copies the selected workstation's saved results into this workstation's history. The original entries are kept.")
        note.setWordWrap(True)
        f.addRow("Import from", picker)
        f.addRow("", note)
        b = QHBoxLayout()
        b.addStretch()
        cancel = QPushButton("Cancel")
        import_btn = QPushButton("Import")
        cancel.clicked.connect(d.reject)
        import_btn.clicked.connect(d.accept)
        b.addWidget(cancel)
        b.addWidget(import_btn)
        f.addRow("", b)
        if d.exec():
            source = picker.currentText()
            count = parent.history.import_machine_to_current(source)
            parent.refresh_history_view()
            QMessageBox.information(self, "History imported", f"Imported {count:,} saved result entries from {source}.")

    @staticmethod
    def remove_selected(widget):
        row = widget.currentRow()
        if row >= 0:
            widget.takeItem(row)

    def values(self):
        try:
            hotkey_text, _, _ = parse_hotkey(self.hotkey_edit.text())
            self.hotkey_warning.clear()
        except ValueError as e:
            self.hotkey_warning.setText(str(e))
            return None

        mappings = []
        for row in range(self.map_table.rowCount()):
            source = self.map_table.item(row, 0)
            target = self.map_table.item(row, 1)
            source = source.text().strip() if source else ""
            target = target.text().strip() if target else ""
            if source and target:
                mappings.append({"share_root": source, "mapped_root": target})

        folders = [self.folder_list.item(i).text() for i in range(self.folder_list.count())]
        files = [self.file_list.item(i).text() for i in range(self.file_list.count())]

        return {
            "host": self.host.text().strip(),
            "port": self.port.value(),
            "user": self.user.text(),
            "password": self.pw.text(),
            "ssl": self.ssl.isChecked(),
            "path_mappings": mappings,
            "exclude": {"folders": folders, "files": files},
            "behavior": {
                "show_in_taskbar": self.show_taskbar.isChecked(),
                "global_hotkey": hotkey_text,
            },
            "history": {
                "enabled": self.history_enabled.isChecked(),
                "file": self.history_file.text().strip() or "history.json",
                "max_entries": self.history_max.value(),
                "clear_on_save": self.clear_history.isChecked(),
            },
        }

    def accept(self):
        if self.values() is None:
            return
        super().accept()

class Main(QWidget):
    def __init__(self):
        super().__init__()
        self.cfg = self.load()
        self.client = None
        self.worker = None
        self.results = []
        self.history = HistoryStore(self.cfg)
        self.exiting = False
        self.refreshing_history = False
        self.has_visible_content = False
        self.dragging = False
        self.drag_offset = None
        self.pinned = bool(self.cfg.get("always_on_top", True))
        self.behavior = behavior_defaults(self.cfg)
        self.show_in_taskbar = bool(self.behavior["show_in_taskbar"])
        self.hotkey_manager = HotkeyManager(self)
        self.hotkey_warning = ""
        self.apply_window_flags()
        self.setAttribute(Qt.WA_TranslucentBackground)
        self.resize(820, 560)
        self.build()
        self.apply_style()
        self.setup_tray()
        self.register_global_hotkey()
        QTimer.singleShot(0, self.refresh_history_view)
        self.update_compact_state()
        self.search.setFocus()

    def load(self):
        try: return json.loads(CONFIG.read_text())
        except Exception:
            return {
                "host":"",
                "port":8080,
                "user":"",
                "password":"",
                "ssl":False,
                "path_mappings": [],
                "exclude": {
                    "folders": [
                        "@Recently-Snapshot",
                        "@Recently-Snapshot\\*",
                        "@Recycle",
                        "@Recycle\\*",
                        "@recycle",
                        "@recycle\\*",
                        "#recycle",
                        "#recycle\\*",
                        ".sync",
                        ".sync\\*",
                        ".qsync",
                        ".qsync\\*",
                        ".qsync_sn",
                        ".qsync_sn\\*"
                    ],
                    "files": [
                        "Thumbs.db",
                        "desktop.ini",
                        "*.tmp",
                        "~$*",
                        ".DS_Store",
                        "*.qsync",
                        "*.qsync_tmp",
                        "*.syncing",
                        "*_conflict_*",
                        "*conflicted copy*"
                    ]
                },
                "history": {"enabled": True, "file": "history.json", "max_entries": 200},
                "behavior": {"show_in_taskbar": True, "global_hotkey": "Ctrl+Space"},
                "always_on_top": True
            }

    def save(self):
        CONFIG.parent.mkdir(parents=True, exist_ok=True)
        CONFIG.write_text(json.dumps(self.cfg, indent=2))

    def build(self):
        outer=QVBoxLayout(self); outer.setContentsMargins(10,10,10,10)
        card=QFrame(); card.setObjectName("card"); self.card = card; outer.addWidget(card)
        card.installEventFilter(self)
        v=QVBoxLayout(card); v.setContentsMargins(14,14,14,12); v.setSpacing(8)

        top=QHBoxLayout()
        top.setSpacing(8)
        self.search=QLineEdit(); self.search.setPlaceholderText("Search Qsirch")
        self.search.setMinimumHeight(40)
        self.search.setClearButtonEnabled(False)
        self.search.returnPressed.connect(self.do_search)
        self.search.textChanged.connect(self.query_changed)
        self.search.textChanged.connect(lambda _: self.clear_btn.setEnabled(bool(self.search.text())))
        top.addWidget(self.search,1)

        self.clear_btn=QPushButton("Clear")
        self.clear_btn.setObjectName("toolButton")
        self.clear_btn.setToolTip("Clear search")
        self.clear_btn.setFixedWidth(58)
        self.clear_btn.setMinimumHeight(36)
        self.clear_btn.setEnabled(False)
        self.clear_btn.clicked.connect(self.clear_search)
        top.addWidget(self.clear_btn)

        self.version_label = QLabel(APP_VERSION)
        self.version_label.setObjectName("versionBadge")
        self.version_label.setToolTip("Running build version")
        self.version_label.installEventFilter(self)

        self.pin_btn=QPushButton("Pinned")
        self.pin_btn.setObjectName("pinButton")
        self.pin_btn.setToolTip("Keep the search window always on top")
        self.pin_btn.setCheckable(True)
        self.pin_btn.setChecked(self.pinned)
        self.pin_btn.setFixedWidth(72)
        self.pin_btn.setMinimumHeight(36)
        self.pin_btn.clicked.connect(self.toggle_pin)
        top.addWidget(self.pin_btn)
        self.update_pin_button()

        self.gear=QPushButton("Settings")
        self.gear.setObjectName("toolButton")
        self.gear.setToolTip("Qsirch connection settings")
        self.gear.setFixedWidth(78)
        self.gear.setMinimumHeight(36)
        self.gear.clicked.connect(self.settings)
        top.addWidget(self.gear)

        self.hide_btn=QPushButton("Hide")
        self.hide_btn.setObjectName("toolButton")
        self.hide_btn.setToolTip("Hide to tray")
        self.hide_btn.setFixedWidth(56)
        self.hide_btn.setMinimumHeight(36)
        self.hide_btn.clicked.connect(self.hide)
        top.addWidget(self.hide_btn)

        self.exit_btn=QPushButton("Exit")
        self.exit_btn.setObjectName("exitButton")
        self.exit_btn.setToolTip("Exit Qsirch Floating Search")
        self.exit_btn.setFixedWidth(52)
        self.exit_btn.setMinimumHeight(36)
        self.exit_btn.clicked.connect(self.quit_app)
        top.addWidget(self.exit_btn)
        v.addLayout(top)

        self.status_bar_widget = QWidget()
        bar=QHBoxLayout(self.status_bar_widget)
        bar.setContentsMargins(0,0,0,0)
        self.status=QLabel("Ready")
        self.status.installEventFilter(self)
        self.count=QLabel("")
        self.count.installEventFilter(self)
        self.history_filter = QComboBox()
        self.history_filter.setToolTip("Filter search history")
        self.history_filter.setMinimumWidth(160)
        self.history_filter.currentIndexChanged.connect(self.refresh_history_view)
        bar.addWidget(self.status)
        bar.addStretch()
        bar.addWidget(self.count)
        bar.addWidget(self.history_filter)
        v.addWidget(self.status_bar_widget)

        self.list=QListWidget()
        self.list.setSelectionMode(QAbstractItemView.SingleSelection)
        self.list.itemDoubleClicked.connect(self.open_item)
        self.list.setContextMenuPolicy(Qt.CustomContextMenu)
        self.list.customContextMenuRequested.connect(self.menu)
        v.addWidget(self.list,1)

        bottom=QHBoxLayout()
        bottom.addWidget(self.version_label)
        self.hint=QLabel("Enter to search  •  Double-click/Open opens file  •  Show opens Explorer  •  Esc to hide")
        self.hint.setObjectName("hint")
        self.hint.installEventFilter(self)
        bottom.addWidget(self.hint)
        bottom.addStretch()
        v.addLayout(bottom)

        self.busy = QProgressBar()
        self.busy.setRange(0, 0)
        self.busy.setFixedHeight(4)
        self.busy.setTextVisible(False)
        self.busy.hide()
        v.addWidget(self.busy)

    def apply_style(self):
        self.setStyleSheet("""
        #card { background:#202124; border:1px solid #3b3d42; border-radius:12px; }
        QLineEdit {
            background:#2b2c30; color:#f5f6f7; border:1px solid #4a4d54;
            border-radius:8px; padding:9px 12px; font-size:15px;
        }
        QLineEdit:focus { border:1px solid #76a9ff; background:#303238; }
        QComboBox {
            background:#2b2c30; color:#e9eaec; border:1px solid #4a4d54;
            border-radius:7px; padding:5px 8px; font-size:12px;
        }
        QPushButton {
            background:#2f3136; color:#eceef1; border:1px solid #4a4d54;
            border-radius:7px; padding:7px 10px; font-size:12px;
        }
        QPushButton:hover { background:#393c42; }
        QPushButton:pressed { background:#24262a; }
        QPushButton:disabled { color:#777c84; background:#282a2e; }
        QPushButton#pinButton:checked { background:#28456f; border-color:#5f91d6; color:#ffffff; }
        QPushButton#exitButton:hover { background:#7a3030; border-color:#985151; }
        QLabel#versionBadge {
            color:#aeb4bb; background:#282a2e; border:1px solid #3f4248;
            border-radius:7px; padding:6px 9px; font-size:12px;
        }
        QListWidget {
            background:#191a1d; color:#e7e9eb; border:1px solid #303238;
            border-radius:8px; font-size:13px; padding:4px;
        }
        QListWidget::item { padding:0; border-bottom:1px solid #282a2e; }
        QListWidget::item:selected { background:#26364d; border-radius:6px; }
        QLabel { color:#bcc2ca; }
        #hint { color:#858b94; font-size:12px; }
        """)

    def apply_window_flags(self):
        flags = Qt.Window | Qt.FramelessWindowHint
        if not getattr(self, "show_in_taskbar", True):
            flags = Qt.Tool | Qt.FramelessWindowHint
        if self.pinned:
            flags |= Qt.WindowStaysOnTopHint
        self.setWindowFlags(flags)

    def apply_behavior(self):
        bcfg = behavior_defaults(self.cfg)
        was_visible = self.isVisible()
        taskbar_changed = self.show_in_taskbar != bcfg["show_in_taskbar"]
        self.behavior = bcfg
        self.show_in_taskbar = bool(bcfg["show_in_taskbar"])
        if taskbar_changed:
            self.apply_window_flags()
            if was_visible:
                self.show()
                self.raise_()
                self.activateWindow()
        self.register_global_hotkey()

    def register_global_hotkey(self):
        ok, warning = self.hotkey_manager.register(behavior_defaults(self.cfg)["global_hotkey"])
        self.hotkey_warning = warning
        if warning:
            self.status.setText(warning)
        return ok

    def nativeEvent(self, eventType, message):
        if sys.platform == "win32":
            msg = wintypes.MSG.from_address(int(message))
            if msg.message == WM_HOTKEY and msg.wParam == HOTKEY_ID:
                self.toggle_visibility()
                return True, 0
        return super().nativeEvent(eventType, message)

    def toggle_visibility(self):
        if self.isVisible() and self.isActiveWindow():
            self.hide()
        else:
            self.activate()

    def update_pin_button(self):
        if not hasattr(self, "pin_btn"):
            return
        self.pin_btn.setChecked(self.pinned)
        self.pin_btn.setText("Pinned" if self.pinned else "Pin")

    def toggle_pin(self):
        self.pinned = bool(self.pin_btn.isChecked())
        self.cfg["always_on_top"] = self.pinned
        self.save()
        was_visible = self.isVisible()
        self.apply_window_flags()
        self.update_pin_button()
        if was_visible:
            self.show()
            self.raise_()
            self.activateWindow()

    @staticmethod
    def event_global_pos(event):
        if hasattr(event, "globalPosition"):
            return event.globalPosition().toPoint()
        return event.globalPos()

    def eventFilter(self, obj, event):
        draggable = obj in (
            getattr(self, "card", None),
            getattr(self, "version_label", None),
            getattr(self, "status", None),
            getattr(self, "count", None),
            getattr(self, "hint", None),
        )
        if draggable and event.type() == QEvent.Type.MouseButtonPress and event.button() == Qt.LeftButton:
            self.dragging = True
            self.drag_offset = self.event_global_pos(event) - self.frameGeometry().topLeft()
            return True
        if draggable and event.type() == QEvent.Type.MouseMove and self.dragging and event.buttons() & Qt.LeftButton:
            self.move(self.event_global_pos(event) - self.drag_offset)
            return True
        if event.type() == QEvent.Type.MouseButtonRelease:
            self.dragging = False
            self.drag_offset = None
        return super().eventFilter(obj, event)

    def setup_tray(self):
        icon = self.style().standardIcon(QStyle.StandardPixmap.SP_FileDialogContentsView)
        self.setWindowIcon(icon)
        self.tray = QSystemTrayIcon(icon, self)
        self.tray.setToolTip(APP_NAME)
        menu = QMenu()
        show_action = menu.addAction("Show Search")
        settings_action = menu.addAction("Settings")
        menu.addSeparator()
        exit_action = menu.addAction("Exit")
        show_action.triggered.connect(self.activate)
        settings_action.triggered.connect(self.settings)
        exit_action.triggered.connect(self.quit_app)
        self.tray.setContextMenu(menu)
        self.tray.activated.connect(self.tray_activated)
        self.tray.show()

    def tray_activated(self, reason):
        if reason in (QSystemTrayIcon.Trigger, QSystemTrayIcon.DoubleClick):
            self.activate()

    def changeEvent(self, event):
        if event.type() == QEvent.Type.WindowStateChange and self.isMinimized():
            QTimer.singleShot(0, self.hide)
        super().changeEvent(event)

    def closeEvent(self, event):
        if self.exiting:
            event.accept()
            return
        event.ignore()
        self.hide()
        if hasattr(self, "tray") and self.tray.isVisible():
            self.tray.showMessage(APP_NAME, "Still running in the tray.", QSystemTrayIcon.Information, 1800)

    def quit_app(self):
        self.exiting = True
        if hasattr(self, "hotkey_manager"):
            self.hotkey_manager.unregister()
        if hasattr(self, "tray"):
            self.tray.hide()
        QApplication.instance().quit()

    def has_history_to_show(self):
        return bool(getattr(self, "history", None) and self.history.filtered(self.current_history_filter()))

    def current_history_filter(self):
        if not hasattr(self, "history_filter"):
            return "__this__"
        if self.history_filter.count() == 0:
            return "__this__"
        return self.history_filter.currentData() or "__this__"

    def update_history_filter_choices(self):
        if not hasattr(self, "history_filter"):
            return
        current = self.current_history_filter()
        self.history_filter.blockSignals(True)
        self.history_filter.clear()
        self.history_filter.addItem("This machine", "__this__")
        self.history_filter.addItem("All history", "__all__")
        for machine in self.history.machines():
            self.history_filter.addItem(machine, machine)
        idx = self.history_filter.findData(current)
        self.history_filter.setCurrentIndex(idx if idx >= 0 else 0)
        self.history_filter.blockSignals(False)

    def add_sized_row(self, item, row, minimum_height=58):
        row.setMinimumHeight(minimum_height)
        row.adjustSize()
        height = max(minimum_height, row.sizeHint().height())
        item.setSizeHint(QSize(0, height))
        self.list.addItem(item)
        self.list.setItemWidget(item, row)

    def refresh_history_view(self, *args):
        if self.refreshing_history or not hasattr(self, "list") or self.search.text().strip():
            return
        self.refreshing_history = True
        try:
            self.update_history_filter_choices()
            entries = self.history.filtered(self.current_history_filter())
            self.list.clear()
            for entry in entries:
                item = entry.get("item") if isinstance(entry.get("item"), dict) else entry
                name = entry.get("name") or item.get("name", "")
                ext = entry.get("extension") or item.get("extension", "")
                if ext and name and not str(name).lower().endswith("." + str(ext).lower()):
                    name = f"{name}.{ext}"
                path = entry.get("path") or QsirchClient.path(item)
                machine = entry.get("machine", "")
                ip = entry.get("ip", "")
                used = entry.get("lastUsed", "")
                meta_parts = [x for x in (machine, ip, used) if x]
                meta = display_path(path) + ("\n" + "  |  ".join(meta_parts) if meta_parts else "")
                li = QListWidgetItem()
                li.setData(Qt.UserRole, {"_history": True, "item": item})
                row = QWidget()
                rh = QVBoxLayout(row)
                rh.setContentsMargins(10, 7, 10, 7)
                rh.setSpacing(3)
                title = QLabel(str(name or path or "Saved result"))
                title.setTextInteractionFlags(Qt.TextSelectableByMouse)
                detail = QLabel(meta)
                detail.setObjectName("hint")
                detail.setTextInteractionFlags(Qt.TextSelectableByMouse)
                detail.setWordWrap(True)
                rh.addWidget(title)
                rh.addWidget(detail)
                self.add_sized_row(li, row, 54)
            if entries:
                self.status.setText("Saved results")
                self.count.setText(f"{len(entries):,} saved")
            else:
                self.status.setText("Ready")
                self.count.clear()
        finally:
            self.refreshing_history = False
        compact = not entries
        self.status_bar_widget.setVisible(not compact)
        self.list.setVisible(not compact)
        self.hint.setVisible(not compact)
        if compact:
            self.setMinimumHeight(COMPACT_HEIGHT)
            self.resize(self.width(), COMPACT_HEIGHT)
        elif self.height() < 420:
            self.setMinimumHeight(0)
            self.resize(self.width(), 560)

    def update_compact_state(self):
        empty = not self.search.text().strip() if hasattr(self, "search") else True
        if empty and not getattr(self, "refreshing_history", False):
            self.refresh_history_view()
        compact = (empty and not self.has_history_to_show()) or (not empty and not self.has_visible_content)

        if hasattr(self, "status_bar_widget"):
            self.status_bar_widget.setVisible(not compact)
        if hasattr(self, "list"):
            self.list.setVisible(not compact)
        if hasattr(self, "hint"):
            self.hint.setVisible(not compact)

        if compact:
            self.setMinimumHeight(COMPACT_HEIGHT)
            self.resize(self.width(), COMPACT_HEIGHT)
        else:
            self.setMinimumHeight(0)
            if self.height() < 420:
                self.resize(self.width(), 560)

    def activate(self):
        self.show(); self.raise_(); self.activateWindow(); self.search.setFocus(); self.search.selectAll()

    def query_changed(self):
        if self.search.text().strip():
            self.has_visible_content = False
            self.list.clear()
            self.count.clear()
            self.status.setText("Ready")
        self.update_compact_state()

    def keyPressEvent(self,e):
        if e.key()==Qt.Key_Escape: self.hide()
        elif e.key()==Qt.Key_F1: self.settings()
        else: super().keyPressEvent(e)

    def settings(self):
        d=Settings(self,self.cfg)
        if d.exec():
            values = d.values()
            if values is None:
                self.settings()
                return
            clear_history = (values.get("history", {}) or {}).pop("clear_on_save", False)
            self.cfg.update(values)
            self.save()
            self.client=None
            self.apply_behavior()
            self.history.configure(self.cfg)
            if clear_history:
                self.history.clear_current_machine()
            self.history.load()
            self.refresh_history_view()
            if self.hotkey_warning:
                QMessageBox.warning(self, "Shortcut unavailable", self.hotkey_warning)
            else:
                self.status.setText("Settings saved")

    def ensure_client(self):
        if not all((self.cfg.get("host"), self.cfg.get("user"), self.cfg.get("password"))):
            self.settings()
            if not all((self.cfg.get("host"), self.cfg.get("user"), self.cfg.get("password"))):
                raise RuntimeError("Configure the QNAP connection first")
        if not self.client:
            self.client=QsirchClient(self.cfg["host"],self.cfg["port"],self.cfg["ssl"])
            self.client.login(self.cfg["user"],self.cfg["password"])
        return self.client

    def clear_search(self):
        self.search.clear()
        self.list.clear()
        self.count.clear()
        self.status.setText("Ready")
        self.has_visible_content = False
        self.search.setFocus()
        self.update_compact_state()

    def do_search(self):
        q=self.search.text().strip()
        if not q:
            self.update_compact_state()
            return
        cached = self.history.search_results(q, self.current_history_filter())
        if cached:
            self.results = cached
            self.render_results(len(cached), 0, "Saved results")
            return
        self.update_history_filter_choices()
        self.list.clear(); self.count.clear(); self.status.setText("Searching...")
        if hasattr(self, "busy"):
            self.busy.show()
        self.has_visible_content = True
        self.update_compact_state()
        def fn(q):
            return self.ensure_client().search(q)
        self.worker=Worker(fn,q); self.worker.done.connect(self.show_results); self.worker.fail.connect(self.failed); self.worker.start()

    def render_results(self, server_total, hidden=0, status_text="Ready"):
        self.list.clear()
        for item in self.results:
            name=item.get("name","")
            ext=item.get("extension","")
            path=QsirchClient.path(item)
            size=item.get("size",0)
            try: size=f"{int(size)/1048576:.1f} MB" if int(size)>1048576 else f"{int(size)/1024:.0f} KB"
            except: size=""
            li=QListWidgetItem()
            li.setData(Qt.UserRole,item)
            row=QWidget()
            rh=QHBoxLayout(row)
            rh.setContentsMargins(10,7,10,7)
            rh.setSpacing(10)
            info_box=QWidget()
            iv=QVBoxLayout(info_box)
            iv.setContentsMargins(0,0,0,0)
            iv.setSpacing(3)
            title_text = f"{name}{('.'+ext) if ext else ''}"
            title=QLabel(title_text)
            title.setTextInteractionFlags(Qt.TextSelectableByMouse)
            title.setWordWrap(True)
            detail_text = display_path(path)
            if size:
                detail_text += f"\n{size}"
            detail=QLabel(detail_text)
            detail.setObjectName("hint")
            detail.setTextInteractionFlags(Qt.TextSelectableByMouse)
            detail.setWordWrap(True)
            iv.addWidget(title)
            iv.addWidget(detail)
            openb=QPushButton("Open")
            openb.setFixedWidth(70)
            openb.setToolTip("Open with the Windows default app")
            openb.clicked.connect(lambda checked=False, x=item: self.open_item(x))
            explorerb=QPushButton("Show")
            explorerb.setFixedWidth(70)
            explorerb.setToolTip("Show this file in Explorer")
            explorerb.clicked.connect(lambda checked=False, x=item: self.explorer_item(x))
            rh.addWidget(info_box,1)
            rh.addWidget(openb)
            rh.addWidget(explorerb)
            self.add_sized_row(li, row, 76)
        if hidden:
            self.count.setText(f"{len(self.results):,} shown  •  {hidden:,} excluded  •  {server_total:,} total")
        else:
            self.count.setText(f"{server_total:,} results")
        self.status.setText(status_text)
        self.has_visible_content = bool(self.results)
        self.update_compact_state()

    def show_results(self,data):
        if hasattr(self, "busy"):
            self.busy.hide()
        raw_items=data.get("items",[])
        self.results=[item for item in raw_items if not self.is_excluded(item)]
        self.history.add_results(self.results)
        self.update_history_filter_choices()
        server_total = data.get("total", len(raw_items))
        hidden = len(raw_items) - len(self.results)
        self.render_results(server_total, hidden, "Ready")

    def failed(self,msg):
        if hasattr(self, "busy"):
            self.busy.hide()
        self.status.setText("Error")
        self.has_visible_content = True
        self.update_compact_state()
        QMessageBox.critical(self,"Qsirch",msg)

    def selected(self):
        x=self.list.currentItem()
        return x.data(Qt.UserRole) if x else None

    @staticmethod
    def _norm_path(path):
        return str(path or "").replace("/", "\\").strip()

    def is_excluded(self, item):
        cfg = self.cfg.get("exclude", {}) or {}
        folder_rules = cfg.get("folders", []) or []
        file_rules = cfg.get("files", []) or []

        qpath = self._norm_path(QsirchClient.path(item))
        filename = str(item.get("name", "") or "")
        ext = str(item.get("extension", "") or "")
        full_name = filename
        if ext and not full_name.lower().endswith("." + ext.lower()):
            full_name += "." + ext

        parent = qpath
        if full_name and parent.lower().endswith(full_name.lower()):
            parent = parent[:-len(full_name)].rstrip("\\")
        components = [x for x in parent.split("\\") if x]
        path_candidates = {parent.lower(), (parent.rstrip("\\") + "\\").lower()}
        for idx in range(len(components)):
            tail = "\\".join(components[idx:])
            if tail:
                path_candidates.add(tail.lower())
                path_candidates.add((tail.rstrip("\\") + "\\").lower())

        for rule in folder_rules:
            rule = self._norm_path(rule)
            if not rule:
                continue
            rl = rule.lower()
            if any(ch in rule for ch in "*?[]"):
                if any(fnmatch.fnmatch(candidate, rl) for candidate in path_candidates):
                    return True
                if any(fnmatch.fnmatch(c.lower(), rl) for c in components):
                    return True
            else:
                if any(c.lower() == rl for c in components):
                    return True
                if rl in parent.lower():
                    return True

        for rule in file_rules:
            rule = str(rule or "").strip()
            if not rule:
                continue
            if any(ch in rule for ch in "*?[]"):
                if fnmatch.fnmatch(full_name.lower(), rule.lower()):
                    return True
            elif full_name.lower() == rule.lower():
                return True

        return False

    def resolve_mapped_path(self, item):
        qpath = self._norm_path(QsirchClient.path(item))

        name = str(item.get("name", "") or "")
        ext = str(item.get("extension", "") or "")
        full_name = name
        if ext and not full_name.lower().endswith("." + ext.lower()):
            full_name += "." + ext

        if full_name and not qpath.lower().endswith(full_name.lower()):
            qpath = qpath.rstrip("\\") + "\\" + full_name

        mappings = self.cfg.get("path_mappings", []) or []

        for m in mappings:
            mapped_root = self._norm_path(m.get("mapped_root", "")).rstrip("\\")
            source = self._norm_path(
                m.get("share_root")
                or m.get("unc_prefix")
                or m.get("qsirch_prefix")
                or ""
            ).rstrip("\\")

            if not mapped_root or not source:
                continue

            # Case 1: explicit UNC mapping.
            if source.startswith("\\\\"):
                if qpath.startswith("\\\\") and qpath.lower().startswith(source.lower()):
                    remainder = qpath[len(source):].lstrip("\\")
                    return mapped_root + ("\\" + remainder if remainder else "")

                # Qsirch often returns share-relative paths even when the GUI
                # mapping is entered as a full UNC root. Use the share name
                # from the UNC root as the relative prefix.
                parts = [x for x in source.split("\\") if x]
                share_name = parts[-1] if parts else ""
                if share_name:
                    qp = qpath.lstrip("\\")
                    prefix = share_name + "\\"
                    if qp.lower() == share_name.lower():
                        return mapped_root
                    if qp.lower().startswith(prefix.lower()):
                        return mapped_root + "\\" + qp[len(prefix):]

            # Case 2: Qsirch-relative mapping.
            else:
                qp = qpath.lstrip("\\")
                prefix = source.strip("\\") + "\\"
                if qp.lower() == source.strip("\\").lower():
                    return mapped_root
                if qp.lower().startswith(prefix.lower()):
                    return mapped_root + "\\" + qp[len(prefix):]

        raise RuntimeError(
            "No mapped-drive path rule matched this result. "
            "Add a path mapping in Settings."
        )

    def open_item(self, item):
        try:
            if isinstance(item, QListWidgetItem):
                item = item.data(Qt.UserRole)
            if not isinstance(item, dict):
                raise RuntimeError("Invalid search result object")
            if item.get("_history"):
                item = item.get("item", item)
            path = self.resolve_mapped_path(item)
            if sys.platform != "win32":
                raise RuntimeError("Direct file opening is supported on Windows.")
            os.startfile(path)
            self.status.setText(f"Opened: {path}")
        except Exception as e:
            self.failed(str(e))

    def explorer_item(self, item):
        try:
            if isinstance(item, QListWidgetItem):
                item = item.data(Qt.UserRole)
            if not isinstance(item, dict):
                raise RuntimeError("Invalid search result object")
            if item.get("_history"):
                item = item.get("item", item)
            path = self.resolve_mapped_path(item)
            if sys.platform != "win32":
                raise RuntimeError("Explorer integration is supported on Windows.")
            subprocess.Popen(["explorer.exe", "/select,", path])
            self.status.setText(f"Explorer: {path}")
        except Exception as e:
            self.failed(str(e))

    def menu(self,pos):
        item=self.list.itemAt(pos)
        if not item:
            return
        m=QMenu(self)
        op=m.addAction("Open")
        ex=m.addAction("Show in Explorer")
        a=m.exec(self.list.mapToGlobal(pos))
        obj=item.data(Qt.UserRole)
        if a==op:
            self.open_item(obj)
        elif a==ex:
            self.explorer_item(obj)

def main():
    app=QApplication(sys.argv)
    app.setApplicationName(APP_NAME)
    w=Main(); w.show()
    sys.exit(app.exec())

if __name__=="__main__": main()
