import sys, os, base64, json, webbrowser, fnmatch, subprocess, socket, uuid, tempfile, ctypes, html, re, mimetypes
from ctypes import wintypes
import xml.etree.ElementTree as ET
from pathlib import Path
from datetime import datetime
import requests

from PySide6.QtCore import Qt, QThread, Signal, QUrl, QSize, QTimer, QEvent, QDate
from PySide6.QtGui import QIcon, QDesktopServices, QKeySequence, QPixmap
from PySide6.QtWidgets import (
    QApplication, QWidget, QLineEdit, QListWidget, QListWidgetItem, QLabel,
    QVBoxLayout, QHBoxLayout, QPushButton, QFrame, QDialog, QFormLayout,
    QSpinBox, QCheckBox, QFileDialog, QMessageBox, QMenu, QAbstractItemView,
    QTabWidget, QTableWidget, QTableWidgetItem, QHeaderView, QGroupBox,
    QComboBox, QSystemTrayIcon, QStyle, QProgressBar, QSplitter, QTextEdit,
    QScrollArea, QDateEdit
)

APP_NAME = "Qsirch Floating Search"
APP_VERSION = "v10.17"
COMPACT_HEIGHT = 132
UPSTREAM_REPO = "https://github.com/iios-co/qsirch"
FORK_REPO = "https://github.com/senposage/qsirch-gui"
DONATION_URL = "https://www.paypal.com/donate?business=rjc862003%40gmail.com&currency_code=USD"
BASE_DIR = Path(sys.executable).resolve().parent if getattr(sys, "frozen", False) else Path(__file__).resolve().parent
CONFIG = BASE_DIR / "config.json"
TEXT_PREVIEW_EXTS = {
    "txt", "md", "csv", "tsv", "log", "json", "xml", "html", "htm", "css",
    "js", "ts", "py", "ps1", "bat", "cmd", "ini", "cfg", "conf", "yml",
    "yaml", "sql", "rtf"
}
IMAGE_PREVIEW_EXTS = {"jpg", "jpeg", "png", "gif", "bmp", "webp", "tif", "tiff"}
MAX_TEXT_PREVIEW_BYTES = 512 * 1024

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
    def __init__(self, host, port=8080, ssl=False, verify_ssl=False):
        self.base = f"{'https' if ssl else 'http'}://{host}:{port}"
        self.s = requests.Session()
        self.user = self.pw = None
        self.verify_ssl = bool(verify_ssl)
        if ssl and not self.verify_ssl:
            try:
                requests.packages.urllib3.disable_warnings()
            except Exception:
                pass

    def login(self, user, pw):
        self.user, self.pw = user, pw
        b64 = base64.b64encode(pw.encode()).decode()
        try:
            r = self.s.post(self.base + "/cgi-bin/authLogin.cgi",
                            data={"user": user, "pwd": b64}, timeout=10,
                            verify=self.verify_ssl)
        except requests.exceptions.RequestException as e:
            raise RuntimeError(self.connection_error_message(e))
        r.raise_for_status()
        x = ET.fromstring(r.text)
        if x.findtext("authPassed") != "1":
            raise RuntimeError("QNAP authentication failed")
        sid = x.findtext("authSid")
        if not sid:
            raise RuntimeError("QNAP did not return an authSid")
        self.s.cookies.set("NAS_SID", sid)

    def request(self, method, path, **kw):
        timeout = kw.pop("timeout", 15)
        if str(path).lower().startswith(("http://", "https://")):
            url = path
        elif str(path).startswith("/"):
            url = self.base + path
        else:
            url = self.base + "/" + str(path)
        try:
            r = self.s.request(method, url, timeout=timeout, verify=self.verify_ssl, **kw)
        except requests.exceptions.RequestException as e:
            raise RuntimeError(self.connection_error_message(e))
        if r.status_code == 401:
            try:
                if r.json().get("error", {}).get("code") == 101:
                    self.login(self.user, self.pw)
                    try:
                        r = self.s.request(method, url, timeout=timeout, verify=self.verify_ssl, **kw)
                    except requests.exceptions.RequestException as e:
                        raise RuntimeError(self.connection_error_message(e))
            except RuntimeError:
                raise
            except Exception:
                pass
        r.raise_for_status()
        return r

    def connection_error_message(self, err):
        msg = str(err)
        if "WRONG_VERSION_NUMBER" in msg.upper() or "wrong version number" in msg.lower():
            hint = (
                "HTTPS reached a plain HTTP service. The selected port is not serving TLS. "
                "Use the NAS HTTPS port, or turn HTTPS off for the HTTP port."
            )
        elif "CERTIFICATE_VERIFY_FAILED" in msg.upper() or "certificate verify failed" in msg.lower():
            hint = (
                "HTTPS connected, but the NAS certificate could not be verified. "
                "For a self-signed QNAP certificate, leave certificate verification off."
            )
        elif "MAX RETRIES EXCEEDED" in msg.upper():
            hint = (
                "The app could not connect after retrying. Check the NAS host/IP, port, "
                "firewall, and whether HTTPS is enabled on that port."
            )
        else:
            hint = "The Qsirch connection failed."
        return f"{hint}\n\nTechnical details: {msg}"

    @staticmethod
    def path(item):
        for x in item.get("preview", {}).get("info", []):
            if x.get("key") == "path":
                return x.get("value", "")
        return item.get("path", "")

    def search(self, q, limit=100, offset=0, mode=0, sort_by=None, sort_dir="desc", category=None):
        p = {"q": q, "limit": limit, "offset": offset, "advanced_mode": str(mode)}
        if sort_by and sort_by != "relevance":
            p["sort_by"] = sort_by
            p["sort_dir"] = sort_dir
        if category and category.lower() != "all":
            r = self.request("POST", "/qsirch/latest/api/search", params=p, json={"tools": category, "limit": limit})
        else:
            r = self.request("GET", "/qsirch/latest/api/search", params=p)
        return r.json()

    def similar(self, item_id, limit=25, category=None):
        params = {"limit": limit}
        if category and category.lower() != "all":
            params["categories"] = category
        r = self.request("GET", f"/qsirch/latest/api/more-like-this/{item_id}", params=params)
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

    def thumbnail(self, item):
        action = item.get("actions", {}).get("thumbnail")
        if not action:
            return None
        r = self.request("GET", action, timeout=30)
        return {
            "content": r.content,
            "content_type": r.headers.get("content-type", ""),
        }

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

def windows_identity_names():
    host = os.environ.get("COMPUTERNAME") or socket.gethostname()
    user = os.environ.get("USERNAME") or os.environ.get("USER") or ""
    domain = os.environ.get("USERDOMAIN") or ""
    names = {x.casefold() for x in (host, user) if x}
    if domain and user:
        names.add(f"{domain}\\{user}".casefold())
    return names

def parse_visibility_rule(rule):
    if isinstance(rule, dict):
        action = str(rule.get("access") or rule.get("action") or "deny").strip().lower()
        identity = str(rule.get("identity") or "*").strip() or "*"
        pattern = str(rule.get("pattern") or rule.get("path") or "").strip()
    else:
        text = str(rule or "").strip()
        action = "deny"
        identity = "*"
        pattern = text
        if text[:1] in ("+", "-"):
            action = "allow" if text[:1] == "+" else "deny"
            rest = text[1:]
            if ":" in rest:
                identity, pattern = rest.split(":", 1)
                identity = identity.strip() or "*"
                pattern = pattern.strip()
            else:
                pattern = rest.strip()
    if action not in ("allow", "deny"):
        action = "deny"
    return {"access": action, "identity": identity, "pattern": pattern}

def visibility_rules(cfg):
    return [
        parsed for parsed in (
            parse_visibility_rule(x) for x in (cfg.get("visibility_rules") or cfg.get("visibility", []) or [])
        )
        if parsed["pattern"]
    ]

def visibility_identity_matches(identity, current_names):
    ident = str(identity or "*").strip().casefold()
    if ident in ("", "*", "everyone", "all users", "users"):
        return True, 0
    if ident in current_names:
        return True, 2 if "\\" in ident else 1
    return False, -1

def path_matches_visibility_pattern(qpath, pattern):
    norm_path = str(qpath or "").replace("/", "\\").strip()
    rule = str(pattern or "").replace("/", "\\").strip()
    if not norm_path or not rule:
        return False
    parent = norm_path.rstrip("\\")
    components = [x for x in parent.split("\\") if x]
    candidates = {parent.casefold(), (parent.rstrip("\\") + "\\").casefold()}
    for idx in range(len(components)):
        tail = "\\".join(components[idx:])
        if tail:
            candidates.add(tail.casefold())
            candidates.add((tail.rstrip("\\") + "\\").casefold())
    rl = rule.casefold()
    if any(ch in rule for ch in "*?[]"):
        return any(fnmatch.fnmatch(candidate, rl) for candidate in candidates)
    return any(candidate == rl or candidate.startswith(rl.rstrip("\\").casefold() + "\\") for candidate in candidates)

def history_defaults(cfg):
    raw = cfg.get("history", {}) or {}
    source = str(raw.get("source_filter", raw.get("sourceFilter", "__this__")) or "__this__")
    return {
        "enabled": bool(raw.get("enabled", True)),
        "file": raw.get("file") or "history.json",
        "max_entries": int(raw.get("max_entries", raw.get("maxEntries", 200)) or 200),
        "source_filter": source,
    }

def behavior_defaults(cfg):
    raw = cfg.get("behavior", {}) or {}
    theme = str(raw.get("theme", "system") or "system").lower()
    if theme not in ("system", "light", "dark"):
        theme = "system"
    return {
        "show_in_taskbar": bool(raw.get("show_in_taskbar", raw.get("showInTaskbar", True))),
        "global_hotkey": str(raw.get("global_hotkey", raw.get("globalHotkey", "Ctrl+Space")) or "Ctrl+Space"),
        "theme": theme,
        "highlight_matches": bool(raw.get("highlight_matches", raw.get("highlightMatches", True))),
        "preview_pane": bool(raw.get("preview_pane", raw.get("previewPane", False))),
        "standard_window": bool(raw.get("standard_window", raw.get("standardWindow", True))),
        "allow_download": bool(raw.get("allow_download", raw.get("allowDownload", False))),
        "folders_first": bool(raw.get("folders_first", raw.get("foldersFirst", True))),
    }

def windows_prefers_dark():
    if sys.platform != "win32":
        return False
    try:
        import winreg
        key = winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")
        value, _ = winreg.QueryValueEx(key, "AppsUseLightTheme")
        return int(value) == 0
    except Exception:
        return False

def resolved_theme(cfg):
    theme = behavior_defaults(cfg)["theme"]
    if theme == "system":
        return "dark" if windows_prefers_dark() else "light"
    return theme

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

def display_folder_path(path):
    text = str(path or "").replace("/", "\\").strip("\\")
    parts = [x for x in text.split("\\") if x]
    if parts and parts[0].casefold() == "shared":
        text = "\\".join(parts[1:])
    return display_path(text or path)

def result_display_parts(item, fallback_path=""):
    name = str(item.get("name", "") or "")
    ext = str(item.get("extension", "") or "")
    file_name = name
    if ext and file_name and not file_name.lower().endswith("." + ext.lower()):
        file_name = f"{file_name}.{ext}"
    full_path = str(fallback_path or QsirchClient.path(item) or "")
    folder = full_path
    if file_name and folder.lower().endswith(file_name.lower()):
        folder = folder[:-len(file_name)].rstrip("\\/")
    return folder, file_name or full_path

def result_key(item):
    path = str(QsirchClient.path(item) or "").casefold()
    name = str(item.get("name", "") or "").casefold()
    ext = str(item.get("extension", "") or "").casefold()
    return path, name, ext

def highlight_text(text, query, enabled):
    text = str(text or "")
    if not enabled:
        return html.escape(text).replace("\n", "<br>")
    query = str(query or "").strip()
    if not query:
        return html.escape(text).replace("\n", "<br>")
    terms = [x for x in re.split(r"\s+", query) if len(x) > 1]
    if not terms:
        return html.escape(text).replace("\n", "<br>")
    pattern = re.compile("(" + "|".join(re.escape(x) for x in terms) + ")", re.IGNORECASE)
    pieces = []
    pos = 0
    for match in pattern.finditer(text):
        pieces.append(html.escape(text[pos:match.start()]))
        pieces.append(f"<mark>{html.escape(match.group(0))}</mark>")
        pos = match.end()
    pieces.append(html.escape(text[pos:]))
    return "".join(pieces).replace("\n", "<br>")

def preview_summary(data):
    if not isinstance(data, dict):
        return ""
    lines = []
    container = data.get("container_type") or data.get("type")
    if container:
        lines.append(f"Preview type: {container}")
    for key in ("title", "subject", "from", "to", "date", "modified", "created"):
        value = data.get(key)
        if value:
            lines.append(f"{key[:1].upper() + key[1:]}: {value}")
    html_body = str(data.get("html") or "")
    text_body = str(data.get("text") or data.get("content") or "")
    body = re.sub(r"<[^>]+>", " ", html_body) if html_body else text_body
    body = re.sub(r"\s+", " ", body).strip()
    if body:
        lines.append("")
        lines.append(body[:1800])
    info = data.get("info")
    if isinstance(info, list):
        for entry in info[:12]:
            if isinstance(entry, dict) and entry.get("key") and entry.get("value"):
                lines.append(f"{entry.get('key')}: {entry.get('value')}")
    return "\n".join(lines).strip()

def parse_search_text(text, exact=False):
    text = str(text or "").strip()
    opts = {"query": text, "ext": "", "path": "", "regex": "", "exclude": []}
    tokens = re.findall(r'"[^"]+"|\S+', text)
    query_tokens = []
    for token in tokens:
        raw = token.strip()
        low = raw.lower()
        if low.startswith(("ext:", "type:")) and ":" in raw:
            opts["ext"] = raw.split(":", 1)[1].strip().lstrip(".")
        elif low.startswith("path:") and ":" in raw:
            opts["path"] = raw.split(":", 1)[1].strip().strip('"')
        elif len(raw) > 3 and raw.startswith("r/") and raw.endswith("/"):
            opts["regex"] = raw[2:-1]
        elif raw.startswith("-") and len(raw) > 1:
            opts["exclude"].append(raw[1:].strip('"'))
            query_tokens.append(raw)
        else:
            query_tokens.append(raw)
    query = " ".join(query_tokens).strip() or "."
    if exact and query and not (query.startswith('"') and query.endswith('"')):
        query = f'"{query}"'
    opts["query"] = query
    return opts

def item_modified_text(item):
    for source in (item.get("modified"), item.get("created")):
        if source:
            return str(source)
    for meta in (item.get("metadata", {}) or {}).get("all", []) or []:
        key = str(meta.get("key", "")).lower()
        if key in ("modified", "date modified", "created", "date"):
            return str(meta.get("value", ""))
    return ""

def client_filter_items(items, opts, from_date="", to_date=""):
    ext_filter = str(opts.get("ext", "") or "").lower().lstrip(".")
    path_filter = str(opts.get("path", "") or "").casefold()
    regex_text = str(opts.get("regex", "") or "")
    exclude_terms = [str(x).casefold() for x in opts.get("exclude", []) if x]
    regex = None
    if regex_text:
        try:
            regex = re.compile(regex_text, re.IGNORECASE)
        except re.error:
            regex = None
    filtered = []
    for item in items or []:
        full_path = str(QsirchClient.path(item) or "")
        name = str(item.get("name", "") or "")
        ext = str(item.get("extension", "") or "").lower().lstrip(".")
        haystack = " ".join([name, ext, full_path, str(item.get("content", "") or "")])
        if ext_filter and ext != ext_filter:
            continue
        if path_filter and path_filter not in full_path.casefold():
            continue
        if exclude_terms and any(term in haystack.casefold() for term in exclude_terms):
            continue
        if regex and not regex.search(haystack):
            continue
        modified = item_modified_text(item)[:10]
        if from_date and modified and modified < from_date:
            continue
        if to_date and modified and modified > to_date:
            continue
        filtered.append(item)
    return filtered

def windows_rank(item, query, folders_first=True):
    clean = re.sub(r'\b(AND|OR|NOT)\b', ' ', str(query or ""), flags=re.IGNORECASE).replace('"', "")
    terms = [x.casefold() for x in re.split(r"\s+", clean) if x and not x.startswith("-")]
    name = str(item.get("name", "") or "")
    path = str(QsirchClient.path(item) or "")
    haystack = f"{name} {path} {item.get('content', '')}".casefold()
    exact = clean.strip().casefold()
    exact_score = 0 if exact and exact in name.casefold() else 1
    starts_score = 0 if exact and name.casefold().startswith(exact) else 1
    term_hits = sum(1 for term in terms if term in haystack)
    folder_score = 0 if folders_first and str(item.get("type", "")).lower() == "folder" else 1
    return (folder_score, exact_score, starts_score, -term_hits, name.casefold())

def main_stylesheet(cfg):
    if resolved_theme(cfg) == "light":
        return """
        QWidget { color:#1f2328; }
        #card { background:#f7f8fa; border:1px solid #c9d1d9; border-radius:12px; }
        #cardStandard { background:#f7f8fa; border:0; border-radius:0; }
        QLineEdit {
            background:#ffffff; color:#1f2328; border:1px solid #b8c0cc;
            border-radius:8px; padding:9px 12px; font-size:15px;
        }
        QLineEdit:focus { border:1px solid #0969da; background:#ffffff; }
        QComboBox {
            background:#ffffff; color:#1f2328; border:1px solid #b8c0cc;
            border-radius:7px; padding:5px 8px; font-size:12px;
        }
        QPushButton {
            background:#eef1f4; color:#1f2328; border:1px solid #b8c0cc;
            border-radius:7px; padding:7px 10px; font-size:12px;
        }
        QPushButton:hover { background:#e2e7ee; }
        QPushButton:pressed { background:#d7dde5; }
        QPushButton:disabled { color:#8c959f; background:#eef1f4; }
        QPushButton#pinButton:checked { background:#dbeafe; border-color:#60a5fa; color:#0f172a; }
        QPushButton#exitButton:hover { background:#fee2e2; border-color:#fca5a5; }
        QLabel#versionBadge {
            color:#57606a; background:#eef1f4; border:1px solid #c9d1d9;
            border-radius:7px; padding:6px 9px; font-size:12px;
        }
        QListWidget {
            background:#ffffff; color:#1f2328; border:1px solid #c9d1d9;
            border-radius:8px; font-size:13px; padding:4px;
        }
        QFrame#sidebar {
            background:#eef1f4; border:1px solid #c9d1d9; border-radius:8px;
        }
        QPushButton#navButton {
            text-align:left; background:transparent; border:0; border-radius:6px;
            padding:8px 9px; color:#24292f;
        }
        QPushButton#navButton:checked { background:#dbeafe; color:#0f172a; }
        QListWidget::item { padding:0; border-bottom:1px solid #d8dee4; }
        QListWidget::item:selected { background:#dbeafe; border-radius:6px; }
        QLabel { color:#24292f; }
        QLabel#folderPath { color:#0f172a; font-size:13px; font-weight:600; }
        QLabel#fileName { color:#4b5563; font-size:12px; }
        QFrame#previewPane { background:#ffffff; border:1px solid #c9d1d9; border-radius:8px; }
        QLabel#previewImage { background:#f1f3f5; border:1px solid #d8dee4; border-radius:6px; }
        QTextEdit#previewText { background:#ffffff; color:#4b5563; border:0; font-size:12px; }
        mark { background:#fff3a3; color:#0f172a; }
        #hint { color:#6e7781; font-size:12px; }
        """
    return """
        QWidget { color:#e7e9eb; }
        #card { background:#202124; border:1px solid #3b3d42; border-radius:12px; }
        #cardStandard { background:#202124; border:0; border-radius:0; }
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
        QFrame#sidebar {
            background:#17191c; border:1px solid #303238; border-radius:8px;
        }
        QPushButton#navButton {
            text-align:left; background:transparent; border:0; border-radius:6px;
            padding:8px 9px; color:#dce1e6;
        }
        QPushButton#navButton:checked { background:#26364d; color:#ffffff; }
        QListWidget::item { padding:0; border-bottom:1px solid #282a2e; }
        QListWidget::item:selected { background:#26364d; border-radius:6px; }
        QLabel { color:#dce1e6; }
        QLabel#folderPath { color:#f1f4f7; font-size:13px; font-weight:600; }
        QLabel#fileName { color:#c8d0d8; font-size:12px; }
        QFrame#previewPane { background:#191a1d; border:1px solid #303238; border-radius:8px; }
        QLabel#previewImage { background:#101214; border:1px solid #282a2e; border-radius:6px; }
        QTextEdit#previewText { background:#191a1d; color:#c8d0d8; border:0; font-size:12px; }
        mark { background:#7c5f16; color:#fff7cc; }
        #hint { color:#858b94; font-size:12px; }
        """

def settings_stylesheet(cfg):
    if resolved_theme(cfg) == "light":
        return """
        QDialog, QWidget { background: #f7f8fa; color: #1f2328; }
        QTabWidget::pane { border: 1px solid #c9d1d9; background: #f7f8fa; }
        QTabBar::tab { background: #eef1f4; color: #24292f; padding: 9px 14px; border: 1px solid #c9d1d9; border-bottom: none; }
        QTabBar::tab:selected { background: #ffffff; color: #0f172a; }
        QLineEdit, QSpinBox, QListWidget, QTableWidget, QComboBox { background: #ffffff; color: #1f2328; border: 1px solid #b8c0cc; border-radius: 6px; selection-background-color: #dbeafe; }
        QHeaderView::section { background: #eef1f4; color: #24292f; border: 1px solid #c9d1d9; padding: 6px; }
        QPushButton { background: #eef1f4; color: #1f2328; border: 1px solid #b8c0cc; border-radius: 7px; padding: 7px 12px; }
        QPushButton:hover { background: #e2e7ee; }
        QGroupBox { border: 1px solid #c9d1d9; border-radius: 8px; margin-top: 10px; padding-top: 10px; }
        QGroupBox::title { subcontrol-origin: margin; left: 10px; padding: 0 4px; color: #24292f; }
        QCheckBox { color: #1f2328; }
        QLabel { color: #24292f; }
        """
    return """
        QDialog, QWidget { background: #15171a; color: #e7e9eb; }
        QTabWidget::pane { border: 1px solid #30343a; background: #15171a; }
        QTabBar::tab { background: #22262b; color: #cfd3d8; padding: 9px 14px; border: 1px solid #30343a; border-bottom: none; }
        QTabBar::tab:selected { background: #2b3138; color: #ffffff; }
        QLineEdit, QSpinBox, QListWidget, QTableWidget, QComboBox { background: #101214; color: #f0f2f4; border: 1px solid #3a4048; border-radius: 6px; selection-background-color: #26364d; }
        QHeaderView::section { background: #22262b; color: #d8dbe0; border: 1px solid #30343a; padding: 6px; }
        QPushButton { background: #252a30; color: #e7e9eb; border: 1px solid #3a4048; border-radius: 7px; padding: 7px 12px; }
        QPushButton:hover { background: #30363d; }
        QGroupBox { border: 1px solid #30343a; border-radius: 8px; margin-top: 10px; padding-top: 10px; }
        QGroupBox::title { subcontrol-origin: margin; left: 10px; padding: 0 4px; color: #d7dbe0; }
        QCheckBox { color: #e7e9eb; }
        QLabel { color: #c7ccd2; }
        """

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
            current["starred"] = bool(entry.get("starred", False))
            old = merged.get(key)
            if not old or current["lastUsed"] >= old.get("lastUsed", ""):
                if old:
                    current["uses"] = max(current["uses"], int(old.get("uses", 1) or 1))
                    current["starred"] = bool(current.get("starred") or old.get("starred"))
                merged[key] = current
        out = list(merged.values())
        out.sort(key=lambda x: (bool(x.get("starred")), x.get("lastUsed", "")), reverse=True)
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
                current["starred"] = bool(entry.get("starred", False))
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
        if mode == "__favorites__":
            return [
                x for x in self.entries
                if bool(x.get("starred")) and (x.get("machineId") == self.machine_id or x.get("machine") == self.machine)
            ]
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

    def clear_current_machine(self, clear_starred=False):
        self.load()
        self.entries = [
            x for x in self.entries
            if (
                x.get("machineId") != self.machine_id and x.get("machine") != self.machine
            ) or (bool(x.get("starred")) and not clear_starred)
        ]
        try:
            self._write()
        except Exception:
            pass

    def is_starred(self, item):
        key = result_key(item)
        self.load()
        for entry in self.entries:
            stored = entry.get("item") if isinstance(entry.get("item"), dict) else entry
            if result_key(stored) == key and (
                entry.get("machineId") == self.machine_id or entry.get("machine") == self.machine
            ):
                return bool(entry.get("starred", False))
        return False

    def starred_keys(self):
        self.load()
        keys = set()
        for entry in self.entries:
            if not entry.get("starred"):
                continue
            stored = entry.get("item") if isinstance(entry.get("item"), dict) else entry
            if entry.get("machineId") == self.machine_id or entry.get("machine") == self.machine:
                keys.add(result_key(stored))
        return keys

    def set_starred(self, item, starred):
        if not self.enabled or not isinstance(item, dict):
            return
        self.load()
        key = result_key(item)
        now = datetime.now().isoformat(timespec="seconds")
        found = False
        for entry in self.entries:
            stored = entry.get("item") if isinstance(entry.get("item"), dict) else entry
            if result_key(stored) == key and (
                entry.get("machineId") == self.machine_id or entry.get("machine") == self.machine
            ):
                entry["starred"] = bool(starred)
                entry["lastUsed"] = now
                found = True
        if not found and starred:
            self.entries.append({
                "name": str(item.get("name", "") or ""),
                "extension": str(item.get("extension", "") or ""),
                "path": QsirchClient.path(item),
                "size": item.get("size", 0),
                "lastUsed": now,
                "machine": self.machine,
                "machineId": self.machine_id,
                "ip": self.ip,
                "uses": 1,
                "starred": True,
                "item": item,
            })
        self.entries = self._normalise(self.entries)
        self._write()

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
        self.setStyleSheet(settings_stylesheet(self.cfg))

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
        self.ssl_verify = QCheckBox("Verify HTTPS certificate")
        self.ssl_verify.setChecked(self.cfg.get("ssl_verify", False))
        self.ssl_warning = QLabel("")
        self.ssl_warning.setWordWrap(True)
        self.ssl_warning.setStyleSheet("color: #ffcf7a;")
        self.ssl.stateChanged.connect(self.ssl_toggled)
        self.port.valueChanged.connect(self.update_ssl_warning)
        cf.addRow("NAS host / IP", self.host)
        cf.addRow("Port", self.port)
        cf.addRow("Username", self.user)
        cf.addRow("Password", self.pw)
        cf.addRow("", self.ssl)
        cf.addRow("", self.ssl_verify)
        cf.addRow("", self.ssl_warning)
        self.update_ssl_warning()
        self.tabs.addTab(conn, "Connection")

        behavior = QWidget()
        bf = QFormLayout(behavior)
        bcfg = behavior_defaults(self.cfg)
        self.show_taskbar = QCheckBox("Show the main window in the Windows taskbar")
        self.show_taskbar.setChecked(bcfg["show_in_taskbar"])
        self.standard_window = QCheckBox("Use standard resizable Windows frame")
        self.standard_window.setChecked(bcfg["standard_window"])
        self.theme_choice = QComboBox()
        self.theme_choice.addItem("Use Windows setting", "system")
        self.theme_choice.addItem("Light", "light")
        self.theme_choice.addItem("Dark", "dark")
        theme_idx = self.theme_choice.findData(bcfg["theme"])
        self.theme_choice.setCurrentIndex(theme_idx if theme_idx >= 0 else 0)
        self.highlight_matches = QCheckBox("Highlight matching text in results")
        self.highlight_matches.setChecked(bcfg["highlight_matches"])
        self.preview_pane = QCheckBox("Show preview pane")
        self.preview_pane.setChecked(bcfg["preview_pane"])
        self.folders_first = QCheckBox("Show folders before files")
        self.folders_first.setChecked(bcfg["folders_first"])
        self.allow_download = QCheckBox("Show Download button")
        self.allow_download.setChecked(bcfg["allow_download"])
        self.hotkey_edit = ShortcutEdit(normalise_hotkey_text(bcfg["global_hotkey"]))
        self.hotkey_edit.setPlaceholderText("Ctrl+Space")
        self.hotkey_warning = QLabel("")
        self.hotkey_warning.setWordWrap(True)
        self.hotkey_warning.setStyleSheet("color: #ffcf7a;")
        if getattr(parent, "hotkey_warning", ""):
            self.hotkey_warning.setText(parent.hotkey_warning)
        bf.addRow("", self.show_taskbar)
        bf.addRow("", self.standard_window)
        bf.addRow("Theme", self.theme_choice)
        bf.addRow("", self.highlight_matches)
        bf.addRow("", self.preview_pane)
        bf.addRow("", self.folders_first)
        bf.addRow("", self.allow_download)
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

        rules_tab = QWidget()
        ev = QVBoxLayout(rules_tab)

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
        visible_box = QGroupBox("Visibility rules")
        vv = QVBoxLayout(visible_box)
        visible_note = QLabel(
            "Visibility rules only hide or show results in this app. They do not change NAS permissions."
        )
        visible_note.setWordWrap(True)
        vv.addWidget(visible_note)
        self.visibility_table = QTableWidget(0, 3)
        self.visibility_table.setHorizontalHeaderLabels(["Access", "Identity", "Path pattern"])
        self.visibility_table.horizontalHeader().setSectionResizeMode(0, QHeaderView.ResizeToContents)
        self.visibility_table.horizontalHeader().setSectionResizeMode(1, QHeaderView.ResizeToContents)
        self.visibility_table.horizontalHeader().setSectionResizeMode(2, QHeaderView.Stretch)
        self.visibility_table.verticalHeader().setVisible(False)
        self.visibility_table.setSelectionBehavior(QAbstractItemView.SelectRows)
        self.visibility_table.setSelectionMode(QAbstractItemView.SingleSelection)
        vv.addWidget(self.visibility_table, 1)
        vb = QHBoxLayout()
        add_visibility = QPushButton("Add")
        edit_visibility = QPushButton("Edit")
        remove_visibility = QPushButton("Remove")
        add_visibility.clicked.connect(self.add_visibility_rule)
        edit_visibility.clicked.connect(self.edit_visibility_rule)
        remove_visibility.clicked.connect(self.remove_visibility_rule)
        vb.addWidget(add_visibility)
        vb.addWidget(edit_visibility)
        vb.addWidget(remove_visibility)
        vb.addStretch()
        vv.addLayout(vb)
        for rule in visibility_rules(self.cfg):
            self._append_visibility_rule(rule["access"], rule["identity"], rule["pattern"])
        ev.addWidget(visible_box, 1)
        self.tabs.addTab(rules_tab, "Rules")

        hist = QWidget()
        hf = QFormLayout(hist)
        hcfg = history_defaults(self.cfg)
        self.history_enabled = QCheckBox("Keep shared result history")
        self.history_enabled.setChecked(hcfg["enabled"])
        self.history_file = QLineEdit(hcfg["file"])
        self.history_max = QSpinBox()
        self.history_max.setRange(1, 5000)
        self.history_max.setValue(hcfg["max_entries"])
        self.history_source = QComboBox()
        self.history_source.addItem("Favorites", "__favorites__")
        self.history_source.addItem("This machine", "__this__")
        self.history_source.addItem("All history", "__all__")
        if parent and hasattr(parent, "history"):
            for machine in parent.history.machines():
                self.history_source.addItem(machine, machine)
        source_idx = self.history_source.findData(hcfg["source_filter"])
        self.history_source.setCurrentIndex(source_idx if source_idx >= 0 else 1)
        self.clear_history = QCheckBox("Clear this machine's history when saving")
        self.clear_starred_history = QCheckBox("Also clear starred results")
        self.clear_starred_history.setToolTip("Normally starred results stay saved when this machine's history is cleared.")
        clear_this_machine = QPushButton("Clear This Machine's History")
        clear_this_machine.clicked.connect(self.clear_current_machine_history)
        import_machine = QPushButton("Import Another Machine's History")
        import_machine.clicked.connect(self.import_machine_history)
        hf.addRow("", self.history_enabled)
        hf.addRow("History file", self.history_file)
        hf.addRow("Maximum entries", self.history_max)
        hf.addRow("Default saved-results view", self.history_source)
        hf.addRow("", self.clear_history)
        hf.addRow("", self.clear_starred_history)
        hf.addRow("", clear_this_machine)
        hf.addRow("", import_machine)
        self.tabs.addTab(hist, "History")

        about = QWidget()
        av = QVBoxLayout(about)
        av.setSpacing(10)
        title = QLabel(f"{APP_NAME} {APP_VERSION}")
        title.setObjectName("folderPath")
        title.setWordWrap(True)
        av.addWidget(title)
        description = QLabel(
            "Windows floating search app for QNAP Qsirch. This GUI builds on the "
            "MIT-licensed upstream Qsirch CLI/API implementation."
        )
        description.setWordWrap(True)
        av.addWidget(description)
        upstream = QLabel(f"Upstream CLI/API project: {UPSTREAM_REPO}")
        upstream.setTextInteractionFlags(Qt.TextSelectableByMouse)
        upstream.setWordWrap(True)
        av.addWidget(upstream)
        fork = QLabel(f"GUI fork: {FORK_REPO}")
        fork.setTextInteractionFlags(Qt.TextSelectableByMouse)
        fork.setWordWrap(True)
        av.addWidget(fork)
        license_note = QLabel(
            "License: MIT. Original upstream copyright belongs to IIOS Pty Ltd. "
            "GUI additions are distributed under the same MIT terms."
        )
        license_note.setWordWrap(True)
        av.addWidget(license_note)
        donate = QPushButton("Donate via PayPal")
        donate.setToolTip("Open PayPal donation page")
        donate.clicked.connect(self.open_donation)
        av.addWidget(donate)
        av.addStretch()
        self.tabs.addTab(about, "About")

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

    def _append_visibility_rule(self, access, identity, pattern):
        row = self.visibility_table.rowCount()
        self.visibility_table.insertRow(row)
        label = "Allow" if str(access).lower() == "allow" else "Deny"
        self.visibility_table.setItem(row, 0, QTableWidgetItem(label))
        self.visibility_table.setItem(row, 1, QTableWidgetItem(identity or "*"))
        self.visibility_table.setItem(row, 2, QTableWidgetItem(pattern or ""))

    def update_ssl_warning(self):
        self.ssl_verify.setEnabled(self.ssl.isChecked())
        if self.ssl.isChecked() and self.port.value() == 8080:
            self.ssl_warning.setText("HTTPS is enabled on port 8080. If the NAS uses 8080 for HTTP, either uncheck HTTPS or choose the NAS HTTPS port.")
        elif self.ssl.isChecked() and not self.ssl_verify.isChecked():
            self.ssl_warning.setText("HTTPS certificate verification is off. This is usually needed for self-signed NAS certificates.")
        else:
            self.ssl_warning.clear()

    def ssl_toggled(self):
        if self.ssl.isChecked() and self.port.value() == 8080:
            self.port.setValue(443)
        elif not self.ssl.isChecked() and self.port.value() == 443:
            self.port.setValue(8080)
        self.update_ssl_warning()

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

    def _visibility_dialog(self, access="deny", identity="*", pattern=""):
        d = QDialog(self)
        d.setStyleSheet(self.styleSheet())
        d.setWindowTitle("Visibility Rule")
        f = QFormLayout(d)
        access_box = QComboBox()
        access_box.addItem("Deny", "deny")
        access_box.addItem("Allow", "allow")
        idx = access_box.findData(str(access or "deny").lower())
        access_box.setCurrentIndex(idx if idx >= 0 else 0)
        ident = QLineEdit(identity or "*")
        ident.setPlaceholderText("*, username, DOMAIN\\username, or HOSTNAME")
        path = QLineEdit(pattern)
        path.setPlaceholderText("Share\\Folder\\*")
        note = QLabel("Deny everyone with * or a blank identity, then add Allow rows for users or hosts that should see the path.")
        note.setWordWrap(True)
        f.addRow("Access", access_box)
        f.addRow("Identity", ident)
        f.addRow("Path pattern", path)
        f.addRow("", note)
        b = QHBoxLayout()
        b.addStretch()
        c = QPushButton("Cancel")
        o = QPushButton("OK")
        c.clicked.connect(d.reject)
        o.clicked.connect(d.accept)
        b.addWidget(c); b.addWidget(o)
        f.addRow("", b)
        if d.exec() and path.text().strip():
            return access_box.currentData(), ident.text().strip() or "*", path.text().strip()
        return None

    def add_visibility_rule(self):
        result = self._visibility_dialog()
        if result:
            self._append_visibility_rule(*result)

    def edit_visibility_rule(self):
        row = self.visibility_table.currentRow()
        if row < 0:
            return
        access_text = self.visibility_table.item(row, 0).text() if self.visibility_table.item(row, 0) else "Deny"
        access = "allow" if access_text.lower() == "allow" else "deny"
        identity = self.visibility_table.item(row, 1).text() if self.visibility_table.item(row, 1) else "*"
        pattern = self.visibility_table.item(row, 2).text() if self.visibility_table.item(row, 2) else ""
        result = self._visibility_dialog(access, identity, pattern)
        if result:
            self.visibility_table.setItem(row, 0, QTableWidgetItem("Allow" if result[0] == "allow" else "Deny"))
            self.visibility_table.setItem(row, 1, QTableWidgetItem(result[1]))
            self.visibility_table.setItem(row, 2, QTableWidgetItem(result[2]))

    def remove_visibility_rule(self):
        row = self.visibility_table.currentRow()
        if row >= 0:
            self.visibility_table.removeRow(row)

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
            parent.history.clear_current_machine(self.clear_starred_history.isChecked())
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

    def open_donation(self):
        QDesktopServices.openUrl(QUrl(DONATION_URL))

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
        rules = []
        for row in range(self.visibility_table.rowCount()):
            access_item = self.visibility_table.item(row, 0)
            identity_item = self.visibility_table.item(row, 1)
            pattern_item = self.visibility_table.item(row, 2)
            access_text = access_item.text().strip() if access_item else "Deny"
            identity = identity_item.text().strip() if identity_item else "*"
            pattern = pattern_item.text().strip() if pattern_item else ""
            if pattern:
                rules.append({
                    "access": "allow" if access_text.lower() == "allow" else "deny",
                    "identity": identity or "*",
                    "pattern": pattern,
                })

        return {
            "host": self.host.text().strip(),
            "port": self.port.value(),
            "user": self.user.text(),
            "password": self.pw.text(),
            "ssl": self.ssl.isChecked(),
            "ssl_verify": self.ssl_verify.isChecked(),
            "path_mappings": mappings,
            "exclude": {"folders": folders, "files": files},
            "visibility_rules": rules,
            "behavior": {
                "show_in_taskbar": self.show_taskbar.isChecked(),
                "global_hotkey": hotkey_text,
                "theme": self.theme_choice.currentData() or "system",
                "highlight_matches": self.highlight_matches.isChecked(),
                "preview_pane": self.preview_pane.isChecked(),
                "standard_window": self.standard_window.isChecked(),
                "allow_download": self.allow_download.isChecked(),
                "folders_first": self.folders_first.isChecked(),
            },
            "history": {
                "enabled": self.history_enabled.isChecked(),
                "file": self.history_file.text().strip() or "history.json",
                "max_entries": self.history_max.value(),
                "source_filter": self.history_source.currentData() or "__this__",
                "clear_on_save": self.clear_history.isChecked(),
                "clear_starred_on_save": self.clear_starred_history.isChecked(),
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
        self.preview_worker = None
        self.preview_workers = []
        self.preview_request_id = 0
        self.preview_pixmap = None
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
        self.preview_visible = False
        self.filters_visible = False
        self.history_view = history_defaults(self.cfg)["source_filter"]
        self.hotkey_manager = HotkeyManager(self)
        self.hotkey_warning = ""
        self.apply_window_flags()
        self.setAttribute(Qt.WA_TranslucentBackground, not behavior_defaults(self.cfg)["standard_window"])
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
                "ssl_verify": False,
                "path_mappings": [],
                "exclude": {
                    "folders": [
                        "@Recently-Snapshot\\*",
                        "@Recycle\\*",
                        "#recycle\\*",
                        ".sync\\*",
                        ".qsync\\*",
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
                "visibility_rules": [],
                "history": {"enabled": True, "file": "history.json", "max_entries": 200, "source_filter": "__this__"},
                "behavior": {"show_in_taskbar": True, "global_hotkey": "Ctrl+Space", "theme": "system", "highlight_matches": True, "preview_pane": False, "standard_window": True, "allow_download": False, "folders_first": True},
                "always_on_top": True
            }

    def save(self):
        CONFIG.parent.mkdir(parents=True, exist_ok=True)
        CONFIG.write_text(json.dumps(self.cfg, indent=2))

    def build(self):
        outer=QVBoxLayout(self); self.outer_layout = outer; outer.setContentsMargins(10,10,10,10)
        card=QFrame(); card.setObjectName("card"); self.card = card; outer.addWidget(card)
        card.installEventFilter(self)
        v=QVBoxLayout(card); self.card_layout = v; v.setContentsMargins(14,14,14,12); v.setSpacing(8)

        top=QHBoxLayout()
        top.setSpacing(8)
        self.search=QLineEdit(); self.search.setPlaceholderText("Search Qsirch")
        self.search.setMinimumHeight(40)
        self.search.setClearButtonEnabled(False)
        self.search.returnPressed.connect(self.do_search)
        self.search.textChanged.connect(self.query_changed)
        self.search.textChanged.connect(lambda _: self.clear_btn.setEnabled(bool(self.search.text())))
        top.addWidget(self.search,1)

        self.help_btn=QPushButton("?")
        self.help_btn.setObjectName("toolButton")
        self.help_btn.setToolTip("Search syntax")
        self.help_btn.setFixedWidth(34)
        self.help_btn.setMinimumHeight(36)
        self.help_btn.clicked.connect(self.show_search_help)
        top.addWidget(self.help_btn)

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

        self.pin_btn=QPushButton("Always on top")
        self.pin_btn.setObjectName("pinButton")
        self.pin_btn.setToolTip("Keep the search window always on top")
        self.pin_btn.setCheckable(True)
        self.pin_btn.setChecked(self.pinned)
        self.pin_btn.setFixedWidth(112)
        self.pin_btn.setMinimumHeight(36)
        self.pin_btn.clicked.connect(self.toggle_pin)
        top.addWidget(self.pin_btn)
        self.update_pin_button()

        self.filters_btn=QPushButton("Filters")
        self.filters_btn.setObjectName("toolButton")
        self.filters_btn.setToolTip("Show or hide search filters")
        self.filters_btn.setCheckable(True)
        self.filters_btn.setChecked(False)
        self.filters_btn.setFixedWidth(68)
        self.filters_btn.setMinimumHeight(36)
        self.filters_btn.clicked.connect(self.toggle_filters)
        top.addWidget(self.filters_btn)

        self.preview_btn=QPushButton("Preview")
        self.preview_btn.setObjectName("toolButton")
        self.preview_btn.setToolTip("Show or hide the preview pane")
        self.preview_btn.setCheckable(True)
        self.preview_btn.setChecked(False)
        self.preview_btn.setFixedWidth(74)
        self.preview_btn.setMinimumHeight(36)
        self.preview_btn.clicked.connect(self.toggle_preview_pane)
        top.addWidget(self.preview_btn)

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

        self.filters_widget = QWidget()
        filters=QHBoxLayout(self.filters_widget)
        filters.setContentsMargins(0, 0, 0, 0)
        filters.setSpacing(6)
        self.exact_match=QCheckBox("Exact")
        self.exact_match.setToolTip("Search as an exact phrase")
        filters.addWidget(self.exact_match)
        self.ext_filter=QLineEdit()
        self.ext_filter.setPlaceholderText("ext")
        self.ext_filter.setToolTip("Filter by extension, such as pdf or docx")
        self.ext_filter.setFixedWidth(58)
        self.ext_filter.returnPressed.connect(self.do_search)
        filters.addWidget(self.ext_filter)
        self.path_filter=QLineEdit()
        self.path_filter.setPlaceholderText("path")
        self.path_filter.setToolTip("Filter results to paths containing this text")
        self.path_filter.setFixedWidth(120)
        self.path_filter.returnPressed.connect(self.do_search)
        filters.addWidget(self.path_filter)
        self.category_filter=QComboBox()
        self.category_filter.setToolTip("Qsirch category filter")
        for label, value in (("All", "All"), ("Email", "Email"), ("PDF", "PDF"), ("Documents", "Documents"), ("Images", "Images"), ("Videos", "Videos"), ("Music", "Music"), ("Excel", "Excel"), ("Word", "Word")):
            self.category_filter.addItem(label, value)
        filters.addWidget(self.category_filter)
        self.mode_filter=QComboBox()
        self.mode_filter.setToolTip("Search mode")
        self.mode_filter.addItem("Text", 0)
        self.mode_filter.addItem("Image OCR", 1)
        self.mode_filter.addItem("Combined", 2)
        filters.addWidget(self.mode_filter)
        self.sort_filter=QComboBox()
        self.sort_filter.setToolTip("Sort results")
        for label, value in (("Best match", "relevance"), ("Name", "name"), ("Modified", "modified"), ("Created", "created"), ("Size", "size")):
            self.sort_filter.addItem(label, value)
        filters.addWidget(self.sort_filter)
        self.sort_dir=QComboBox()
        self.sort_dir.setToolTip("Sort direction")
        self.sort_dir.addItem("Desc", "desc")
        self.sort_dir.addItem("Asc", "asc")
        filters.addWidget(self.sort_dir)
        self.limit_filter=QSpinBox()
        self.limit_filter.setRange(1, 500)
        self.limit_filter.setValue(100)
        self.limit_filter.setToolTip("Maximum results")
        filters.addWidget(self.limit_filter)
        self.from_date=QDateEdit()
        self.from_date.setCalendarPopup(True)
        self.from_date.setDisplayFormat("yyyy-MM-dd")
        self.from_date.setSpecialValueText("From")
        self.from_date.setMinimumDate(QDate(1900, 1, 1))
        self.from_date.setDate(self.from_date.minimumDate())
        filters.addWidget(self.from_date)
        self.to_date=QDateEdit()
        self.to_date.setCalendarPopup(True)
        self.to_date.setDisplayFormat("yyyy-MM-dd")
        self.to_date.setSpecialValueText("To")
        self.to_date.setMinimumDate(QDate(1900, 1, 1))
        self.to_date.setDate(self.to_date.minimumDate())
        filters.addWidget(self.to_date)
        self.similar_btn=QPushButton("Similar")
        self.similar_btn.setToolTip("Find more results like the selected item")
        self.similar_btn.setFixedWidth(70)
        self.similar_btn.clicked.connect(self.more_like_this)
        filters.addWidget(self.similar_btn)
        filters.addStretch()
        self.filters_widget.setVisible(False)
        v.addWidget(self.filters_widget)

        self.status_bar_widget = QWidget()
        bar=QHBoxLayout(self.status_bar_widget)
        bar.setContentsMargins(0,0,0,0)
        self.status=QLabel("Ready")
        self.status.installEventFilter(self)
        self.count=QLabel("")
        self.count.installEventFilter(self)
        bar.addWidget(self.status)
        bar.addStretch()
        bar.addWidget(self.count)
        v.addWidget(self.status_bar_widget)

        self.list=QListWidget()
        self.list.setSelectionMode(QAbstractItemView.SingleSelection)
        self.list.itemDoubleClicked.connect(self.open_item)
        self.list.itemSelectionChanged.connect(self.selection_changed)
        self.list.setContextMenuPolicy(Qt.CustomContextMenu)
        self.list.customContextMenuRequested.connect(self.menu)

        self.preview_panel = QFrame()
        self.preview_panel.setObjectName("previewPane")
        pp = QVBoxLayout(self.preview_panel)
        pp.setContentsMargins(10, 10, 10, 10)
        pp.setSpacing(8)
        ph = QHBoxLayout()
        self.preview_title = QLabel("Preview")
        self.preview_title.setObjectName("folderPath")
        self.preview_title.setWordWrap(True)
        close_preview = QPushButton("Hide")
        close_preview.setFixedWidth(56)
        close_preview.clicked.connect(lambda: self.set_preview_visible(False))
        ph.addWidget(self.preview_title, 1)
        ph.addWidget(close_preview)
        pp.addLayout(ph)
        self.preview_image = QLabel("")
        self.preview_image.setObjectName("previewImage")
        self.preview_image.setAlignment(Qt.AlignCenter)
        self.preview_image.setMinimumSize(220, 220)
        self.preview_image.setScaledContents(False)
        pp.addWidget(self.preview_image, 2)
        self.preview_text = QTextEdit()
        self.preview_text.setObjectName("previewText")
        self.preview_text.setReadOnly(True)
        self.preview_text.setLineWrapMode(QTextEdit.WidgetWidth)
        self.preview_text.setPlainText("Select a result to preview.")
        pp.addWidget(self.preview_text, 3)

        self.sidebar = QFrame()
        self.sidebar.setObjectName("sidebar")
        self.sidebar_layout = QVBoxLayout(self.sidebar)
        self.sidebar_layout.setContentsMargins(8, 8, 8, 8)
        self.sidebar_layout.setSpacing(6)
        self.favorite_title = QLabel("Favorites")
        self.favorite_title.setObjectName("folderPath")
        self.favorite_count = QLabel("")
        self.favorite_count.setObjectName("fileName")
        self.sidebar_layout.addWidget(self.favorite_title)
        self.sidebar_layout.addWidget(self.favorite_count)
        self.sidebar_layout.addStretch()
        settings_button = QPushButton("Settings")
        settings_button.setObjectName("navButton")
        settings_button.clicked.connect(self.settings)
        self.sidebar_layout.addWidget(settings_button)
        self.refresh_favorites_panel()

        self.results_splitter = QSplitter(Qt.Horizontal)
        self.results_splitter.addWidget(self.list)
        self.results_splitter.addWidget(self.preview_panel)
        self.results_splitter.setStretchFactor(0, 3)
        self.results_splitter.setStretchFactor(1, 2)

        self.content_splitter = QSplitter(Qt.Horizontal)
        self.content_splitter.addWidget(self.sidebar)
        self.content_splitter.addWidget(self.results_splitter)
        self.content_splitter.setStretchFactor(0, 0)
        self.content_splitter.setStretchFactor(1, 1)
        self.content_splitter.setSizes([145, 675])
        self.preview_panel.setVisible(False)
        v.addWidget(self.content_splitter,1)

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
        self.apply_window_layout_mode()

    def apply_style(self):
        self.setStyleSheet(main_stylesheet(self.cfg))

    def apply_window_layout_mode(self):
        standard = behavior_defaults(self.cfg)["standard_window"]
        if hasattr(self, "outer_layout"):
            self.outer_layout.setContentsMargins(0 if standard else 10, 0 if standard else 10, 0 if standard else 10, 0 if standard else 10)
        if hasattr(self, "card_layout"):
            self.card_layout.setContentsMargins(12 if standard else 14, 12 if standard else 14, 12 if standard else 14, 10 if standard else 12)
        if hasattr(self, "card"):
            self.card.setObjectName("cardStandard" if standard else "card")
            self.card.style().unpolish(self.card)
            self.card.style().polish(self.card)

    def apply_window_flags(self):
        standard = behavior_defaults(self.cfg)["standard_window"]
        flags = Qt.Window if standard else (Qt.Window | Qt.FramelessWindowHint)
        if not getattr(self, "show_in_taskbar", True):
            flags = Qt.Tool if standard else (Qt.Tool | Qt.FramelessWindowHint)
        if self.pinned:
            flags |= Qt.WindowStaysOnTopHint
        self.setWindowFlags(flags)

    def apply_behavior(self):
        bcfg = behavior_defaults(self.cfg)
        was_visible = self.isVisible()
        taskbar_changed = self.show_in_taskbar != bcfg["show_in_taskbar"]
        frame_changed = bool(self.behavior.get("standard_window", False)) != bool(bcfg["standard_window"])
        self.behavior = bcfg
        self.show_in_taskbar = bool(bcfg["show_in_taskbar"])
        self.setAttribute(Qt.WA_TranslucentBackground, not bcfg["standard_window"])
        self.apply_window_layout_mode()
        self.apply_style()
        if taskbar_changed or frame_changed:
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

    def show_search_help(self):
        QMessageBox.information(
            self,
            "Search syntax",
            "\n".join([
                'Use normal words, or quote an exact phrase: "invoice 123"',
                "ext:pdf or type:docx filters by extension.",
                "path:clients narrows results to matching paths.",
                "-draft excludes matching text.",
                "r/pattern/ applies a local regex filter.",
                "Use Filters for category, OCR mode, dates, sort, and result limit.",
            ])
        )

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
        self.pin_btn.setText("Always on top" if self.pinned else "On top")

    def update_preview_button(self):
        if hasattr(self, "preview_btn"):
            self.preview_btn.setChecked(self.preview_visible)

    def toggle_filters(self):
        self.filters_visible = not self.filters_visible
        if hasattr(self, "filters_widget"):
            self.filters_widget.setVisible(self.filters_visible)
        if hasattr(self, "filters_btn"):
            self.filters_btn.setChecked(self.filters_visible)

    def refresh_favorites_panel(self):
        if not hasattr(self, "favorite_count"):
            return
        count = len(self.history.filtered("__favorites__"))
        self.favorite_count.setText(f"{count:,} saved" if count else "No favorites")

    def set_preview_visible(self, visible, persist=True):
        self.preview_visible = bool(visible)
        if hasattr(self, "preview_panel"):
            self.preview_panel.setVisible(self.preview_visible)
        self.update_preview_button()
        if persist:
            self.cfg.setdefault("behavior", {})["preview_pane"] = self.preview_visible
            self.save()
        if self.preview_visible and persist:
            self.load_selected_preview()
        elif self.preview_visible:
            self.clear_preview()
        else:
            self.clear_preview("Preview hidden")

    def toggle_preview_pane(self):
        self.set_preview_visible(not self.preview_visible)

    def selected_item_data(self):
        item = self.selected()
        if isinstance(item, dict) and item.get("_history"):
            item = item.get("item", item)
        return item if isinstance(item, dict) else None

    def selection_changed(self):
        if self.preview_visible:
            self.load_selected_preview()

    def clear_preview(self, text="Select a result to preview."):
        self.preview_pixmap = None
        if hasattr(self, "preview_image"):
            self.preview_image.clear()
            self.preview_image.setText("")
        if hasattr(self, "preview_text"):
            self.preview_text.setPlainText(text)
        if hasattr(self, "preview_title"):
            self.preview_title.setText("Preview")

    def load_selected_preview(self):
        if not self.preview_visible or not hasattr(self, "preview_text"):
            return
        item = self.selected_item_data()
        if not item:
            self.clear_preview()
            return
        folder, file_name = result_display_parts(item, QsirchClient.path(item))
        self.preview_title.setText(file_name or "Preview")
        self.preview_pixmap = None
        self.preview_image.clear()
        self.preview_text.setPlainText("Loading preview...")
        self.preview_request_id += 1
        request_id = self.preview_request_id

        def fn(preview_item, rid):
            return self.build_preview_data(preview_item, rid)

        worker = Worker(fn, item, request_id)
        self.preview_workers.append(worker)
        self.preview_worker = worker
        worker.done.connect(self.preview_loaded)
        worker.fail.connect(self.preview_failed)
        worker.finished.connect(lambda w=worker: self.preview_workers.remove(w) if w in self.preview_workers else None)
        worker.start()

    def build_preview_data(self, preview_item, request_id):
        local = self.local_file_preview(preview_item)
        thumb = None
        summary = local.get("summary", "")
        if not local.get("image_path") and all((self.cfg.get("host"), self.cfg.get("user"), self.cfg.get("password"))):
            client = self.ensure_client()
            try:
                thumb = client.thumbnail(preview_item)
            except Exception as e:
                if not summary:
                    summary = str(e)
            if not summary:
                try:
                    summary = preview_summary(client.preview(preview_item))
                except Exception as e:
                    summary = str(e)
        if not summary and not all((self.cfg.get("host"), self.cfg.get("user"), self.cfg.get("password"))):
            summary = "Configure the QNAP connection, or add a path mapping to preview the local file."
        return {
            "request_id": request_id,
            "thumbnail": thumb,
            "image_path": local.get("image_path", ""),
            "summary": summary,
        }

    def local_file_preview(self, item):
        try:
            path = Path(self.resolve_mapped_path(item))
        except Exception:
            return {"summary": ""}
        if not path.exists():
            return {"summary": f"Mapped file was not found:\n{path}"}
        if path.is_dir():
            try:
                names = []
                for idx, child in enumerate(path.iterdir()):
                    if idx >= 200:
                        break
                    names.append(child.name)
                names.sort()
                body = "\n".join(names)
                more = "\n..." if len(names) >= 200 else ""
                return {"summary": f"Folder: {path}\n\n{body}{more}"}
            except Exception as e:
                return {"summary": f"Folder: {path}\n\n{e}"}

        ext = path.suffix.lower().lstrip(".")
        mime, _ = mimetypes.guess_type(str(path))
        size = path.stat().st_size
        header = f"{path.name}\n{path}\n{size:,} bytes"
        if ext in IMAGE_PREVIEW_EXTS or str(mime or "").startswith("image/"):
            return {"image_path": str(path), "summary": header}
        if ext in TEXT_PREVIEW_EXTS or str(mime or "").startswith("text/"):
            raw = path.read_bytes()[:MAX_TEXT_PREVIEW_BYTES]
            text = None
            for encoding in ("utf-8-sig", "utf-16", "cp1252", "latin-1"):
                try:
                    text = raw.decode(encoding)
                    break
                except UnicodeDecodeError:
                    pass
            if text is None:
                return {"summary": header + "\n\nText preview unavailable."}
            text = text.replace("\x00", "")
            truncated = "\n\n[Preview truncated]" if size > MAX_TEXT_PREVIEW_BYTES else ""
            return {"summary": header + "\n\n" + text + truncated}
        return {"summary": header + "\n\nNo local text preview is available for this file type."}

    def preview_loaded(self, data):
        if not isinstance(data, dict) or data.get("request_id") != self.preview_request_id:
            return
        thumb = data.get("thumbnail")
        image_path = data.get("image_path")
        if image_path:
            pix = QPixmap(image_path)
            if not pix.isNull():
                self.preview_pixmap = pix
                self.scale_preview_image()
            else:
                self.preview_image.setText("Preview unavailable")
        elif isinstance(thumb, dict) and thumb.get("content"):
            pix = QPixmap()
            if pix.loadFromData(thumb["content"]):
                self.preview_pixmap = pix
                self.scale_preview_image()
            else:
                self.preview_image.setText("Thumbnail unavailable")
        else:
            self.preview_image.setText("No thumbnail")
        self.preview_text.setPlainText(data.get("summary") or "No preview available.")

    def scale_preview_image(self):
        if not self.preview_pixmap or not hasattr(self, "preview_image"):
            return
        size = self.preview_image.contentsRect().size()
        if size.width() < 16 or size.height() < 16:
            size = self.preview_image.size()
        self.preview_image.setPixmap(self.preview_pixmap.scaled(size, Qt.KeepAspectRatio, Qt.SmoothTransformation))

    def resizeEvent(self, event):
        super().resizeEvent(event)
        self.scale_preview_image()

    def preview_failed(self, msg):
        self.preview_image.clear()
        self.preview_text.setPlainText(msg or "Preview unavailable.")

    def make_star_button(self, item, starred=None):
        button = QPushButton()
        button.setFixedWidth(38)
        button.setToolTip("Keep this result in saved history")
        if starred is None:
            starred = self.history.is_starred(item)
        button.setText("★" if starred else "☆")
        button.clicked.connect(lambda checked=False, x=item, b=button: self.toggle_star(x, b))
        return button

    def toggle_star(self, item, button=None):
        if isinstance(item, dict) and item.get("_history"):
            item = item.get("item", item)
        if not isinstance(item, dict):
            return
        starred = not self.history.is_starred(item)
        self.history.set_starred(item, starred)
        if button is not None:
            button.setText("★" if starred else "☆")
        self.status.setText("Added to favorites" if starred else "Removed from favorites")

    def make_download_button(self, item):
        if not behavior_defaults(self.cfg)["allow_download"]:
            return None
        button = QPushButton("Download")
        button.setFixedWidth(82)
        button.setToolTip("Download this file from Qsirch")
        button.clicked.connect(lambda checked=False, x=item: self.download_item(x))
        return button

    def download_item(self, item):
        try:
            if isinstance(item, QListWidgetItem):
                item = item.data(Qt.UserRole)
            if isinstance(item, dict) and item.get("_history"):
                item = item.get("item", item)
            if not isinstance(item, dict):
                raise RuntimeError("Invalid search result object")
            folder = QFileDialog.getExistingDirectory(self, "Choose download folder")
            if not folder:
                return
            self.status.setText("Downloading...")
            out = self.ensure_client().download(item, folder)
            self.status.setText(f"Downloaded: {out}")
        except Exception as e:
            self.failed(str(e))

    def more_like_this(self):
        item = self.selected_item_data()
        if not item:
            self.status.setText("Select a result first")
            return
        item_id = item.get("id")
        if not item_id:
            self.status.setText("This result has no Qsirch ID")
            return
        self.list.clear()
        self.count.clear()
        self.status.setText("Finding similar...")
        if hasattr(self, "busy"):
            self.busy.show()
        opts = self.current_search_options()

        def fn(search_item_id, search_opts):
            return {
                "data": self.ensure_client().similar(
                    search_item_id,
                    limit=search_opts["limit"],
                    category=search_opts["category"],
                ),
                "opts": search_opts,
            }

        self.worker=Worker(fn,item_id,opts)
        self.worker.done.connect(self.show_results)
        self.worker.fail.connect(self.failed)
        self.worker.start()

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
        return getattr(self, "history_view", history_defaults(self.cfg)["source_filter"]) or "__this__"

    def update_history_filter_choices(self):
        self.refresh_favorites_panel()

    def is_visibility_hidden(self, item):
        rules = visibility_rules(self.cfg)
        if not rules:
            return False
        qpath = self._norm_path(QsirchClient.path(item))
        if not qpath:
            return False
        identities = windows_identity_names()
        matches = []
        for rule in rules:
            if not path_matches_visibility_pattern(qpath, rule["pattern"]):
                continue
            applies, specificity = visibility_identity_matches(rule["identity"], identities)
            if applies:
                matches.append((specificity, rule["access"]))
        if not matches:
            return False
        best = max(specificity for specificity, _ in matches)
        best_actions = [access for specificity, access in matches if specificity == best]
        return "allow" not in best_actions

    def add_sized_row(self, item, row, minimum_height=58):
        def select_row(event, list_item=item):
            self.list.setCurrentItem(list_item)
        def open_row(event, list_item=item):
            self.list.setCurrentItem(list_item)
            self.open_item(list_item)
        row.mousePressEvent = select_row
        row.mouseDoubleClickEvent = open_row
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
            raw_entries = self.history.filtered(self.current_history_filter())
            entries = []
            for entry in raw_entries:
                item = entry.get("item") if isinstance(entry.get("item"), dict) else entry
                if not self.is_visibility_hidden(item):
                    entries.append(entry)
            starred_keys = self.history.starred_keys()
            self.list.clear()
            for entry in entries:
                item = entry.get("item") if isinstance(entry.get("item"), dict) else entry
                name = entry.get("name") or item.get("name", "")
                ext = entry.get("extension") or item.get("extension", "")
                if ext and name and not str(name).lower().endswith("." + str(ext).lower()):
                    name = f"{name}.{ext}"
                path = entry.get("path") or QsirchClient.path(item)
                folder, file_name = result_display_parts(item, path)
                machine = entry.get("machine", "")
                ip = entry.get("ip", "")
                used = entry.get("lastUsed", "")
                meta_parts = [x for x in (machine, ip, used) if x]
                starred = result_key(item) in starred_keys
                meta = str(file_name or name or "Saved result")
                if meta_parts:
                    meta += "\n" + "  |  ".join(meta_parts)
                if path:
                    meta += "\n" + display_path(path)
                li = QListWidgetItem()
                li.setData(Qt.UserRole, {"_history": True, "item": item})
                row = QWidget()
                rh = QHBoxLayout(row)
                rh.setContentsMargins(10, 7, 10, 7)
                rh.setSpacing(10)
                info_box = QWidget()
                iv = QVBoxLayout(info_box)
                iv.setContentsMargins(0, 0, 0, 0)
                iv.setSpacing(3)
                title = QLabel(display_folder_path(folder))
                title.setObjectName("folderPath")
                title.setTextInteractionFlags(Qt.TextSelectableByMouse)
                title.setWordWrap(True)
                detail = QLabel(meta)
                detail.setObjectName("fileName")
                detail.setTextInteractionFlags(Qt.TextSelectableByMouse)
                detail.setWordWrap(True)
                iv.addWidget(title)
                iv.addWidget(detail)
                openb = QPushButton("Open")
                openb.setFixedWidth(70)
                openb.setToolTip("Open with the Windows default app")
                openb.clicked.connect(lambda checked=False, x=item: self.open_item(x))
                explorerb = QPushButton("Show")
                explorerb.setFixedWidth(70)
                explorerb.setToolTip("Show this file in Explorer")
                explorerb.clicked.connect(lambda checked=False, x=item: self.explorer_item(x))
                starb = self.make_star_button(item, starred)
                downb = self.make_download_button(item)
                rh.addWidget(info_box, 1)
                rh.addWidget(starb)
                if downb:
                    rh.addWidget(downb)
                rh.addWidget(openb)
                rh.addWidget(explorerb)
                self.add_sized_row(li, row, 76)
            if entries:
                self.status.setText("Saved results")
                self.count.setText(f"{len(entries):,} saved")
            else:
                self.status.setText("Ready")
                self.count.clear()
            if self.preview_visible:
                if entries:
                    self.list.setCurrentRow(0)
                else:
                    self.clear_preview()
        finally:
            self.refreshing_history = False
        compact = not entries
        self.status_bar_widget.setVisible(not compact)
        self.list.setVisible(not compact)
        if hasattr(self, "content_splitter"):
            self.content_splitter.setVisible(not compact)
        self.hint.setVisible(not compact)
        if compact and not behavior_defaults(self.cfg)["standard_window"]:
            self.setMinimumHeight(COMPACT_HEIGHT)
            self.resize(self.width(), COMPACT_HEIGHT)
        elif self.height() < 420 and not behavior_defaults(self.cfg)["standard_window"]:
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
        if hasattr(self, "content_splitter"):
            self.content_splitter.setVisible(not compact)
        if hasattr(self, "hint"):
            self.hint.setVisible(not compact)

        if compact and not behavior_defaults(self.cfg)["standard_window"]:
            self.setMinimumHeight(COMPACT_HEIGHT)
            self.resize(self.width(), COMPACT_HEIGHT)
        elif not behavior_defaults(self.cfg)["standard_window"]:
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
            hvalues = values.get("history", {}) or {}
            clear_history = hvalues.pop("clear_on_save", False)
            clear_starred = hvalues.pop("clear_starred_on_save", False)
            self.cfg.update(values)
            self.save()
            self.client=None
            self.apply_behavior()
            self.history.configure(self.cfg)
            self.history_view = history_defaults(self.cfg)["source_filter"]
            if clear_history:
                self.history.clear_current_machine(clear_starred)
            self.history.load()
            self.refresh_favorites_panel()
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
            self.client=QsirchClient(
                self.cfg["host"],
                self.cfg["port"],
                self.cfg["ssl"],
                self.cfg.get("ssl_verify", False),
            )
            try:
                self.client.login(self.cfg["user"],self.cfg["password"])
            except RuntimeError as e:
                msg = str(e)
                if self.cfg.get("ssl") and self.cfg.get("ssl_verify", False) and "certificate could not be verified" in msg.lower():
                    self.cfg["ssl_verify"] = False
                    self.save()
                    self.client=QsirchClient(
                        self.cfg["host"],
                        self.cfg["port"],
                        self.cfg["ssl"],
                        False,
                    )
                    self.client.login(self.cfg["user"],self.cfg["password"])
                    self.status.setText("HTTPS certificate verification turned off")
                elif self.cfg.get("ssl") and int(self.cfg.get("port", 0) or 0) == 8080 and "SSL handshake" in msg:
                    self.cfg["port"] = 443
                    self.save()
                    self.client=QsirchClient(
                        self.cfg["host"],
                        self.cfg["port"],
                        self.cfg["ssl"],
                        self.cfg.get("ssl_verify", False),
                    )
                    self.client.login(self.cfg["user"],self.cfg["password"])
                    self.status.setText("HTTPS port changed to 443")
                else:
                    raise
        return self.client

    def clear_search(self):
        self.search.clear()
        if hasattr(self, "ext_filter"):
            self.ext_filter.clear()
        if hasattr(self, "path_filter"):
            self.path_filter.clear()
        self.list.clear()
        self.count.clear()
        self.status.setText("Ready")
        self.has_visible_content = False
        self.search.setFocus()
        self.update_compact_state()

    def date_filter_text(self, widget):
        if not hasattr(widget, "date") or widget.date() == widget.minimumDate():
            return ""
        return widget.date().toString("yyyy-MM-dd")

    def current_search_options(self):
        opts = parse_search_text(self.search.text(), self.exact_match.isChecked())
        if self.ext_filter.text().strip():
            opts["ext"] = self.ext_filter.text().strip().lstrip(".")
        if self.path_filter.text().strip():
            opts["path"] = self.path_filter.text().strip()
        opts["category"] = self.category_filter.currentData() or "All"
        opts["mode"] = int(self.mode_filter.currentData() or 0)
        opts["sort_by"] = self.sort_filter.currentData() or "relevance"
        opts["sort_dir"] = self.sort_dir.currentData() or "desc"
        opts["limit"] = int(self.limit_filter.value())
        opts["from_date"] = self.date_filter_text(self.from_date)
        opts["to_date"] = self.date_filter_text(self.to_date)
        return opts

    def do_search(self):
        raw_q=self.search.text().strip()
        if not raw_q:
            self.update_compact_state()
            return
        opts = self.current_search_options()
        cached = [
            item for item in self.history.search_results(raw_q, self.current_history_filter())
            if not self.is_visibility_hidden(item)
        ]
        cached = client_filter_items(cached, opts, opts["from_date"], opts["to_date"])
        if cached:
            self.results = self.sort_results(cached, opts)
            self.render_results(len(cached), 0, "Saved results")
            return
        self.update_history_filter_choices()
        self.list.clear(); self.count.clear(); self.status.setText("Searching...")
        if hasattr(self, "busy"):
            self.busy.show()
        self.has_visible_content = True
        self.update_compact_state()
        def fn(search_opts):
            return {"data": self.ensure_client().search(
                search_opts["query"],
                limit=search_opts["limit"],
                mode=search_opts["mode"],
                sort_by=search_opts["sort_by"],
                sort_dir=search_opts["sort_dir"],
                category=search_opts["category"],
            ), "opts": search_opts}
        self.worker=Worker(fn,opts); self.worker.done.connect(self.show_results); self.worker.fail.connect(self.failed); self.worker.start()

    def sort_results(self, items, opts):
        folders_first = behavior_defaults(self.cfg)["folders_first"]
        if opts.get("sort_by") == "relevance":
            return sorted(items, key=lambda item: windows_rank(item, opts.get("query", ""), folders_first))
        return list(items)

    def render_results(self, server_total, hidden=0, status_text="Ready"):
        self.list.clear()
        query = self.search.text().strip()
        do_highlight = behavior_defaults(self.cfg)["highlight_matches"]
        starred_keys = self.history.starred_keys()
        for item in self.results:
            path=QsirchClient.path(item)
            folder, file_name = result_display_parts(item, path)
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
            title=QLabel()
            title.setObjectName("folderPath")
            title.setTextFormat(Qt.RichText)
            title.setText(highlight_text(display_folder_path(folder), query, do_highlight))
            title.setTextInteractionFlags(Qt.TextSelectableByMouse)
            title.setWordWrap(True)
            detail_text = str(file_name)
            if size:
                detail_text += f"\n{size}"
            if path:
                detail_text += f"\n{display_path(path)}"
            detail=QLabel()
            detail.setObjectName("fileName")
            detail.setTextFormat(Qt.RichText)
            detail.setText(highlight_text(detail_text, query, do_highlight))
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
            starb=self.make_star_button(item, result_key(item) in starred_keys)
            downb=self.make_download_button(item)
            rh.addWidget(info_box,1)
            rh.addWidget(starb)
            if downb:
                rh.addWidget(downb)
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
        if self.preview_visible:
            if self.results:
                self.list.setCurrentRow(0)
            else:
                self.clear_preview()

    def show_results(self,data):
        if hasattr(self, "busy"):
            self.busy.hide()
        opts = data.get("opts", {}) if isinstance(data, dict) and "data" in data else self.current_search_options()
        data = data.get("data", {}) if isinstance(data, dict) and "data" in data else data
        raw_items=data.get("items",[])
        indexed_items=[item for item in raw_items if not self.is_excluded(item)]
        visible_items=[item for item in indexed_items if not self.is_visibility_hidden(item)]
        visible_items = client_filter_items(visible_items, opts, opts.get("from_date", ""), opts.get("to_date", ""))
        self.results=self.sort_results(visible_items, opts)
        self.history.add_results(indexed_items)
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
        sim=m.addAction("More Like This")
        dl=None
        if behavior_defaults(self.cfg)["allow_download"]:
            dl=m.addAction("Download")
        a=m.exec(self.list.mapToGlobal(pos))
        obj=item.data(Qt.UserRole)
        if a==op:
            self.open_item(obj)
        elif a==ex:
            self.explorer_item(obj)
        elif a==sim:
            self.list.setCurrentItem(item)
            self.more_like_this()
        elif dl and a==dl:
            self.download_item(obj)

def main():
    app=QApplication(sys.argv)
    app.setApplicationName(APP_NAME)
    w=Main(); w.show()
    sys.exit(app.exec())

if __name__=="__main__": main()
