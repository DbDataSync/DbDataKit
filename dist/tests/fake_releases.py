"""Serves a folder of release packages the way GitHub's releases API lists them, for an install test on a machine (dist/tests/suite-systemd-e2e.sh).

  python3 -I dist/tests/fake_releases.py --packages <folder> --version <v> --repo <owner/repo> --port <p>

<folder> holds one folder per product, release-<product>/, each with its zips and SHA256SUMS, as a dry run of suite-release.yml leaves them. Each becomes
the release <product>/v<version>, whose assets are those files; install.sh --api-base http://127.0.0.1:<p> --repo <owner/repo> --version <v> installs from it.
"""

import argparse
import http.server
import json
import pathlib


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--packages", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--repo", required=True)
    parser.add_argument("--port", type=int, required=True)
    args = parser.parse_args()

    base = f"http://127.0.0.1:{args.port}"
    blobs = {}
    releases = []
    for folder in sorted(pathlib.Path(args.packages).glob("release-*")):
        product = folder.name[len("release-"):]
        tag = f"{product}/v{args.version}"
        assets = []
        for file in sorted(p for p in folder.iterdir() if p.is_file()):
            path = f"/dl/{product}/{file.name}"
            blobs[path] = file
            assets.append({"name": file.name, "browser_download_url": base + path})
        releases.append({"tag_name": tag, "name": tag, "draft": False, "prerelease": False, "assets": assets})

    class Handler(http.server.BaseHTTPRequestHandler):
        def log_message(self, *a):
            pass

        def do_GET(self):
            path, _, query = self.path.partition("?")
            if path == f"/repos/{args.repo}/releases":
                page = dict(kv.partition("=")[::2] for kv in query.split("&") if kv).get("page", "1")
                body = json.dumps(releases if page == "1" else [], separators=(",", ":")).encode()
            elif path in blobs:
                body = blobs[path].read_bytes()
            else:
                self.send_error(404)
                return
            self.send_response(200)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

    http.server.ThreadingHTTPServer(("127.0.0.1", args.port), Handler).serve_forever()


if __name__ == "__main__":
    main()
