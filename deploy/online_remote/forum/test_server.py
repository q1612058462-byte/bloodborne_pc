import base64
from io import BytesIO
import json
from http.server import ThreadingHTTPServer
from pathlib import Path
import tempfile
import threading
import unittest
from urllib.error import HTTPError
from urllib.request import Request, urlopen

from PIL import Image

from server import ApiError, Forum, Handler, MAX_USER_IMAGE_BYTES, prepare_images


def sample_image():
    output = BytesIO()
    image = Image.new("RGB", (8, 8), "#b2342f")
    image.save(output, format="PNG")
    return {"mime": "image/png", "data": base64.b64encode(output.getvalue()).decode()}


class ForumTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.forum = Forum(Path(self.temp.name))

    def tearDown(self):
        self.forum.db.close()
        self.temp.cleanup()

    def test_registration_session_and_first_post_review(self):
        hunter = self.forum.register("猎人甲", "hunter-password-123")
        self.assertEqual(self.forum.login("猎人甲", "hunter-password-123"), hunter)
        with self.assertRaises(ApiError):
            self.forum.register("猎人甲", "another-password")
        token, csrf = self.forum.new_session(hunter)
        self.assertEqual(self.forum.session(token)["csrf"], csrf)
        first = self.forum.make_post("thread", hunter, "这里是足够长的问题内容。", [sample_image()],
                                     category="联机", title="再次加入联机房间失败")
        self.assertEqual(first["status"], "pending")
        self.assertEqual(self.forum.list_threads(), [])
        with self.assertRaises(ApiError):
            self.forum.thread(first["id"])
        own = self.forum.thread(first["id"], self.forum.session(token))
        self.assertEqual(len(own["images"]), 1)
        with self.assertRaises(ApiError):
            self.forum.image(own["images"][0])
        self.assertTrue(self.forum.image(own["images"][0], self.forum.session(token)).startswith(b"RIFF"))

        admin = self.forum.register("管理员", "admin-password-123")
        self.forum.promote("管理员")
        admin_token, _ = self.forum.new_session(admin)
        moderator = self.forum.session(admin_token)
        self.assertEqual(len(self.forum.pending(moderator)["threads"]), 1)
        self.forum.moderate(moderator, "thread", first["id"], "approve")
        self.assertEqual(self.forum.list_threads()[0]["id"], first["id"])
        self.assertTrue(self.forum.image(own["images"][0]).startswith(b"RIFF"))

        second = self.forum.make_post("thread", hunter, "第二个主题不再需要审核。", [],
                                      category="求助", title="第二次测试联机")
        self.assertEqual(second["status"], "visible")
        reply = self.forum.make_post("reply", admin, "我试着排查。", [], thread_id=first["id"])
        self.assertEqual(reply["status"], "pending")
        self.forum.moderate(moderator, "reply", reply["id"], "approve")
        self.assertEqual(len(self.forum.thread(first["id"])["replies"]), 1)
        self.forum.logout(token)
        self.assertIsNone(self.forum.session(token))

    def test_image_validation_and_rejection(self):
        result = prepare_images([sample_image()])
        self.assertEqual(len(result), 1)
        with self.assertRaises(ApiError):
            prepare_images([{"mime": "image/svg+xml", "data": base64.b64encode(b"<svg/>").decode()}])
        with self.assertRaises(ApiError):
            prepare_images([sample_image()] * 3)
        with self.assertRaises(ApiError):
            prepare_images([{"mime": "image/png", "data": "not-base64"}])

    def test_nickname_controls_and_image_quota(self):
        with self.assertRaises(ApiError):
            self.forum.register("bad\nname", "hunter-password-123")
        hunter = self.forum.register("猎人甲", "hunter-password-123")
        self.forum.make_post("thread", hunter, "这里是足够长的问题内容。", [],
                             category="联机", title="截图上传额度测试")
        self.forum.db.execute("INSERT INTO images VALUES(?,?,?,?,?)",
                              ("a" * 32, "thread", 1, b"x" * MAX_USER_IMAGE_BYTES, 0))
        self.forum.db.commit()
        with self.assertRaises(ApiError) as error:
            self.forum.make_post("thread", hunter, "这里是第二个问题内容。", [sample_image()],
                                 category="联机", title="额度用尽时拒绝上传")
        self.assertEqual(error.exception.status, 413)

    def test_rate_limit(self):
        for _ in range(3):
            self.forum.limiter.check("register", "127.0.0.1", 3, 3600)
        with self.assertRaises(ApiError) as ctx:
            self.forum.limiter.check("register", "127.0.0.1", 3, 3600)
        self.assertEqual(ctx.exception.status, 429)

    def test_http_origin_cookie_and_csrf(self):
        Handler.forum = self.forum
        Handler.origin = "http://forum.test"
        httpd = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        thread = threading.Thread(target=httpd.serve_forever, daemon=True)
        thread.start()
        base = f"http://127.0.0.1:{httpd.server_port}"

        def call(path, body=None, headers=None):
            headers = headers or {}
            if body is not None:
                headers = {"Content-Type": "application/json", "Origin": Handler.origin, **headers}
                body = json.dumps(body).encode()
            request = Request(base + path, data=body, headers=headers)
            with urlopen(request, timeout=3) as response:
                return response.status, dict(response.headers), json.load(response)

        try:
            self.assertEqual(call("/healthz")[2], {"ok": True})
            with self.assertRaises(HTTPError) as wrong_origin:
                call("/register", {"nickname": "测试猎人", "password": "strong-password"},
                     {"Origin": "https://evil.invalid"})
            self.assertEqual(wrong_origin.exception.code, 403)
            status, headers, _ = call("/register", {"nickname": "测试猎人", "password": "strong-password"})
            self.assertEqual(status, 201)
            cookie = headers["Set-Cookie"].split(";", 1)[0]
            self.assertIn("HttpOnly", headers["Set-Cookie"])
            self.assertIn("Secure", headers["Set-Cookie"])
            me = call("/me", headers={"Cookie": cookie})[2]["user"]
            self.assertEqual(me["nickname"], "测试猎人")
            with self.assertRaises(HTTPError) as missing_csrf:
                call("/threads", {"category": "求助", "title": "无法进入联机房间",
                                  "body": "第二次加入房间没有响应。"}, {"Cookie": cookie})
            self.assertEqual(missing_csrf.exception.code, 403)
            status, _, post = call("/threads", {"category": "求助", "title": "无法进入联机房间",
                                               "body": "第二次加入房间没有响应。"},
                                   {"Cookie": cookie, "X-CSRF-Token": me["csrf"]})
            self.assertEqual(status, 201)
            self.assertEqual(post["status"], "pending")
            self.assertEqual(call("/threads")[2]["threads"], [])
        finally:
            httpd.shutdown()
            httpd.server_close()
            thread.join(timeout=3)


if __name__ == "__main__":
    unittest.main()
