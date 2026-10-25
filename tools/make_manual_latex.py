"""Builds the LaTeX manual, docs/manual/miniVivisection_Manual.tex, into its PDF next to it, and copies
that PDF to miniVivisection_Manual.pdf, the manual that ships: the build puts it next to the .rhp and
into examples, tools/make_release.py into the release zip and the web pages.

Before LaTeX runs, two files are written from the source, so the manual stays current:
- docs/manual/generated/settings.tex -- every setting (MiniVivisectionSettings.All): its group, label,
  default and description, as the Options page shows them;
- docs/manual/generated/version.tex -- the version (Version.props) and the build number
  (build-number.txt, the last build's).

Needs LuaLaTeX (MiKTeX or TeX Live, with fontspec, KOMA-Script, tcolorbox, listings) and the
Source Serif 4 / Source Sans 3 / Source Code Pro fonts that come with them; Windows' Segoe UI
Symbol for a few symbols; Pillow (pip install pillow) for the screenshots' JPEG copies.

    python tools/make_manual_latex.py          # the PDF
    python tools/make_manual_latex.py --keep   # keep LaTeX's .aux/.log/.toc (to look at warnings)
    python tools/make_manual_latex.py --titles # instead: the title page designs, title_designs.pdf
"""
import datetime
import glob
import os
import re
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MANUAL = os.path.join(HERE, "docs", "manual")
SRC = os.path.join(HERE, "src", "miniVivisection")  # the plug-in's sources
GENERATED = os.path.join(MANUAL, "generated")
MAIN = "miniVivisection_Manual"

ENUMS = {  # choices made from enums (Logging.cs) and VNames
    "LogLevel": ["Off", "Error", "Warning", "Info", "Debug", "Trace"],
    "NameMark": ["°", "|", "∣ (on the line)", "| with thin spaces", "∣ with thin spaces"],
}
STRING = r'"((?:[^"\\]|\\.)*)"'


def main():
    global MAIN
    if "--titles" in sys.argv:
        MAIN = "title_designs"
    os.makedirs(GENERATED, exist_ok=True)
    jpeg_screenshots()
    write(os.path.join(GENERATED, "settings.tex"), settings_tex())
    write(os.path.join(GENERATED, "version.tex"), version_tex())
    lualatex = shutil.which("lualatex")
    if lualatex is None:
        sys.exit("LuaLaTeX wasn't found (MiKTeX or TeX Live).")
    for _ in range(3):  # the table of contents, the references, then the page numbers they moved
        run = subprocess.run([lualatex, "-interaction=nonstopmode", "-halt-on-error", MAIN + ".tex"],
                             cwd=MANUAL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                             encoding="utf-8", errors="replace")
        if run.returncode != 0:
            sys.stdout.buffer.write(run.stdout[-4000:].encode("utf-8", "replace"))
            sys.exit("LuaLaTeX failed; see " + os.path.join(MANUAL, MAIN + ".log"))
    if "--keep" not in sys.argv:
        for ext in (".aux", ".log", ".toc", ".out", ".lof"):
            path = os.path.join(MANUAL, MAIN + ext)
            if os.path.exists(path):
                os.remove(path)
    print("wrote " + os.path.join(MANUAL, MAIN + ".pdf"))
    if MAIN == "miniVivisection_Manual":
        # The shipped manual: the build copies it next to the .rhp and into examples, make_release.py into the zip
        # and the web pages.
        shutil.copy2(os.path.join(MANUAL, MAIN + ".pdf"), os.path.join(HERE, "docs", "miniVivisection_Manual.pdf"))
        print("copied to " + os.path.join(HERE, "docs", "miniVivisection_Manual.pdf"))


def jpeg_screenshots():
    """docs/screenshots/*.png as high-quality JPEGs in generated/jpg, which the PDFs use (style.tex's
    \\graphicspath): about 40 % smaller than the PNGs. Quality 90 with full color resolution (4:4:4),
    so colored code text stays sharp. The PNGs stay the originals; a JPEG is made again when its PNG is newer."""
    from PIL import Image  # pip install pillow
    target = os.path.join(GENERATED, "jpg")
    os.makedirs(target, exist_ok=True)
    for png in sorted(glob.glob(os.path.join(HERE, "docs", "screenshots", "*.png"))):
        jpg = os.path.join(target, os.path.splitext(os.path.basename(png))[0] + ".jpg")
        if not os.path.exists(jpg) or os.path.getmtime(jpg) < os.path.getmtime(png):
            Image.open(png).convert("RGB").save(jpg, "JPEG", quality=90, subsampling=0, optimize=True)


def write(path, text):
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def version_tex():
    props = open(os.path.join(HERE, "Version.props"), encoding="utf-8").read()
    version = re.search(r"<VersionPrefix>([^<]+)</VersionPrefix>", props).group(1)
    build_file = os.path.join(HERE, "build-number.txt")
    build = open(build_file).read().strip() if os.path.exists(build_file) else "?"
    today = datetime.date.today().isoformat()
    return ("% Written by tools/make_manual_latex.py -- don't edit.\n"
            "\\newcommand{\\mvversion}{" + version + "}\n"
            "\\newcommand{\\mvbuild}{" + build + "}\n"
            "\\newcommand{\\mvdate}{" + today + "}\n")


def constants(suffix):
    """MiniVivisectionSettings' string constants ending in suffix (Group, Tab): name -> text, in their order."""
    source = open(os.path.join(SRC, "MiniVivisectionSettings.cs"), encoding="utf-8").read()
    return dict(re.findall(r'public const string (\w+' + suffix + r') = "([^"]*)";', source))


def settings():
    """MiniVivisectionSettings.All, one dict per setting, in its order."""
    groups, tabs = constants("Group"), constants("Tab")
    source = open(os.path.join(SRC, "MiniVivisectionSettings.cs"), encoding="utf-8").read()
    body = source[source.index("All = new List<Setting>"):]
    body = body[:body.index("};")]
    found = []
    for line in body.splitlines():
        # A description that lists an enum's names: "Areas: " + string.Join(", ", Enum.GetNames(typeof(LogArea))) + "."
        line = re.sub(r'" \+ string\.Join\(", ", Enum\.GetNames\(typeof\((\w+)\)\)\) \+ "', lambda e: ", ".join(enum_names(e.group(1))), line)
        m = re.match(r"\s*(Bool|Int|Text|Path|Choice)\(" + STRING + r",\s*(\w+),\s*" + STRING + r",\s*" + STRING + r",\s*(.*)$", line)
        if not m:
            continue
        kind, key, group, label, description, rest = m.groups()
        if "hidden: true" in rest:
            continue  # the V AI and V Debug switches: the top bar
        if kind == "Choice":
            strings = [unescape(s) for s in re.findall(STRING, rest)]
            if key == "NameMark":
                choices, default = ENUMS["NameMark"], "°"
            elif "LogLevel" in rest:
                choices, default = ENUMS["LogLevel"], strings[0]
            else:
                default, choices = strings[0], strings[1:]
        else:
            value = rest.split(")")[0].split(",")[0].strip()
            choices = []
            if kind == "Bool":
                default = "on" if value == "true" else "off"
            elif kind == "Int":
                default = value
            else:
                default = unescape(value.strip('"')) or "(empty)"
        tab = re.search(r"\.In\((\w+Tab)\)", rest)
        found.append(dict(key=key, group=groups.get(group, group), label=unescape(label), description=unescape(description),
                          kind=kind, default=default, choices=choices,
                          tab=tabs[tab.group(1)] if tab else tabs["GeneralTab"],
                          new_windows="ForNewWindows" in rest))
    return found


def enum_names(name):
    """The member names of enum `name`, from the plug-in's C# sources."""
    for path in glob.glob(os.path.join(SRC, "*.cs")):
        source = open(path, encoding="utf-8").read()
        m = re.search(r"enum " + name + r"\s*\{([^}]*)\}", source)
        if m:
            return [w for w in re.findall(r"^\s*(\w+)", re.sub(r"//[^\n]*", "", m.group(1)), re.M)]
    raise RuntimeError("no enum " + name)


def unescape(text):
    text = text.replace('\\"', '"').replace("\\\\", "\\")
    return re.sub(r"\\u([0-9A-Fa-f]{4})", lambda m: chr(int(m.group(1), 16)), text)


def tex(text):
    """Plain text as LaTeX: special characters escaped, V names, quotes and dashes typeset."""
    text = re.sub(r"\bV (?=[A-Z])", "V°", text)
    out = []
    for ch in text:
        out.append({"\\": r"\textbackslash{}", "&": r"\&", "%": r"\%", "$": r"\$", "#": r"\#", "_": r"\_",
                    "{": r"\{", "}": r"\}", "~": r"\textasciitilde{}", "`": r"\textasciigrave{}", "^": r"\textasciicircum{}"}.get(ch, ch))
    text = "".join(out)
    text = re.sub(r'"([^"]*)"', r"“\1”", text)
    text = re.sub(r"(?<=\w)'(?=\w)", "’", text)
    text = re.sub(r"'([^']*)'", r"‘\1’", text)
    return text.replace(" -- ", " – ").replace("--", "–")


def settings_tex():
    lines = ["% Written by tools/make_manual_latex.py from MiniVivisectionSettings.cs -- don't edit.\n"]
    for tab in list(dict.fromkeys(s["tab"] for s in settings())):  # General, Rhino, Grasshopper: as the settings view has them
        groups = []
        for s in settings():
            if s["tab"] == tab and s["group"] not in groups:
                groups.append(s["group"])
        lines.append("\\section{Tab \\emph{" + tex(tab) + "}}\\label{set:" + tab.split()[0].lower() + "}\n")
        for group in groups:
            lines.append("\\needspace{8\\baselineskip}\\subsection*{" + tex(group) + "}\n")
            lines.append("\\begin{xltabular}{\\linewidth}{@{}P{0.3\\linewidth}L@{}}\n\\toprule\n"
                         "\\textsf{\\textbf{Setting}} & \\textsf{\\textbf{What it does}}\\\\\\midrule\\endhead\n")
            for s in settings():
                if s["tab"] != tab or s["group"] != group:
                    continue
                default = "\\textsf{\\small default: \\textbf{" + tex(s["default"]) + "}}"
                choices = (" \\newline\\textsf{\\small\\color{mvgray}" + " · ".join(tex(c) for c in s["choices"]) + "}") if s["choices"] else ""
                star = " \\textsf{\\small\\color{mvgray}(new windows)}" if s["new_windows"] else ""
                lines.append("\\textsf{\\textbf{" + tex(s["label"]) + "}}" + star + " \\newline " + default + choices +
                             " & " + tex(s["description"]) + "\\\\\n")
            lines.append("\\bottomrule\n\\end{xltabular}\n")
    return "".join(lines)


if __name__ == "__main__":
    main()
