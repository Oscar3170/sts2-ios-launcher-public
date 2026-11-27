#!/usr/bin/env python3
"""Read-only web viewer for desktop test runs (scripts/desktop-test/run.sh and suite.sh).

Usage: python3 scripts/desktop-test/viewer.py [--host ADDRESS] [--port 8766] [--root PATH]

Serves 127.0.0.1 unless --host or STS2MOBILE_VIEWER_HOST names another address (for example a LAN address, to open
it from a phone). The default root is the checkout's .work/test-runs.
"""
import argparse, html, json, os, re, shutil, time, urllib.parse
from datetime import datetime
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(os.path.dirname(SCRIPT_DIR))
DEFAULT_ROOT = os.path.join(REPO_ROOT, ".work", "test-runs")
RUN_RE = re.compile(r"^(?P<scenario>.+)-(?P<stamp>\d{8}-\d{6})$")
TEXT_EXT = {".log", ".txt", ".json", ".tsv", ".csv", ".md"}
IMAGE_EXT = {".png": "image/png", ".jpg": "image/jpeg", ".jpeg": "image/jpeg",
             ".webp": "image/webp", ".gif": "image/gif"}
RUNNING_WINDOW = 120  # seconds since the newest file changed in the newest run folder

CSS = """
:root{color-scheme:light dark;--bg:#0d1117;--fg:#e6edf3;--muted:#8b949e;--line:#30363d;--card:#161b22;--link:#58a6ff;--pass:#3fb950;--fail:#f85149;--warn:#d29922}
@media(prefers-color-scheme:light){:root{--bg:#fff;--fg:#1f2328;--muted:#656d76;--line:#d0d7de;--card:#f6f8fa;--link:#0969da;--pass:#1a7f37;--fail:#cf222e;--warn:#9a6700}}
*{box-sizing:border-box}body{margin:0 auto;padding:24px;max-width:1200px;background:var(--bg);color:var(--fg);font:14px/1.5 system-ui,sans-serif}
a{color:var(--link);text-decoration:none}a:hover{text-decoration:underline}nav{margin-bottom:8px}h1{font-size:1.6rem;margin:.2em 0 .4em;word-break:break-word}h2{font-size:1.15rem;margin:1.6em 0 .5em}
.muted{color:var(--muted)}code{background:var(--card);padding:.1em .3em;border-radius:4px;font-size:.9em}
table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:6px 10px;border-bottom:1px solid var(--line);vertical-align:top}
thead th{color:var(--muted);font-weight:600;font-size:.85em;text-transform:uppercase;letter-spacing:.03em}tr:hover td{background:var(--card)}
.badge{display:inline-block;padding:1px 8px;border-radius:999px;font-size:.8em;font-weight:600;border:1px solid;white-space:nowrap}
.badge.pass{color:var(--pass);border-color:var(--pass)}.badge.fail{color:var(--fail);border-color:var(--fail)}.badge.aborted{color:var(--warn);border-color:var(--warn)}.badge.running{color:var(--link);border-color:var(--link)}
.pass{color:var(--pass)}.fail{color:var(--fail)}.warn{color:var(--warn)}.run{color:var(--link)}table.kv{max-width:900px}table.kv th{width:180px;color:var(--muted);font-weight:500}
pre{background:var(--card);border:1px solid var(--line);border-radius:6px;padding:10px;overflow:auto;white-space:pre-wrap;word-break:break-word}
pre.logbox{font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;font-size:12px;line-height:1.4;max-height:420px;overflow:auto}
details{margin:.4em 0}summary{cursor:pointer;color:var(--link)}details.error summary{color:var(--fail)}td.detail{font-size:.92em;white-space:pre-wrap;word-break:break-word}
.shots{display:grid;grid-template-columns:repeat(auto-fill,minmax(min(360px,100%),1fr));gap:18px}.shots figure{margin:0;background:var(--card);border:1px solid var(--line);border-radius:8px;padding:8px}
.shots img{display:block;width:100%;max-height:460px;object-fit:contain;object-position:top;background:#000;border-radius:4px;cursor:zoom-in}
figcaption{margin-top:6px;display:flex;flex-direction:column;gap:4px;align-items:flex-start}.fname{font-size:.82em;color:var(--muted);word-break:break-all}
button.copy{font:inherit;font-size:.78em;padding:2px 8px;border:1px solid var(--line);border-radius:6px;background:transparent;color:var(--fg);cursor:pointer;white-space:nowrap}
button.copy:hover{border-color:var(--link);color:var(--link)}ul.files{padding-left:1.2em}ul.files li{margin:.15em 0}
.stats{margin:.2em 0;color:var(--muted)}
.lb{position:fixed;inset:0;z-index:100;background:rgba(0,0,0,.94);display:flex;flex-direction:column;align-items:center;justify-content:center;gap:10px;padding:14px}
.lb[hidden]{display:none}.lb img{max-width:96vw;max-height:82vh;object-fit:contain;cursor:zoom-out}
.lb-cap{display:flex;gap:10px;align-items:center;color:#e6edf3;font-size:.9em;word-break:break-all}
.lb .copy{color:#e6edf3;border-color:#666}.lb-close{position:absolute;z-index:2;top:8px;right:14px;font-size:1.8rem;line-height:1;background:none;border:0;color:#fff;cursor:pointer}
.lb-nav{position:absolute;top:50%;transform:translateY(-50%);font-size:2.4rem;line-height:1;background:none;border:0;color:#fff;cursor:pointer;padding:6px 12px}
.lb-prev{left:8px}.lb-next{right:8px}
"""
JS = """
function copyPath(btn, text) {
  const done = () => { const old = btn.textContent; btn.textContent = "Copied"; setTimeout(() => { btn.textContent = old; }, 1200); };
  const fallback = () => { const ta = document.createElement("textarea");
    ta.value = text; ta.setAttribute("readonly", ""); ta.style.position = "fixed"; ta.style.opacity = "0";
    document.body.appendChild(ta); ta.select();
    try { document.execCommand("copy"); done(); } catch (e) {} finally { ta.remove(); } };
  if (navigator.clipboard && navigator.clipboard.writeText) navigator.clipboard.writeText(text).then(done, fallback);
  else fallback();
}
let lbShots = [], lbIndex = 0;
function lbShow(n) {
  const lb = document.getElementById("lightbox");
  lbShots = Array.from(document.querySelectorAll("a.shot"));
  if (!lb || !lbShots.length) return;
  lbIndex = (n + lbShots.length) % lbShots.length;
  const a = lbShots[lbIndex];
  const img = document.getElementById("lb-img"), name = document.getElementById("lb-name");
  const copy = document.getElementById("lb-copy");
  img.src = a.href; img.alt = a.dataset.name || "";
  name.textContent = a.dataset.name || ""; copy.dataset.path = a.dataset.path || "";
  lb.hidden = false; document.body.style.overflow = "hidden";
}
function lbClose() {
  const lb = document.getElementById("lightbox");
  if (lb) lb.hidden = true;
  document.body.style.overflow = "";
}
function scrollLogs() {
  document.querySelectorAll("pre[data-autoscroll]").forEach((el) => { el.scrollTop = el.scrollHeight; });
}
document.addEventListener("click", (e) => {
  const nav = e.target.closest(".lb-prev, .lb-next, .lb-close");
  if (nav) {
    e.preventDefault();
    if (nav.classList.contains("lb-close")) lbClose();
    else lbShow(lbIndex + (nav.classList.contains("lb-next") ? 1 : -1));
    return;
  }
  const shot = e.target.closest("a.shot");
  if (shot) {
    e.preventDefault();
    lbShots = Array.from(document.querySelectorAll("a.shot"));
    lbShow(lbShots.indexOf(shot));
  }
});
document.addEventListener("keydown", (e) => {
  const lb = document.getElementById("lightbox");
  if (!lb || lb.hidden) return;
  if (e.key === "Escape") lbClose();
  else if (e.key === "ArrowLeft") lbShow(lbIndex - 1);
  else if (e.key === "ArrowRight") lbShow(lbIndex + 1);
});
function poll(url, selector, every, stopWhenIdle) {
  const target = document.querySelector(selector);
  if (!target) return;
  let timer = null, busy = false;
  const stop = () => { if (timer) { clearInterval(timer); timer = null; } };
  const load = async () => {
    if (busy || document.visibilityState !== "visible") return;
    const lb = document.getElementById("lightbox");
    if (lb && !lb.hidden) return;
    busy = true;
    try {
      const res = await fetch(url, { cache: "no-store" });
      if (!res.ok) return;
      const doc = new DOMParser().parseFromString(await res.text(), "text/html");
      const next = doc.querySelector(selector);
      if (!next) { stop(); return; }
      const y = window.scrollY;
      target.innerHTML = next.innerHTML;
      target.dataset.running = next.dataset.running || "";
      window.scrollTo(0, y);
      scrollLogs();
      if (stopWhenIdle && target.dataset.running !== "1") stop();
    } catch (err) {
    } finally { busy = false; }
  };
  const start = () => {
    if (stopWhenIdle && target.dataset.running !== "1") return;
    if (!timer) timer = setInterval(load, every);
  };
  if (document.visibilityState === "visible") start();
  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "visible") { load(); start(); } else stop();
  });
}
poll(location.href, "#index-live", 5000, false);
if (document.querySelector("#run-live[data-running='1']")) poll(location.href, "#run-live", 5000, true);
scrollLogs();
"""
LIGHTBOX = """
<div id="lightbox" class="lb" hidden onclick="if(event.target===this)lbClose()">
  <button type="button" class="lb-close" aria-label="Close" title="Close" onclick="lbClose()">×</button>
  <button type="button" class="lb-nav lb-prev" aria-label="Previous">‹</button>
  <img id="lb-img" alt="">
  <div class="lb-cap"><span id="lb-name"></span>
    <button type="button" class="copy" id="lb-copy" onclick="copyPath(this, this.dataset.path)">Copy path</button></div>
  <button type="button" class="lb-nav lb-next" aria-label="Next">›</button>
</div>
"""

def h(value):
    return html.escape(str(value), quote=True)

def fmt(value):
    if isinstance(value, bool):
        return "true" if value else "false"
    if value is None:
        return "—"
    if isinstance(value, (dict, list)):
        return json.dumps(value, ensure_ascii=False)
    return str(value)

def human(size):
    size = float(size)
    for unit in ("B", "KB", "MB", "GB"):
        if size < 1024:
            return f"{size:.0f} {unit}" if unit == "B" else f"{size:.1f} {unit}"
        size /= 1024
    return f"{size:.1f} TB"

def repo_relative(path):
    rel = os.path.relpath(path, REPO_ROOT)
    return path if rel == os.pardir or rel.startswith(os.pardir + os.sep) else rel

def copy_button(path):
    rel = repo_relative(path)
    return (f'<button type="button" class="copy" data-path="{h(rel)}" '
            f'title="Copy {h(rel)} (absolute: {h(path)})" '
            f'onclick="copyPath(this, this.dataset.path)">Copy path</button>')

def href_run(name):
    return "/r/" + urllib.parse.quote(name, safe="")

def href_scenario(name):
    return "/s/" + urllib.parse.quote(name, safe="")

def href_file(folder, name):
    return "/files/" + urllib.parse.quote(folder, safe="") + "/" + urllib.parse.quote(name, safe="/")

def page(title, body, nav=False):
    nav_html = '<nav><a href="/">← all scenarios</a></nav>' if nav else ""
    return (f'<!doctype html>\n<html lang="en"><head><meta charset="utf-8">'
            '<meta name="viewport" content="width=device-width, initial-scale=1">'
            f'<title>{h(title)}</title><style>{CSS}</style></head>\n<body>{nav_html}<h1>{h(title)}</h1>\n'
            f'{body}\n<script>{JS}</script></body></html>')

def table(headers, rows, css_class=""):
    head = "".join(f"<th>{h(item)}</th>" for item in headers)
    attrs = f' class="{css_class}"' if css_class else ""
    return f'<table{attrs}><thead><tr>{head}</tr></thead><tbody>{rows}</tbody></table>'

def scan(root):
    runs = []
    try:
        entries = list(os.scandir(root))
    except OSError:
        return runs
    for entry in entries:
        match = RUN_RE.match(entry.name)
        if not match or not entry.is_dir(follow_symlinks=False):
            continue
        try:
            stamp = datetime.strptime(match.group("stamp"), "%Y%m%d-%H%M%S")
        except ValueError:
            continue
        runs.append({"name": entry.name, "dir": entry.path, "scenario": match.group("scenario"), "dt": stamp})
    runs.sort(key=lambda run: (run["dt"], run["name"]), reverse=True)
    return runs

def badge(summary):
    return f'<span class="badge {summary["status"]}">{h(summary["label"])}</span>'

def window_cell(summary):
    if not summary["window"]:
        return '<span class="muted">unknown</span>'
    extra = []
    if summary["insets"] not in (None, ""):
        extra.append(f"insets {h(summary['insets'])}")
    if summary["portrait"] is True:
        extra.append("portrait")
    elif summary["portrait"] is False:
        extra.append("landscape")
    suffix = f'<br><span class="muted">{" · ".join(extra)}</span>' if extra else ""
    return h(summary["window"]) + suffix

def steps_cell(summary):
    if summary["status"] == "aborted":
        return '<span class="muted">—</span>'
    if summary["status"] == "running":
        return '<span class="run">in progress</span>'
    parts = [f'<span class="pass">{summary["passed"]} passed</span>']
    if summary["failed"]:
        parts.append(f'<span class="fail">{summary["failed"]} failed</span>')
    if summary["skipped"]:
        parts.append(f'<span class="muted">{summary["skipped"]} skipped</span>')
    parts.append(f'<span class="muted">of {summary["total"]}</span>')
    return "<br>".join(parts)

def duration(data):
    try:
        started = datetime.fromisoformat(str(data["StartedAt"]))
        finished = datetime.fromisoformat(str(data["FinishedAt"]))
        return f"{(finished - started).total_seconds():.1f}s"
    except (KeyError, ValueError, TypeError):
        return None

def newest_mtime(run_dir):
    newest = 0.0
    try:
        with os.scandir(run_dir) as entries:
            for entry in entries:
                if entry.is_file(follow_symlinks=False):
                    try:
                        newest = max(newest, entry.stat(follow_symlinks=False).st_mtime)
                    except OSError:
                        pass
    except OSError:
        pass
    if not newest:
        try:
            newest = os.stat(run_dir).st_mtime
        except OSError:
            pass
    return newest

def read_tail(path, max_bytes=512 * 1024, max_lines=2000):
    try:
        with open(path, "rb") as handle:
            handle.seek(0, os.SEEK_END)
            size = handle.tell()
            handle.seek(max(0, size - max_bytes))
            text = handle.read().decode("utf-8", "replace")
    except OSError:
        return "", False, 0
    truncated = size > max_bytes
    lines = text.splitlines()
    if truncated and lines:
        lines = lines[1:]
    if len(lines) > max_lines:
        lines = lines[-max_lines:]
        truncated = True
    return "\n".join(lines), truncated, size

class Viewer(BaseHTTPRequestHandler):
    root = DEFAULT_ROOT
    protocol_version = "HTTP/1.1"

    def do_GET(self):
        self.respond(True)

    def do_HEAD(self):
        self.respond(False)

    def respond(self, body_ok):
        self._results, self._newest = {}, ""
        split = urllib.parse.urlsplit(self.path)
        path = urllib.parse.unquote(split.path)
        if path.startswith("/files/"):
            return self.serve_file(path[7:], body_ok)
        if path in ("/", "/index.html"):
            query = urllib.parse.parse_qs(split.query)
            body, code = self.view_index(query.get("window", [""])[0], query.get("sort", ["time"])[0])
        elif path.startswith("/s/"):
            body, code = self.view_scenario(path[3:])
        elif path.startswith("/r/"):
            body, code = self.view_run(path[3:])
        elif path.startswith("/g/"):
            body, code = self.view_gallery(path[3:])
        else:
            body, code = page("Not found", '<p>No such page. <a href="/">All scenarios</a></p>'), 404
        self.send_body(code, "text/html; charset=utf-8", body.encode("utf-8"), body_ok)

    def send_body(self, code, content_type, data, body_ok=True):
        self.send_response(code)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        if body_ok:
            try:
                self.wfile.write(data)
            except (BrokenPipeError, ConnectionResetError):
                pass

    def safe_target(self, rel):
        if not rel or "\x00" in rel:
            return None
        root = os.path.realpath(self.root)
        target = os.path.realpath(os.path.join(root, rel.lstrip("/")))
        try:
            return target if os.path.commonpath((root, target)) == root else None
        except ValueError:
            return None

    def serve_file(self, rel, body_ok):
        target = self.safe_target(rel)
        if target is None:
            return self.send_body(403, "text/plain; charset=utf-8", b"Forbidden\n", body_ok)
        if not os.path.isfile(target):
            return self.send_body(404, "text/plain; charset=utf-8", b"Not found\n", body_ok)
        ext = os.path.splitext(target)[1].lower()
        content_type = IMAGE_EXT.get(ext)
        if content_type is None:
            content_type = "text/plain; charset=utf-8" if ext in TEXT_EXT else "application/octet-stream"
        try:
            handle = open(target, "rb")
        except OSError:
            return self.send_body(404, "text/plain; charset=utf-8", b"Not found\n", body_ok)
        with handle:
            self.send_response(200)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(os.fstat(handle.fileno()).st_size))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            if body_ok:
                try:
                    shutil.copyfileobj(handle, self.wfile, 64 * 1024)
                except (BrokenPipeError, ConnectionResetError):
                    pass

    def results_for(self, run):
        key = run["name"]
        if key not in getattr(self, "_results", {}):
            data = None
            try:
                with open(os.path.join(run["dir"], "RESULTS.json"), encoding="utf-8") as handle:
                    loaded = json.load(handle)
                if isinstance(loaded, dict):
                    data = loaded
            except (OSError, ValueError):
                pass
            self._results[key] = data
        return self._results[key]

    def window_of(self, run):
        data = self.results_for(run)
        return data.get("Window") if data else None

    def summarize(self, run):
        data = self.results_for(run)
        steps = data.get("Steps") if data else None
        if not isinstance(steps, list):
            steps = []
        passed = sum(1 for step in steps if isinstance(step, dict) and step.get("Status") == "passed")
        failed = sum(1 for step in steps if isinstance(step, dict) and step.get("Status") == "failed")
        if data is None:
            running = (run["name"] == getattr(self, "_newest", "")
                       and time.time() - newest_mtime(run["dir"]) <= RUNNING_WINDOW)
            status, label = ("running", "running") if running else ("aborted", "aborted")
        else:
            status, label = ("pass", "pass") if data.get("Passed") is True else ("fail", "fail")
        return {"name": run["name"], "scenario": run["scenario"], "dt": run["dt"], "data": data,
                "status": status, "label": label,
                "window": data.get("Window") if data else None,
                "insets": data.get("Insets") if data else None,
                "portrait": data.get("Portrait") if data else None,
                "passed": passed, "failed": failed,
                "skipped": len(steps) - passed - failed, "total": len(steps)}

    def list_files(self, run_dir):
        screenshots, texts = [], []
        try:
            with os.scandir(run_dir) as entries:
                for entry in entries:
                    if not entry.is_file(follow_symlinks=False):
                        continue
                    try:
                        size = entry.stat(follow_symlinks=False).st_size
                    except OSError:
                        size = 0
                    (screenshots if entry.name.lower().endswith(".png") else texts).append((entry.name, size))
        except OSError:
            pass
        return sorted(screenshots), sorted(texts)

    def folder_dir(self, name):
        if not name or name in (".", "..") or "/" in name or "\\" in name or RUN_RE.match(name):
            return None
        path = os.path.join(self.root, name)
        if os.path.islink(path) or not os.path.isdir(path):
            return None
        return path

    def gallery_images(self, folder_dir):
        images = []
        for base, dirs, files in os.walk(folder_dir):
            dirs[:] = [name for name in dirs if not os.path.islink(os.path.join(base, name))]
            for name in files:
                path = os.path.join(base, name)
                if os.path.islink(path) or os.path.splitext(name)[1].lower() not in IMAGE_EXT:
                    continue
                try:
                    size = os.path.getsize(path)
                except OSError:
                    continue
                images.append((os.path.relpath(path, folder_dir), size))
        return sorted(images, key=lambda item: item[0].lower())

    def other_folders(self):
        folders = []
        try:
            entries = list(os.scandir(self.root))
        except OSError:
            return folders
        for entry in entries:
            if not entry.is_dir(follow_symlinks=False) or RUN_RE.match(entry.name):
                continue
            images = self.gallery_images(entry.path)
            if images:
                folders.append((entry.name, len(images)))
        return sorted(folders, key=lambda item: item[0].lower())

    def shots_html(self, folder, folder_dir, images):
        parts = ['<div class="shots">']
        for name, _size in images:
            url = h(href_file(folder, name))
            path = os.path.join(folder_dir, name)
            parts.append(
                f'<figure><a class="shot" href="{url}" data-name="{h(name)}" '
                f'data-path="{h(repo_relative(path))}">'
                f'<img loading="lazy" src="{url}" alt="{h(name)}"></a>'
                f'<figcaption><span class="fname">{h(name)}</span>{copy_button(path)}</figcaption></figure>')
        parts.append("</div>")
        parts.append(LIGHTBOX)
        return "".join(parts)

    def view_index(self, window_filter, sort="time"):
        sort = sort if sort in ("time", "name") else "time"
        all_runs = scan(self.root)
        self._newest = all_runs[0]["name"] if all_runs else ""
        windows = sorted({window for window in (self.window_of(run) for run in all_runs) if window})
        runs = all_runs
        if window_filter:
            runs = [run for run in runs if self.window_of(run) == window_filter]
        groups = {}
        for run in runs:
            groups.setdefault(run["scenario"], []).append(run)
        rows = []
        for group in groups.values():
            summary = self.summarize(group[0])
            summary["n_runs"] = len(group)
            rows.append(summary)
        if sort == "name":
            rows.sort(key=lambda summary: summary["scenario"].lower())
        else:
            rows.sort(key=lambda summary: summary["dt"], reverse=True)
        counts = {status: sum(1 for summary in rows if summary["status"] == status)
                  for status in ("pass", "fail", "aborted", "running")}

        def index_url(window=None, sort_value="time", with_sort=False):
            params = {}
            if window:
                params["window"] = window
            if with_sort or sort_value != "time":
                params["sort"] = sort_value
            return "/" + ("?" + urllib.parse.urlencode(params) if params else "")

        def window_link(value):
            label = h(value) if value else "all"
            if (value or "") == (window_filter or ""):
                return f"<strong>{label}</strong>"
            return f'<a href="{index_url(value, sort)}">{label}</a>'

        def sort_link(value):
            label = h(value)
            if value == sort:
                return f"<strong>{label}</strong>"
            return f'<a href="{index_url(window_filter, value, True)}">{label}</a>'

        stats = (f'<p class="stats"><strong>{len(rows)}</strong> scenarios · '
                 f'<span class="pass">{counts["pass"]} passing</span> · '
                 f'<span class="fail">{counts["fail"]} failing</span> · '
                 f'<span class="warn">{counts["aborted"]} aborted</span> · '
                 f'<span class="run">{counts["running"]} running</span></p>')
        filter_links = " · ".join([window_link(None)] + [window_link(window) for window in windows])
        body = [stats,
                f'<p class="muted">{len(runs)} runs · root <code>{h(self.root)}</code></p>',
                f'<p class="muted">Window: {filter_links}</p>',
                f'<p class="muted">Sort: {sort_link("time")} · {sort_link("name")}</p>']
        html_rows = []
        for summary in rows:
            plural = "run" if summary["n_runs"] == 1 else "runs"
            html_rows.append(
                f'<tr><td><a href="{href_scenario(summary["scenario"])}">{h(summary["scenario"])}</a></td>'
                f'<td><a href="{href_run(summary["name"])}">{summary["dt"]:%Y-%m-%d %H:%M:%S}</a></td>'
                f'<td>{badge(summary)}</td><td>{window_cell(summary)}</td><td>{steps_cell(summary)}</td>'
                f'<td><a href="{href_scenario(summary["scenario"])}">{summary["n_runs"]} {plural}</a></td>'
                f'<td>{copy_button(os.path.join(self.root, summary["name"]))}</td></tr>')
        body.append(table(["Scenario", "Latest run", "Status", "Window / insets", "Steps", "History", "Path"],
                          "".join(html_rows)) if rows else "<p>No runs found.</p>")
        others = self.other_folders()
        if others:
            items = "".join(
                f'<li><a href="/g/{urllib.parse.quote(name, safe="")}">{h(name)}</a> '
                f'<span class="muted">{count} image{"s" if count != 1 else ""}</span> '
                f'{copy_button(os.path.join(self.root, name))}</li>'
                for name, count in others)
            body.append(f'<h2>Other folders</h2><ul class="files">{items}</ul>')
        return page("Test runs", '<div id="index-live">' + "\n".join(body) + "</div>"), 200

    def view_scenario(self, scenario):
        all_runs = scan(self.root)
        self._newest = all_runs[0]["name"] if all_runs else ""
        runs = [run for run in all_runs if run["scenario"] == scenario]
        if not runs:
            return page("Not found", '<p>No such scenario. <a href="/">All scenarios</a></p>'), 404
        html_rows = []
        for summary in (self.summarize(run) for run in runs):
            html_rows.append(
                f'<tr><td><a href="{href_run(summary["name"])}">{summary["dt"]:%Y-%m-%d %H:%M:%S}</a></td>'
                f'<td>{badge(summary)}</td><td>{window_cell(summary)}</td><td>{steps_cell(summary)}</td>'
                f'<td><code>{h(summary["name"])}</code></td>'
                f'<td>{copy_button(os.path.join(self.root, summary["name"]))}</td></tr>')
        body = [f'<p class="muted">{len(runs)} run(s), newest first. <a href="/">All scenarios</a></p>',
                table(["Run", "Status", "Window / insets", "Steps", "Folder", "Path"], "".join(html_rows))]
        return page(scenario, "\n".join(body), nav=True), 200

    def view_gallery(self, folder):
        folder_dir = self.folder_dir(folder)
        images = self.gallery_images(folder_dir) if folder_dir else []
        if not images:
            return page("Not found", '<p>No such image folder. <a href="/">All scenarios</a></p>'), 404
        body = [f'<p class="muted">{len(images)} image(s) · {copy_button(folder_dir)}</p>',
                self.shots_html(folder, folder_dir, images)]
        return page(folder, "\n".join(body), nav=True), 200

    def view_run(self, folder):
        all_runs = scan(self.root)
        self._newest = all_runs[0]["name"] if all_runs else ""
        run = next((run for run in all_runs if run["name"] == folder), None)
        if run is None:
            return page("Not found", '<p>No such run. <a href="/">All scenarios</a></p>'), 404
        data = self.results_for(run)
        summary = self.summarize(run)
        run_dir = os.path.join(self.root, folder)
        title = f'{summary["scenario"]} · {summary["dt"]:%Y-%m-%d %H:%M:%S}'
        body = [f'<p>{badge(summary)} {copy_button(run_dir)} '
                f'<a href="{href_scenario(summary["scenario"])}">all runs</a></p>']
        if data is None and summary["status"] == "running":
            body.append('<p><strong>Run in progress</strong> — no RESULTS.json yet. Logs and screenshots '
                        'written so far are below.</p>')
        elif data is None:
            body.append('<p><strong>No RESULTS.json</strong> in this run directory — it was aborted before '
                        'results were written. Logs and any screenshots are listed below.</p>')
        else:
            kv = "".join(f"<tr><th>{h(key)}</th><td>{h(fmt(value))}</td></tr>"
                         for key, value in data.items() if key not in ("Steps", "Error"))
            run_duration = duration(data)
            if run_duration:
                kv += f"<tr><th>Duration</th><td>{h(run_duration)}</td></tr>"
            body.append(f'<table class="kv"><tbody>{kv}</tbody></table>')
            if data.get("Error"):
                body.append(f'<details class="error" open><summary>Error</summary><pre>{h(data["Error"])}</pre></details>')
            steps = data.get("Steps") if isinstance(data.get("Steps"), list) else []
            failed = [step for step in steps if isinstance(step, dict) and step.get("Status") == "failed"]
            body.append(f"<h2>Failed steps ({len(failed)})</h2>")
            if failed:
                body.append(table(["Step", "Detail"], "".join(
                    f'<tr><td class="fail">{h(step.get("Name", ""))}</td>'
                    f'<td class="detail">{h(step.get("Detail", ""))}</td></tr>' for step in failed)))
            else:
                body.append('<p class="muted">None.</p>')
            body.append(f"<h2>All steps ({len(steps)})</h2>")
            if steps:
                body.append(f'<details><summary>Show all {len(steps)} steps</summary>' + table(
                    ["Status", "Step", "Detail"], "".join(
                        f'<tr><td>{h(step.get("Status", ""))}</td><td>{h(step.get("Name", ""))}</td>'
                        f'<td class="detail">{h(step.get("Detail", ""))}</td></tr>'
                        for step in steps if isinstance(step, dict))) + "</details>")
            else:
                body.append('<p class="muted">No steps recorded.</p>')
        logs = []
        for log_name in ("process.log", "game.log", "broker-build.log"):
            path = os.path.join(run_dir, log_name)
            if os.path.isfile(path):
                text, truncated, size = read_tail(path)
                logs.append((log_name, text, truncated, size))
        body.append("<h2>Logs</h2>")
        if logs:
            for log_name, text, truncated, size in logs:
                live_log = summary["status"] == "running" and log_name == "process.log"
                open_attr = " open" if live_log else ""
                auto_attr = ' data-autoscroll="1"' if live_log else ""
                note = " · showing tail" if truncated else ""
                body.append(
                    f'<details class="log"{open_attr}><summary>{h(log_name)} '
                    f'<span class="muted">({human(size)}{note})</span> '
                    f'<a href="{h(href_file(folder, log_name))}">full file</a></summary>'
                    f'<pre class="logbox"{auto_attr}>{h(text)}</pre></details>')
        else:
            body.append('<p class="muted">No process.log, game.log or broker-build.log.</p>')
        screenshots, texts = self.list_files(run_dir)
        body.append(f"<h2>Screenshots ({len(screenshots)})</h2>")
        body.append(self.shots_html(folder, run_dir, screenshots) if screenshots else '<p class="muted">None.</p>')
        body.append(f"<h2>Text files ({len(texts)})</h2>")
        if texts:
            body.append('<ul class="files">' + "".join(
                f'<li><a href="{h(href_file(folder, name))}">{h(name)}</a> '
                f'<span class="muted">{human(size)}</span></li>' for name, size in texts) + "</ul>")
        else:
            body.append('<p class="muted">None.</p>')
        live_attr = ' data-running="1"' if summary["status"] == "running" else ""
        return page(title, f'<div id="run-live"{live_attr}>' + "\n".join(body) + "</div>", nav=True), 200

def main(argv=None):
    parser = argparse.ArgumentParser(description="Read-only viewer for desktop test runs.")
    host = os.environ.get("STS2MOBILE_VIEWER_HOST", "127.0.0.1")
    parser.add_argument("--host", default=host, help=f"bind address (default: {host})")
    parser.add_argument("--port", type=int, default=8766, help="port (default: 8766)")
    parser.add_argument("--root", default=DEFAULT_ROOT, help="test-runs directory")
    args = parser.parse_args(argv)
    root = os.path.realpath(args.root)
    if not os.path.isdir(root):
        parser.error(f"root is not a directory: {root}")
    Viewer.root = root
    server = ThreadingHTTPServer((args.host, args.port), Viewer)
    print(f"Test runs viewer: http://{args.host}:{args.port}/  (root: {root})", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()

if __name__ == "__main__":
    main()
