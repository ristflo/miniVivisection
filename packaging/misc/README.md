# miniVivisection

A companion to the Rhino 8 Script Editor's debugger (Windows, Rhino 8.36 or later).

- **Start:** open the Script Editor (`ScriptEditor`); the top bar shows miniVivisection's version, the V°AI and V°Debug switches and V°Help, which opens the manual (`miniVivisection_Manual.md`, also as PDF, next to `miniVivisection.rhp`).
- **Examples:** the Script Editor's left panel, *Examples > miniVivisection*.
- **Settings:** Tools > Options > miniVivisection.
- **Your own `rhino_tools.json`** (which RhinoAI tools V°Claude may use): put it in the plug-in's settings folder, `%APPDATA%\McNeel\Rhinoceros\8.0\Plug-ins\miniVivisection (…)\settings`. Updates replace the copy next to the `.rhp`.
- **Installed from the zip before?** Rhino would find the plug-in twice. Close Rhino and run `miniVivisection_Remove_Old_Install.cmd` (here, and on the web page): it removes Rhino's registration of the zip copy and its Grasshopper link, after showing them, and keeps a backup. `-DeleteFiles` moves the zip copy's files out too, `-ResetSettings` the settings, `-ListOnly` only shows what it finds.
- **Web page:** https://restweiss.de/miniVivisection
- **License:** MIT (`LICENSE.txt`); third-party libraries in `THIRD-PARTY-NOTICES.txt`.
