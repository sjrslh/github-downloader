import http.server
import socketserver
import json
import os
import re
import sys

PORT = 8765
ASSET_SIZE = 20 * 1024 * 1024        # 20 MB，够看出进度和并发
SECOND_SIZE = 3 * 1024 * 1024

RELEASE = {
    "tag_name": "v9.9.9",
    "name": "Demo 9.9.9",
    "assets": [
        {"name": "demo-9.9.9-win-x64.zip",
         "browser_download_url": "http://127.0.0.1:%d/asset/win" % PORT,
         "size": ASSET_SIZE, "download_count": 1234},
        {"name": "demo-9.9.9-linux-x64.tar.gz",
         "browser_download_url": "http://127.0.0.1:%d/asset/linux" % PORT,
         "size": SECOND_SIZE, "download_count": 55},
        {"name": "demo-9.9.9-arm64.zip",
         "browser_download_url": "http://127.0.0.1:%d/asset/arm" % PORT,
         "size": SECOND_SIZE, "download_count": 7},
    ],
}

SIZES = {
    "/asset/win": ASSET_SIZE,
    "/asset/linux": SECOND_SIZE,
    "/asset/arm": SECOND_SIZE,
}


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        pass

    def _json(self, obj, code=200):
        body = json.dumps(obj).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(body)

    def _asset(self):
        path = self.path.split("?")[0]
        size = SIZES.get(path)
        if size is None:
            self.send_error(404)
            return

        rng = self.headers.get("Range")
        start, end = 0, size - 1
        partial = False
        if rng:
            m = re.match(r"bytes=(\d+)-(\d*)", rng)
            if m:
                start = int(m.group(1))
                if m.group(2):
                    end = int(m.group(2))
                end = min(end, size - 1)
                partial = True

        length = end - start + 1
        self.send_response(206 if partial else 200)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Length", str(length))
        if partial:
            self.send_header("Content-Range", "bytes %d-%d/%d" % (start, end, size))
        self.end_headers()

        if self.command == "HEAD":
            return

        chunk = b"\x5a" * 65536
        remaining = length
        while remaining > 0:
            n = min(len(chunk), remaining)
            try:
                self.wfile.write(chunk[:n])
            except (BrokenPipeError, ConnectionResetError):
                return
            remaining -= n

    def do_HEAD(self):
        path = self.path.split("?")[0]
        if path.startswith("/repos/"):
            self._json(RELEASE)
        else:
            self._asset()

    def do_GET(self):
        path = self.path.split("?")[0]
        if path.startswith("/repos/"):
            self._json(RELEASE)
        else:
            self._asset()


if __name__ == "__main__":
    socketserver.ThreadingTCPServer.allow_reuse_address = True
    with socketserver.ThreadingTCPServer(("127.0.0.1", PORT), Handler) as httpd:
        print("mock github api on %d" % PORT, flush=True)
        httpd.serve_forever()
