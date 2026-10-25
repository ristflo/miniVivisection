# miniVivisection: project structure and naming

**Created:** 2026-10-25T19:20:36+03:00 (Asia/Riyadh)
**Updated:** 2026-10-25T20:44:05+03:00 (Asia/Riyadh) -- the pages' artwork (web/sources) public too; the pages link the public repository
**Generator:** Claude Code (Claude Opus 5.5, claude-opus-5-5)

**Original prompt:**
> first check the license, then do both steps, move to the new folder, develop a consistent file and folder naming strategy and project folder structure and adapt the existing code and file names folder names to it, follow your suggestions for the split in a private development repo and a public release repo, remove my email address from the public repo, use my github noreply email address, for now keep everything in experimental but prepare to remove it and its leftovers in other folders. keep the yak files in both new repos.

**Context:**
- miniVivisection moved out of `RhinoVivisection/experimental` into its own repository, `C:\Dev\miniVivisection`, with its history (git filter-repo).
- This document is the layout, the naming rules, the names kept on purpose, and the split into a private development and a public release repository.

## Two repositories

| | Private: development | Public: releases |
|---|---|---|
| GitHub | `ristflo/miniVivisection-dev` (private) | `ristflo/miniVivisection` (public) |
| Local | `C:\Dev\miniVivisection` | `C:\Dev\miniVivisection-public` (a clone, written only by the tool) |
| History | every commit since 2026-10-03 (from RhinoVivisection) | one commit per release, "miniVivisection 0.2.0-beta.N" |
| Author | your usual address | the GitHub noreply address, `27972398+ristflo@users.noreply.github.com` |
| Contents | everything | `tools/public_files.txt`: code, tests, examples, manuals, pages, packages |

All work happens in the private repository. After a release (`tools/make_release.py`), `python tools/publish_public.py` takes the release's commit (a tag, `v0.2.0-beta.N`), copies the files `tools/public_files.txt` names into the public clone, checks them (`tools/publish_public.py --check`: no personal address, no `C:\Users\...` path, no password or key, nothing over 20 MB) and commits and pushes one commit, tagged like the private one. A change someone offers on the public repository is carried over by hand (`git format-patch` there, `git am` here).

## Layout

```text
miniVivisection/
├── README.md                    the developer guide: build, test, release, how the code works
├── LICENSE                      MIT (also in the package, as misc/LICENSE.txt)
├── THIRD-PARTY-NOTICES.txt      Harmony, System.Text.Json
├── AGENTS.md                    rules for AI assistants (private)
├── miniVivisection.sln          the three projects
├── Version.props                version 0.2.0 and the build counter (build-number.txt: local)
├── src/
│   ├── miniVivisection/         the Rhino plug-in -> miniVivisection.rhp
│   │   ├── *.cs, Properties/
│   │   ├── python/              shipped Python: minivivisection_helpers.py (embedded),
│   │   │                        minivivisection_visualize.py, rhinoviv.py
│   │   ├── styles/              the default Markdown styles (CSS)
│   │   ├── toolbar/             miniVivisection.rui and its icons
│   │   └── rhino_tools.json     V°Claude's Rhino tools
│   └── miniVivisection.GH/      the Grasshopper part -> miniVivisection.GH.gha (built into the .rhp's bin)
├── tests/
│   ├── miniVivisection.Checks/  checks outside Rhino: dotnet run
│   ├── python/                  pytest, outside Rhino
│   ├── live/                    tests in a fresh Rhino: run_live.py
│   └── rhino/                   scripts to open in Rhino by hand: miniVivisection_Test.py (shipped), watch_test.py/.cs, hover_test.py
├── examples/                    the lessons (shipped): NN_*.py, grasshopper/ghN_*.gh, 00_overview.3dm, the guide
├── docs/
│   ├── miniVivisection_Manual.md / .pdf   the manuals that ship (V°Help opens the Markdown)
│   ├── manual/                  the LaTeX manual (chapters, generated/, screenshots in ../screenshots)
│   ├── screenshots/
│   ├── naming/                  the V° mark's samples
│   └── notes/                   plans, reviews, research, prompts (private)
├── web/
│   ├── pages/                   https://restweiss.de/miniVivisection: eight designs, the chooser, packages
│   └── sources/                 the pages' artwork (generated with ChatGPT Pro; the rights are the author's)
├── packaging/                   Yak: manifest.yml, icon.png, misc/ (README.md, the removal tool)
└── tools/                       make_*.py (build things), publish_*.py (upload things), examples_overview.py,
                                 public_files.txt; publish.local.json (local, the SFTP account)
```

What is built goes into `bin/` folders, never into git: `src/miniVivisection/bin/<Configuration>/net7.0` (the `.rhp`, the `.gha`, and everything that ships next to them) and `bin/yak` at the root (the packages). The packages that are released are kept in git where the pages offer them (`web/pages/*/minivivisection-*.yak`), in both repositories.

## Names

**The product** is *miniVivisection*: a small m in text, in file names the user sees (`miniVivisection.rhp`, `miniVivisection_Manual.pdf`), in folders and project files (`src/miniVivisection`, `miniVivisection.GH.csproj`). Yak's package names are lowercase: `minivivisection-<version>-rh8_36-win.yak`.

**Code** follows its language:

| What | Rule | Examples |
|---|---|---|
| C# namespaces | the project's name, PascalCase | `MiniVivisection`, `MiniVivisection.GH` |
| C# types named after the product | `MiniVivisection` + role | `MiniVivisectionPlugin`, `MiniVivisectionCommand`, `MiniVivisectionSettings`, `MiniVivisectionOptionsPage` |
| C# files | the type they hold | `MiniVivisectionSettings.cs`, `StoppedRuns.Record.cs` (a part of a type) |
| Python modules | snake_case, the product as prefix when shipped | `minivivisection_helpers.py`, `minivivisection_visualize.py`; `rhinoviv.py` is RhinoVivisection's API, kept |
| Python test files | `test_<module>.py`, live tests `t_<topic>.py`, screenshot scenes `shots_<where>.py` | `test_minivivisection_helpers.py`, `t_ghexamples.py` |
| Tools | a verb and what: `make_*` writes files, `publish_*` uploads | `make_manual_latex.py`, `publish_webpages.py` |
| Run-time names | lowercase, `minivivisection` first | Harmony ids `minivivisection.freepause`, the log `minivivisection.log`, the paused script's helpers `__minivivisection_*` |

**Documents:** what ships is `miniVivisection_<Topic>.<ext>` (`miniVivisection_Manual.pdf`); notes are `<Kind>_<Topic>.md` or `<Kind>_<YYYY-MM-DD>.md` with the kinds Plan, Review, Research, Prompt, Notes (`Review_2026-10-15.md`) and the provenance header of `AGENTS.md`. Lessons are `NN_<topic>.py` and `ghN_<topic>.gh`, numbered in learning order.

**Folders:** lowercase for kinds of things (`src`, `tests`, `docs`, `examples`, `web`, `tools`, `packaging`); a project's folder is its project's name (`miniVivisection.GH`).

## Names kept on purpose

These are saved somewhere -- in Rhino's settings, in the user's files, in released packages -- so a new name would lose or break them:

- **The GUIDs:** the plug-in's `c198ec17-3078-46a0-8f9b-94a40e9ba298` (Rhino keeps its settings by it), the Grasshopper assembly's and every component's (definitions name components by them).
- **Settings' keys** (`PluginOn`, `AiOn`, ...) and the Grasshopper chunk `MiniVivisectionShowAs` saved in Panels.
- **Layers** `Script Editor Watch::Baked` and `Script Editor Watch::Edit` in the users' models.
- **The temporary folder** `%TEMP%\rhinovivisection\minivivisection` (V°Log, the lessons' tools and the manual name it).
- **The old names in the removal tool** (`ScriptEditorWatchViz.rhp`, `.gha`): it finds old installs by them; and `scripteditorwatchviz.pth`, the old file `PythonPaths` deletes.
- **History:** `docs/notes` and the manual's "formerly ScriptEditorWatchViz" say what was.

## Leaving RhinoVivisection

Until it is removed there, `RhinoVivisection/experimental` is frozen at commit `5762c2e` (released 0.2.0-beta.365); `RhinoVivisection/Plan_Remove_miniVivisection.md` lists what goes then.
