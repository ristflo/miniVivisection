"""Prints the examples guide to PDF, next to its Markdown: examples/00_Examples_Guide.md.

The manual's PDF isn't printed here any more: miniVivisection_Manual.pdf is the typeset LaTeX manual
(tools/make_manual_latex.py); miniVivisection_Manual.md stays the short manual V°Help opens.

Run after changing the guide, then build: the build copies it to the build folder. Needs the Python package `markdown` (pip install markdown) and Chrome or Edge,
which print the HTML to PDF in headless mode.

    python tools/make_manual_pdf.py
"""
import os
import subprocess
import sys
import tempfile

try:
    import markdown
except ImportError:
    sys.exit("The Python package 'markdown' is missing: pip install markdown")

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DOCUMENTS = [  # (Markdown, title)
    (os.path.join(HERE, "examples", "00_Examples_Guide.md"), "miniVivisection — Examples Guide"),
]
BROWSERS = [
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
]
CSS = """
@page { size: A4; margin: 14mm 15mm; }
body { font-family: 'Segoe UI', Arial, sans-serif; font-size: 9.6pt; line-height: 1.38; color: #1d1d1f; }
h1 { font-size: 19pt; margin: 0 0 6px; color: #1a4d8f; }
h2 { font-size: 12.5pt; margin: 14px 0 5px; padding-bottom: 2px; border-bottom: 1.5px solid #c9d6ea; color: #1a4d8f; page-break-after: avoid; }
p { margin: 4px 0; }
ul { margin: 3px 0 5px; padding-left: 18px; }
li { margin: 1.5px 0; }
code { font-family: Consolas, 'Cascadia Mono', monospace; font-size: 8.8pt; background: #f1f3f6; padding: 0 3px; border-radius: 3px; }
pre { background: #f1f3f6; padding: 6px 9px; border-radius: 4px; margin: 5px 0; page-break-inside: avoid; }
pre code { background: none; padding: 0; }
table { border-collapse: collapse; width: 100%; margin: 5px 0 7px; page-break-inside: avoid; }
th, td { border: 1px solid #d5dbe5; padding: 3px 7px; text-align: left; vertical-align: top; }
th { background: #eaf0f8; }
hr { border: none; border-top: 1px solid #d5dbe5; margin: 8px 0; }
"""


def main():
    browser = next((b for b in BROWSERS if os.path.exists(b)), None)
    if browser is None:
        sys.exit("Neither Chrome nor Edge was found to print the PDF.")
    for source, title in DOCUMENTS:
        print_pdf(browser, source, os.path.splitext(source)[0] + ".pdf", title)


def print_pdf(browser, source, target, title):
    text = open(source, encoding="utf-8").read()
    # Nested lists need four spaces in Python-Markdown: write sub-items that way.
    body = markdown.markdown(text, extensions=["tables", "fenced_code", "sane_lists"])
    html = ("<!doctype html><html><head><meta charset='utf-8'><title>" + title + "</title><style>"
            + CSS + "</style></head><body>" + body + "</body></html>")
    with tempfile.TemporaryDirectory() as work:
        page = os.path.join(work, "manual.html")
        open(page, "w", encoding="utf-8").write(html)
        subprocess.run([browser, "--headless=new", "--disable-gpu", "--no-pdf-header-footer",
                        "--user-data-dir=" + os.path.join(work, "profile"),  # not the browser already open
                        "--print-to-pdf=" + target, "file:///" + page.replace("\\", "/")],
                       check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    print("wrote " + target)


if __name__ == "__main__":
    main()
