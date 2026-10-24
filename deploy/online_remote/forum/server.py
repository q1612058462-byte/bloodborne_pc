"""Small, isolated feedback forum for the Windows information site.

No game or portal credentials are accepted here. New accounts' first posts
require moderation. All uploaded pictures are decoded and re-encoded as WebP.
"""
import base64
from collections import defaultdict, deque
import hashlib
import hmac
from http.cookies import SimpleCookie
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from io import BytesIO
import zipfile
import json
import os
from pathlib import Path
import re
import secrets
import sqlite3
import sys
import threading
import time
from urllib.parse import parse_qs, quote, urlsplit
import warnings

from PIL import Image, ImageOps


DATA = Path(os.environ.get("FORUM_DATA", "/var/lib/forum"))
ORIGIN = os.environ.get("FORUM_ORIGIN", "https://82.157.168.20")
MAX_BODY = 32 * 1024 * 1024
MAX_IMAGE = 3 * 1024 * 1024
MAX_IMAGE_PIXELS = 12_000_000
MAX_IMAGES = 6
MAX_USER_IMAGE_BYTES = 50 * 1024 * 1024
MAX_FILES = 3
MAX_FILE = 4 * 1024 * 1024
MAX_USER_FILE_BYTES = 100 * 1024 * 1024
FILE_TYPES = {".log": "text/plain", ".txt": "text/plain", ".jsonl": "application/jsonl", ".zip": "application/zip"}
SESSION_SECONDS = 30 * 86400
CATEGORIES = {"求助", "联机", "建议", "闲聊"}
Image.MAX_IMAGE_PIXELS = MAX_IMAGE_PIXELS


class ApiError(Exception):
    def __init__(self, status, message):
        super().__init__(message)
        self.status = status


def clean_text(value, minimum, maximum, label):
    if not isinstance(value, str):
        raise ApiError(400, f"{label}格式不正确")
    value = value.strip()
    if not minimum <= len(value) <= maximum or any(ord(c) < 32 and c not in "\n\t" for c in value):
        raise ApiError(400, f"{label}长度或内容不正确")
    return value


def prepare_images(items):
    if not isinstance(items, list) or len(items) > MAX_IMAGES:
        raise ApiError(400, "每条最多上传六张图片")
    output = []
    for item in items:
        if not isinstance(item, dict) or item.get("mime") not in ("image/png", "image/jpeg", "image/webp"):
            raise ApiError(400, "仅支持 PNG、JPEG、WebP 图片")
        encoded = item.get("data")
        if not isinstance(encoded, str) or len(encoded) > (MAX_IMAGE * 4 // 3 + 8):
            raise ApiError(400, "图片不能超过 3 MB")
        try:
            raw = base64.b64decode(encoded, validate=True)
            if len(raw) > MAX_IMAGE:
                raise ValueError("image too large")
            with warnings.catch_warnings():
                warnings.simplefilter("error", Image.DecompressionBombWarning)
                with Image.open(BytesIO(raw)) as source:
                    if source.format not in ("PNG", "JPEG", "WEBP") or getattr(source, "is_animated", False):
                        raise ValueError("unsupported image")
                    if source.width * source.height > MAX_IMAGE_PIXELS:
                        raise ValueError("image dimensions too large")
                    image = ImageOps.exif_transpose(source)
                    image.thumbnail((2400, 2400))
                    if image.mode in ("RGBA", "LA") or (image.mode == "P" and "transparency" in image.info):
                        image = image.convert("RGBA")
                    else:
                        image = image.convert("RGB")
                    target = BytesIO()
                    image.save(target, format="WEBP", quality=82, method=4)
                    result = target.getvalue()
            if len(result) > 2 * 1024 * 1024:
                raise ValueError("encoded image too large")
            output.append(result)
        except (ValueError, OSError, Image.DecompressionBombError) as exc:
            raise ApiError(400, "图片无法读取、尺寸过大或格式不支持") from exc
    return output


def prepare_files(items, limit=MAX_FILES):
    if not isinstance(items, list) or len(items) > limit:
        raise ApiError(400, f"每条最多上传 {limit} 个附件")
    output = []
    for item in items:
        name = item.get("name") if isinstance(item, dict) else None
        encoded = item.get("data") if isinstance(item, dict) else None
        if not isinstance(name, str) or not re.fullmatch(r"[\w.()\- \u4e00-\u9fff]{1,100}", name) or "." not in name:
            raise ApiError(400, "附件名称无效")
        suffix = Path(name).suffix.lower()
        if suffix not in FILE_TYPES or not isinstance(encoded, str) or len(encoded) > MAX_FILE * 4 // 3 + 8:
            raise ApiError(400, "附件仅支持 LOG、TXT、JSONL、ZIP，单个不超过 4 MB")
        try:
            raw = base64.b64decode(encoded, validate=True)
            if not raw or len(raw) > MAX_FILE:
                raise ValueError("file size")
            if suffix == ".zip":
                with zipfile.ZipFile(BytesIO(raw)) as archive:
                    entries = archive.infolist()
                    if not entries or len(entries) > 20 or sum(x.file_size for x in entries) > 16 * 1024 * 1024:
                        raise ValueError("zip size")
                    for entry in entries:
                        if entry.is_dir() or entry.flag_bits & 1 or "\\" in entry.filename or entry.filename.startswith("/") or ".." in Path(entry.filename).parts or Path(entry.filename).suffix.lower() not in (".log", ".txt", ".jsonl"):
                            raise ValueError("zip contents")
            elif b"\x00" in raw:
                raise ValueError("binary attachment")
            output.append((name, FILE_TYPES[suffix], raw))
        except (ValueError, OSError, zipfile.BadZipFile) as exc:
            raise ApiError(400, "附件损坏、过大或包含不支持的文件") from exc
    return output


def redact_log(value):
    if not isinstance(value, str):
        raise ApiError(400, "日志片段格式不正确")
    lines = value.splitlines()[-100:]
    result = []
    for line in lines:
        line = re.sub(r"[\x00-\x08\x0b-\x1f\x7f]", "", line)[:400]
        line = re.sub(r"(?i)(password|passwd|secret|token|api[_-]?key|authorization|cookie|qq|npid)\s*[:=]\s*[^\s,;]+", r"\1=[已隐藏]", line)
        line = re.sub(r"(?i)C:\\Users\\[^\\\s]+", r"C:\\Users\\[用户]", line)
        line = re.sub(r"(?i)[\w.+-]+@[\w.-]+\.[a-z]{2,}", "[邮箱已隐藏]", line)
        result.append(line)
    return "\n".join(result)[-20000:]


class RateLimiter:
    def __init__(self):
        self.events = defaultdict(deque)
        self.lock = threading.Lock()

    def check(self, kind, identity, limit, window):
        now = time.monotonic()
        key = (kind, identity)
        with self.lock:
            recent = self.events[key]
            while recent and recent[0] < now - window:
                recent.popleft()
            if len(recent) >= limit:
                raise ApiError(429, "操作太频繁，请稍后再试")
            recent.append(now)
            # Avoid an unbounded map if many source addresses visit once.
            if len(self.events) > 10000:
                self.events = defaultdict(deque, {k: v for k, v in self.events.items() if v and v[-1] > now - 86400})


class Forum:
    def __init__(self, data=DATA):
        data.mkdir(parents=True, exist_ok=True)
        self.db = sqlite3.connect(data / "forum.db", check_same_thread=False)
        self.db.row_factory = sqlite3.Row
        self.db.execute("PRAGMA journal_mode=WAL")
        self.db.execute("PRAGMA foreign_keys=ON")
        self.db.execute("PRAGMA busy_timeout=5000")
        self.db.executescript("""
            CREATE TABLE IF NOT EXISTS users (
                id INTEGER PRIMARY KEY, nickname TEXT NOT NULL,
                nickname_key TEXT NOT NULL UNIQUE, salt BLOB NOT NULL,
                password_hash BLOB NOT NULL, is_admin INTEGER NOT NULL DEFAULT 0,
                approved_count INTEGER NOT NULL DEFAULT 0, created_at INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS sessions (
                token_hash TEXT PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES users(id),
                csrf TEXT NOT NULL, expires_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS sessions_expires ON sessions(expires_at);
            CREATE TABLE IF NOT EXISTS threads (
                id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES users(id),
                category TEXT NOT NULL, title TEXT NOT NULL, body TEXT NOT NULL,
                status TEXT NOT NULL, created_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS threads_recent ON threads(status,created_at DESC);
            CREATE TABLE IF NOT EXISTS replies (
                id INTEGER PRIMARY KEY, thread_id INTEGER NOT NULL REFERENCES threads(id),
                user_id INTEGER NOT NULL REFERENCES users(id), body TEXT NOT NULL,
                status TEXT NOT NULL, created_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS replies_thread ON replies(thread_id,status,created_at);
            CREATE TABLE IF NOT EXISTS images (
                id TEXT PRIMARY KEY, owner_kind TEXT NOT NULL, owner_id INTEGER NOT NULL,
                data BLOB NOT NULL, created_at INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS files (
                id TEXT PRIMARY KEY, owner_kind TEXT NOT NULL, owner_id INTEGER NOT NULL,
                name TEXT NOT NULL, mime TEXT NOT NULL, data BLOB NOT NULL,
                admin_only INTEGER NOT NULL DEFAULT 0, created_at INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS reports (
                thread_id INTEGER PRIMARY KEY REFERENCES threads(id),
                qq TEXT NOT NULL
            );
        """)
        # A non-login author for user-consented crash submissions. Its random
        # credentials are never returned or written to logs.
        self.db.execute("INSERT OR IGNORE INTO users(id,nickname,nickname_key,salt,password_hash,approved_count,created_at) "
                        "VALUES(0,'启动器反馈','__launcher_reports__',?,?,1,?)",
                        (secrets.token_bytes(16), secrets.token_bytes(32), int(time.time())))
        self.db.commit()
        self.lock = threading.RLock()
        self.limiter = RateLimiter()

    def register(self, nickname, password):
        nickname = clean_text(nickname, 2, 24, "昵称")
        if not isinstance(password, str) or not 10 <= len(password) <= 128:
            raise ApiError(400, "密码需要 10–128 个字符")
        if any(ord(c) < 32 or c in "/\\<>\u200b" for c in nickname):
            raise ApiError(400, "昵称包含不支持的字符")
        salt = secrets.token_bytes(16)
        digest = hashlib.pbkdf2_hmac("sha256", password.encode("utf-8"), salt, 260000)
        with self.lock:
            try:
                cur = self.db.execute("INSERT INTO users(nickname,nickname_key,salt,password_hash,created_at) "
                                      "VALUES(?,?,?,?,?)", (nickname, nickname.casefold(), salt, digest, int(time.time())))
                self.db.commit()
            except sqlite3.IntegrityError as exc:
                raise ApiError(409, "昵称已被使用") from exc
        return cur.lastrowid

    def login(self, nickname, password):
        if not isinstance(nickname, str) or not isinstance(password, str):
            raise ApiError(401, "昵称或密码不正确")
        with self.lock:
            row = self.db.execute("SELECT * FROM users WHERE nickname_key=?", (nickname.strip().casefold(),)).fetchone()
        # Equalize the expensive path for unknown names as well.
        salt = row["salt"] if row else bytes(16)
        expected = row["password_hash"] if row else bytes(32)
        digest = hashlib.pbkdf2_hmac("sha256", password.encode("utf-8"), salt, 260000)
        if not hmac.compare_digest(digest, expected):
            raise ApiError(401, "昵称或密码不正确")
        return row["id"]

    def new_session(self, user_id):
        token = secrets.token_urlsafe(32)
        csrf = secrets.token_urlsafe(24)
        expires = int(time.time()) + SESSION_SECONDS
        with self.lock:
            self.db.execute("DELETE FROM sessions WHERE expires_at<?", (int(time.time()),))
            self.db.execute("INSERT INTO sessions VALUES(?,?,?,?)",
                            (hashlib.sha256(token.encode()).hexdigest(), user_id, csrf, expires))
            self.db.commit()
        return token, csrf

    def session(self, token):
        if not token or len(token) > 100:
            return None
        with self.lock:
            return self.db.execute("SELECT s.csrf,u.id,u.nickname,u.is_admin,u.approved_count "
                                   "FROM sessions s JOIN users u ON u.id=s.user_id "
                                   "WHERE s.token_hash=? AND s.expires_at>?",
                                   (hashlib.sha256(token.encode()).hexdigest(), int(time.time()))).fetchone()

    def logout(self, token):
        if token:
            with self.lock:
                self.db.execute("DELETE FROM sessions WHERE token_hash=?", (hashlib.sha256(token.encode()).hexdigest(),))
                self.db.commit()

    def make_post(self, kind, user_id, body, images, category=None, title=None, thread_id=None, files=None):
        body = clean_text(body, 2 if kind == "reply" else 10, 5000, "正文")
        image_data = prepare_images(images)
        file_data = prepare_files(files or [])
        if kind == "thread":
            title = clean_text(title, 5, 100, "标题")
            if category not in CATEGORIES:
                raise ApiError(400, "请选择分类")
        now = int(time.time())
        with self.lock:
            user = self.db.execute("SELECT approved_count FROM users WHERE id=?", (user_id,)).fetchone()
            if not user:
                raise ApiError(401, "请重新登录")
            used = self.db.execute("SELECT COALESCE(SUM(LENGTH(i.data)),0) FROM images i "
                                   "LEFT JOIN threads t ON i.owner_kind='thread' AND i.owner_id=t.id "
                                   "LEFT JOIN replies r ON i.owner_kind='reply' AND i.owner_id=r.id "
                                   "WHERE t.user_id=? OR r.user_id=?", (user_id, user_id)).fetchone()[0]
            if used + sum(len(data) for data in image_data) > MAX_USER_IMAGE_BYTES:
                raise ApiError(413, "图片存储额度已满，请联系管理员")
            used_files = self.db.execute("SELECT COALESCE(SUM(LENGTH(f.data)),0) FROM files f "
                                         "LEFT JOIN threads t ON f.owner_kind='thread' AND f.owner_id=t.id "
                                         "LEFT JOIN replies r ON f.owner_kind='reply' AND f.owner_id=r.id "
                                         "WHERE t.user_id=? OR r.user_id=?", (user_id, user_id)).fetchone()[0]
            if used_files + sum(len(data) for _, _, data in file_data) > MAX_USER_FILE_BYTES:
                raise ApiError(413, "附件存储额度已满，请联系管理员")
            status = "visible" if user["approved_count"] else "pending"
            if kind == "reply":
                parent = self.db.execute("SELECT status FROM threads WHERE id=?", (thread_id,)).fetchone()
                if not parent or parent["status"] != "visible":
                    raise ApiError(404, "讨论不存在或尚未公开")
            with self.db:
                if kind == "thread":
                    cur = self.db.execute("INSERT INTO threads(user_id,category,title,body,status,created_at) "
                                          "VALUES(?,?,?,?,?,?)", (user_id, category, title, body, status, now))
                else:
                    cur = self.db.execute("INSERT INTO replies(thread_id,user_id,body,status,created_at) "
                                          "VALUES(?,?,?,?,?)", (thread_id, user_id, body, status, now))
                post_id = cur.lastrowid
                for data in image_data:
                    self.db.execute("INSERT INTO images VALUES(?,?,?,?,?)",
                                    (secrets.token_hex(16), kind, post_id, data, now))
                for name, mime, data in file_data:
                    self.db.execute("INSERT INTO files VALUES(?,?,?,?,?,?,?,?)",
                                    (secrets.token_hex(16), kind, post_id, name, mime, data, 0, now))
        return {"id": post_id, "status": status}

    def make_report(self, body):
        qq = body.get("qq")
        if not isinstance(qq, str) or not re.fullmatch(r"[1-9][0-9]{4,11}", qq):
            raise ApiError(400, "请填写有效的 QQ 号")
        version = clean_text(body.get("version", "未知"), 1, 32, "版本")
        if not re.fullmatch(r"[\w.\-]{1,32}", version):
            raise ApiError(400, "版本格式不正确")
        code = body.get("exit_code")
        if type(code) is not int or abs(code) > 4294967295:
            raise ApiError(400, "退出代码不正确")
        summary = clean_text(body.get("summary"), 3, 500, "报错摘要")
        public_tail = redact_log(body.get("tail"))
        raw_files = prepare_files(body.get("files", []), limit=1)
        if len(raw_files) != 1 or not raw_files[0][0].lower().endswith((".log", ".zip")):
            raise ApiError(400, "请附上本次运行的 LOG 或 ZIP 日志")
        safe_summary = redact_log(summary).replace("\n", " ")[:300]
        text = f"启动器版本：{version}\n退出代码：{code}\n报错摘要：{safe_summary}\n\n运行日志末尾 100 行（已脱敏）：\n{public_tail or '无'}"
        text = text.replace(qq, "[QQ已隐藏]")
        title = f"[启动器报错] {version} · 退出代码 {code}"
        now = int(time.time())
        with self.lock, self.db:
            cur = self.db.execute("INSERT INTO threads(user_id,category,title,body,status,created_at) "
                                  "VALUES(0,'求助',?,?,'visible',?)", (title, text, now))
            post_id = cur.lastrowid
            self.db.execute("INSERT INTO reports VALUES(?,?)", (post_id, qq))
            name, mime, data = raw_files[0]
            self.db.execute("INSERT INTO files VALUES(?,?,?,?,?,?,?,?)",
                            (secrets.token_hex(16), "thread", post_id, name, mime, data, 1, now))
        return {"id": post_id, "status": "visible"}

    def _image_ids(self, kind, post_id):
        return [row[0] for row in self.db.execute("SELECT id FROM images WHERE owner_kind=? AND owner_id=?",
                                                  (kind, post_id)).fetchall()]

    def _file_list(self, kind, post_id, viewer=None):
        admin = bool(viewer and viewer["is_admin"])
        return [dict(row) for row in self.db.execute(
            "SELECT id,name,admin_only FROM files WHERE owner_kind=? AND owner_id=? AND (admin_only=0 OR ?)",
            (kind, post_id, admin)).fetchall()]

    def list_threads(self, category=None, query="", mine=None):
        if category and category not in CATEGORIES:
            raise ApiError(400, "分类不存在")
        query = clean_text(query, 0, 80, "搜索词") if query else ""
        conditions = ["t.status='visible'" if mine is None else "t.user_id=?"]
        values = [] if mine is None else [mine]
        if category:
            conditions.append("t.category=?")
            values.append(category)
        if query:
            conditions.append("(t.title LIKE ? OR t.body LIKE ?)")
            values += ["%" + query + "%"] * 2
        sql = "SELECT t.id,t.category,t.title,t.body,t.status,t.created_at,u.nickname," \
              "(SELECT COUNT(*) FROM replies r WHERE r.thread_id=t.id AND r.status='visible') AS reply_count " \
              "FROM threads t JOIN users u ON u.id=t.user_id WHERE " + " AND ".join(conditions) + \
              " ORDER BY t.created_at DESC LIMIT 50"
        with self.lock:
            return [dict(row) for row in self.db.execute(sql, values).fetchall()]

    def thread(self, thread_id, viewer=None):
        with self.lock:
            row = self.db.execute("SELECT t.*,u.nickname FROM threads t JOIN users u ON u.id=t.user_id "
                                  "WHERE t.id=?", (thread_id,)).fetchone()
            if not row or row["status"] != "visible" and not (viewer and
                    (viewer["is_admin"] or viewer["id"] == row["user_id"])):
                raise ApiError(404, "讨论不存在或尚未公开")
            result = dict(row)
            result["images"] = self._image_ids("thread", thread_id)
            result["files"] = self._file_list("thread", thread_id, viewer)
            if viewer and viewer["is_admin"]:
                report = self.db.execute("SELECT qq FROM reports WHERE thread_id=?", (thread_id,)).fetchone()
                if report:
                    result["report_qq"] = report["qq"]
            replies = self.db.execute("SELECT r.*,u.nickname FROM replies r JOIN users u ON u.id=r.user_id "
                                      "WHERE r.thread_id=? ORDER BY r.created_at,r.id", (thread_id,)).fetchall()
            result["replies"] = []
            for reply in replies:
                if reply["status"] == "visible" or viewer and (viewer["is_admin"] or viewer["id"] == reply["user_id"]):
                    item = dict(reply)
                    item["images"] = self._image_ids("reply", reply["id"])
                    item["files"] = self._file_list("reply", reply["id"], viewer)
                    result["replies"].append(item)
            return result

    def image(self, image_id, viewer=None):
        with self.lock:
            row = self.db.execute("SELECT * FROM images WHERE id=?", (image_id,)).fetchone()
            if not row:
                raise ApiError(404, "图片不存在")
            table = "threads" if row["owner_kind"] == "thread" else "replies"
            owner = self.db.execute(f"SELECT status,user_id FROM {table} WHERE id=?", (row["owner_id"],)).fetchone()
            if not owner or owner["status"] != "visible" and not (viewer and
                    (viewer["is_admin"] or viewer["id"] == owner["user_id"])):
                raise ApiError(404, "图片不存在")
            if row["owner_kind"] == "reply":
                parent = self.db.execute("SELECT t.status,t.user_id FROM threads t JOIN replies r "
                                         "ON r.thread_id=t.id WHERE r.id=?", (row["owner_id"],)).fetchone()
                if not parent or parent["status"] != "visible" and not (viewer and
                        (viewer["is_admin"] or viewer["id"] == parent["user_id"])):
                    raise ApiError(404, "图片不存在")
            return row["data"]

    def file(self, file_id, viewer=None):
        with self.lock:
            row = self.db.execute("SELECT * FROM files WHERE id=?", (file_id,)).fetchone()
            if not row or row["admin_only"] and not (viewer and viewer["is_admin"]):
                raise ApiError(404, "附件不存在")
            table = "threads" if row["owner_kind"] == "thread" else "replies"
            owner = self.db.execute(f"SELECT status,user_id FROM {table} WHERE id=?", (row["owner_id"],)).fetchone()
            if not owner or owner["status"] != "visible" and not (viewer and
                    (viewer["is_admin"] or viewer["id"] == owner["user_id"])):
                raise ApiError(404, "附件不存在")
            if row["owner_kind"] == "reply":
                parent = self.db.execute("SELECT t.status,t.user_id FROM threads t JOIN replies r "
                                         "ON r.thread_id=t.id WHERE r.id=?", (row["owner_id"],)).fetchone()
                if not parent or parent["status"] != "visible" and not (viewer and
                        (viewer["is_admin"] or viewer["id"] == parent["user_id"])):
                    raise ApiError(404, "附件不存在")
            return row

    def pending(self, admin):
        if not admin or not admin["is_admin"]:
            raise ApiError(403, "需要管理员权限")
        with self.lock:
            threads = [dict(x) for x in self.db.execute(
                "SELECT t.id,t.title,t.body,t.created_at,u.nickname FROM threads t "
                "JOIN users u ON u.id=t.user_id WHERE t.status='pending' ORDER BY t.created_at LIMIT 100")]
            replies = [dict(x) for x in self.db.execute(
                "SELECT r.id,r.thread_id,r.body,r.created_at,u.nickname FROM replies r "
                "JOIN users u ON u.id=r.user_id WHERE r.status='pending' ORDER BY r.created_at LIMIT 100")]
            for item in threads:
                item["images"] = self._image_ids("thread", item["id"])
                item["files"] = self._file_list("thread", item["id"], admin)
            for item in replies:
                item["images"] = self._image_ids("reply", item["id"])
                item["files"] = self._file_list("reply", item["id"], admin)
            return {"threads": threads, "replies": replies}

    def moderate(self, admin, kind, post_id, decision):
        if not admin or not admin["is_admin"]:
            raise ApiError(403, "需要管理员权限")
        if kind not in ("thread", "reply") or decision not in ("approve", "reject", "hide"):
            raise ApiError(400, "审核操作不正确")
        table = "threads" if kind == "thread" else "replies"
        with self.lock, self.db:
            row = self.db.execute(f"SELECT status,user_id FROM {table} WHERE id=?", (post_id,)).fetchone()
            if not row or row["status"] == "hidden":
                raise ApiError(404, "内容不存在")
            if decision in ("approve", "reject") and row["status"] != "pending":
                raise ApiError(409, "内容已审核")
            new_status = "visible" if decision == "approve" else "hidden"
            self.db.execute(f"UPDATE {table} SET status=? WHERE id=?", (new_status, post_id))
            if decision == "approve":
                self.db.execute("UPDATE users SET approved_count=approved_count+1 WHERE id=?", (row["user_id"],))
        return {"ok": True}

    def promote(self, nickname):
        with self.lock, self.db:
            cur = self.db.execute("UPDATE users SET is_admin=1 WHERE nickname_key=?", (nickname.strip().casefold(),))
        if cur.rowcount != 1:
            raise ValueError("Forum account not found; register it first")


class Handler(BaseHTTPRequestHandler):
    forum = None
    origin = ORIGIN
    server_version = "BBPortForum/1.0"

    def log_message(self, format, *args):
        # Do not log cookies, uploaded content, IPs or user-supplied text.
        pass

    def send_json(self, status, value, cookie=None):
        data = json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Content-Length", str(len(data)))
        if cookie:
            self.send_header("Set-Cookie", cookie)
        self.end_headers()
        self.wfile.write(data)

    def token(self):
        try:
            cookie = SimpleCookie()
            cookie.load(self.headers.get("Cookie", ""))
            return cookie["bbport_forum"].value if "bbport_forum" in cookie else ""
        except Exception:
            return ""

    def viewer(self):
        return self.forum.session(self.token())

    def require_user(self):
        user = self.viewer()
        if not user:
            raise ApiError(401, "请先登录论坛")
        if not hmac.compare_digest(self.headers.get("X-CSRF-Token", ""), user["csrf"]):
            raise ApiError(403, "页面已过期，请刷新后重试")
        return user

    def read_json(self):
        if self.headers.get("Content-Type", "").split(";")[0] != "application/json":
            raise ApiError(415, "仅支持 JSON 请求")
        try:
            length = int(self.headers.get("Content-Length", ""))
        except ValueError as exc:
            raise ApiError(411, "请求缺少长度") from exc
        if length < 1 or length > MAX_BODY:
            raise ApiError(413, "内容超过上传限制")
        try:
            value = json.loads(self.rfile.read(length))
        except (ValueError, UnicodeDecodeError) as exc:
            raise ApiError(400, "请求内容无法读取") from exc
        if not isinstance(value, dict):
            raise ApiError(400, "请求格式不正确")
        return value

    def client_ip(self):
        return self.headers.get("X-Real-IP", self.client_address[0])[:80]

    def do_GET(self):
        try:
            path = urlsplit(self.path).path
            query = parse_qs(urlsplit(self.path).query)
            viewer = self.viewer()
            if path == "/healthz":
                return self.send_json(200, {"ok": True})
            if path == "/me":
                user = None if not viewer else {"id": viewer["id"], "nickname": viewer["nickname"],
                                                "is_admin": bool(viewer["is_admin"]), "csrf": viewer["csrf"]}
                return self.send_json(200, {"user": user})
            if path == "/threads":
                return self.send_json(200, {"threads": self.forum.list_threads(
                    query.get("category", [""])[0] or None, query.get("q", [""])[0])})
            if path == "/mine":
                if not viewer:
                    raise ApiError(401, "请先登录论坛")
                return self.send_json(200, {"threads": self.forum.list_threads(mine=viewer["id"])})
            if path == "/admin/pending":
                return self.send_json(200, self.forum.pending(viewer))
            match = re.fullmatch(r"/threads/([0-9]{1,9})", path)
            if match:
                return self.send_json(200, self.forum.thread(int(match[1]), viewer))
            match = re.fullmatch(r"/images/([a-f0-9]{32})", path)
            if match:
                data = self.forum.image(match[1], viewer)
                self.send_response(200)
                self.send_header("Content-Type", "image/webp")
                self.send_header("Cache-Control", "private, no-store")
                self.send_header("X-Content-Type-Options", "nosniff")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                return self.wfile.write(data)
            match = re.fullmatch(r"/files/([a-f0-9]{32})", path)
            if match:
                item = self.forum.file(match[1], viewer)
                data = item["data"]
                self.send_response(200)
                self.send_header("Content-Type", "application/octet-stream")
                self.send_header("Content-Disposition", "attachment; filename*=UTF-8''" + quote(item["name"]))
                self.send_header("Cache-Control", "private, no-store")
                self.send_header("X-Content-Type-Options", "nosniff")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                return self.wfile.write(data)
            raise ApiError(404, "页面不存在")
        except ApiError as exc:
            self.send_json(exc.status, {"error": str(exc)})
        except Exception:
            self.send_json(500, {"error": "服务暂时不可用"})

    def do_POST(self):
        try:
            path = urlsplit(self.path).path
            if path != "/reports" and self.headers.get("Origin") != self.origin:
                raise ApiError(403, "来源不正确")
            if path == "/reports" and self.headers.get("Origin") not in (None, "", self.origin):
                raise ApiError(403, "来源不正确")
            body = self.read_json()
            ip = self.client_ip()
            if path == "/reports":
                self.forum.limiter.check("report-hour", ip, 2, 3600)
                self.forum.limiter.check("report-day", ip, 6, 86400)
                self.forum.limiter.check("report-global", "all", 30, 3600)
                return self.send_json(201, self.forum.make_report(body))
            if path == "/register":
                self.forum.limiter.check("register", ip, 3, 86400)
                user_id = self.forum.register(body.get("nickname"), body.get("password"))
                token, _ = self.forum.new_session(user_id)
                return self.send_json(201, {"ok": True}, cookie=(
                    f"bbport_forum={token}; Path=/windows/; Max-Age={SESSION_SECONDS}; "
                    "HttpOnly; Secure; SameSite=Strict"))
            if path == "/login":
                self.forum.limiter.check("login", ip, 10, 900)
                user_id = self.forum.login(body.get("nickname"), body.get("password"))
                token, _ = self.forum.new_session(user_id)
                return self.send_json(200, {"ok": True}, cookie=(
                    f"bbport_forum={token}; Path=/windows/; Max-Age={SESSION_SECONDS}; "
                    "HttpOnly; Secure; SameSite=Strict"))
            user = self.require_user()
            if path == "/logout":
                self.forum.logout(self.token())
                return self.send_json(200, {"ok": True}, cookie=(
                    "bbport_forum=; Path=/windows/; Max-Age=0; HttpOnly; Secure; SameSite=Strict"))
            if path == "/threads":
                self.forum.limiter.check("thread", str(user["id"]), 4, 3600)
                return self.send_json(201, self.forum.make_post(
                    "thread", user["id"], body.get("body"), body.get("images", []),
                    category=body.get("category"), title=body.get("title"), files=body.get("files", [])))
            match = re.fullmatch(r"/threads/([0-9]{1,9})/replies", path)
            if match:
                self.forum.limiter.check("reply", str(user["id"]), 20, 3600)
                return self.send_json(201, self.forum.make_post(
                    "reply", user["id"], body.get("body"), body.get("images", []),
                    thread_id=int(match[1]), files=body.get("files", [])))
            if path == "/admin/moderate":
                kind = body.get("kind")
                post_id = body.get("id")
                decision = body.get("decision")
                if type(post_id) is not int or post_id < 1:
                    raise ApiError(400, "内容编号无效")
                return self.send_json(200, self.forum.moderate(user, kind, post_id, decision))
            raise ApiError(404, "接口不存在")
        except ApiError as exc:
            self.send_json(exc.status, {"error": str(exc)})
        except Exception:
            self.send_json(500, {"error": "服务暂时不可用"})


def main():
    forum = Forum()
    if len(sys.argv) == 3 and sys.argv[1] == "promote":
        forum.promote(sys.argv[2])
        print("Forum moderator enabled")
        return
    Handler.forum = forum
    port = int(os.environ.get("PORT", "8080"))
    ThreadingHTTPServer(("0.0.0.0", port), Handler).serve_forever()


if __name__ == "__main__":
    main()
