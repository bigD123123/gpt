"""Real HTTP transport tests against our own in-process server, no live AI."""
from http.server import BaseHTTPRequestHandler, HTTPServer
import json
import threading
import unittest
from urllib.error import HTTPError
from vision_agent.providers import _post


class Handler(BaseHTTPRequestHandler):
    seen = []
    def log_message(self, *args):
        pass

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        type(self).seen.append((self.path, body))
        if self.path == "/redirect":
            self.send_response(302)
            self.send_header("Location", "/leak")
            self.end_headers()
        else:
            self.send_response(200)
            self.end_headers()
            self.wfile.write(b"x" * 1_000_001 if self.path == "/large" else b'{"ok": true}')


class TransportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = HTTPServer(("127.0.0.1", 0), Handler)
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()
        cls.url = f"http://127.0.0.1:{cls.server.server_port}"

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join()

    def test_json_roundtrip(self):
        self.assertEqual(_post(self.url + "/ok", {"image": "dummy"}), {"ok": True})
        self.assertEqual(Handler.seen[-1], ("/ok", {"image": "dummy"}))

    def test_redirect_not_followed(self):
        with self.assertRaises(HTTPError):
            _post(self.url + "/redirect", {}, "not-a-real-key")
        self.assertFalse(any(path == "/leak" for path, _ in Handler.seen))

    def test_oversized_response_rejected(self):
        with self.assertRaises(ValueError):
            _post(self.url + "/large", {})
