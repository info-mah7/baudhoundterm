"""Write docs/html/HELP.html and MANUAL.html from the Markdown guides, for the app's Help menu.

They ship in the docs folder beside the exe, so Help works offline and without the source
repository. Run after editing docs/HELP.md or docs/MANUAL.md, then commit the result:
    python3 helpers/build_docs.py
Needs pandoc. The output is self-contained: no scripts, no fonts or files from the network.
"""
import html
import os
import re
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DOCS = os.path.join(ROOT, "docs")
OUT = os.path.join(DOCS, "html")
PAGES = {"HELP.md": "BaudHoundTerm: getting started", "MANUAL.md": "BaudHoundTerm user manual"}

CSS = """
:root { color-scheme: light dark; --fg: #1f2328; --bg: #ffffff; --muted: #59636e;
        --line: #d1d9e0; --code: #f6f8fa; --link: #0969da; }
@media (prefers-color-scheme: dark) {
  :root { --fg: #e6edf3; --bg: #0d1117; --muted: #9198a1; --line: #3d444d; --code: #151b23; --link: #4493f8; }
}
body { margin: 0; background: var(--bg); color: var(--fg);
       font: 15px/1.6 "Segoe UI", system-ui, sans-serif; }
main { max-width: 52rem; margin: 0 auto; padding: 1.5rem 1rem 4rem; }
a { color: var(--link); }
h1, h2 { border-bottom: 1px solid var(--line); padding-bottom: .3em; }
code, pre { font-family: "Cascadia Mono", Consolas, monospace; font-size: .9em; background: var(--code); }
code { padding: .1em .3em; border-radius: 4px; }
pre { padding: .8em 1em; overflow-x: auto; border-radius: 6px; }
pre code { padding: 0; }
table { border-collapse: collapse; display: block; overflow-x: auto; }
th, td { border: 1px solid var(--line); padding: .35em .7em; text-align: left; vertical-align: top; }
blockquote { margin: 0; padding: 0 1em; color: var(--muted); border-left: .25em solid var(--line); }
hr { border: 0; border-top: 1px solid var(--line); }
""".strip()


def pandoc():
    exe = shutil.which("pandoc") or os.path.expanduser("~/.local/bin/pandoc")
    if not os.path.exists(exe):
        sys.exit("pandoc not found: install it, or put it on PATH")
    return exe


def convert(exe, name, title):
    body = subprocess.run([exe, "-f", "gfm", "-t", "html5", os.path.join(DOCS, name)],
                          check=True, capture_output=True, text=True).stdout
    # Links between the guides point at the HTML copies.
    body = re.sub(r'href="(HELP|MANUAL)\.md(#[^"]*)?"', r'href="\1.html\2"', body)
    ids = set(re.findall(r'id="([^"]+)"', body))
    broken = [a for a in re.findall(r'href="#([^"]+)"', body) if a not in ids]
    if broken:
        sys.exit(f"{name}: links to missing anchors: {broken}")
    local = [h for h in re.findall(r'href="([^"]*)"', body)
             if ":" not in h and not h.startswith("#") and not h.split("#")[0].endswith(".html")]
    if local:
        sys.exit(f"{name}: relative links that will not exist beside the exe: {local}")
    return ("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n"
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n"
            f"<title>{html.escape(title)}</title>\n<style>\n{CSS}\n</style>\n</head>\n"
            f"<body>\n<main>\n{body}</main>\n</body>\n</html>\n")


def main():
    exe = pandoc()
    os.makedirs(OUT, exist_ok=True)
    for name, title in PAGES.items():
        target = os.path.join(OUT, name[:-3] + ".html")
        with open(target, "w", encoding="utf-8", newline="\n") as f:
            f.write(convert(exe, name, title))
        print(f"{target}: {os.path.getsize(target)} bytes")


if __name__ == "__main__":
    main()
