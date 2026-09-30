from http.server import BaseHTTPRequestHandler, HTTPServer


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path != "/message":
            self.send_response(404)
            self.end_headers()
            return
        body = b"backend-ok"
        self.send_response(200)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, format, *args):
        pass


def serve(port):
    HTTPServer(("127.0.0.1", port), Handler).serve_forever()
