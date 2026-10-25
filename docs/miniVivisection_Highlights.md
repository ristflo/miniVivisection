# miniVivisection Highlights

**Created:** 2026-10-11T11:13:49+03:00 (Asia/Riyadh)
**Updated:** 2026-10-11T11:45:12+03:00 (Asia/Riyadh) — added background for readers new to the project.
**Updated:** 2026-10-11T13:58:15+03:00 (Asia/Riyadh) — Grasshopper components that hang can now be paused, not only stopped.
**Generator:** Claude Code (Model: Claude Opus 5.5, claude-opus-5-5)

**Original prompt:**
> generate a short list of the highlights of our plugin / add a comparision rhino no plugin vs rhino plus plugin and write everything to a md file / reweite the highlights notes to also include a bit more context, but very brief, like why the plungin was written, because the integrated debugger is not good, that in working with geometry or shape ist is important to see the shape, ect. write is for someone unfamilliar with th project and the problem

**Context:**
- A short introduction to miniVivisection 0.2.0 (build 125) for readers who don't know the project.
- Covers why it exists, its highlights, and Rhino 8 with and without the plug-in.

## Why miniVivisection exists

[Rhino](https://www.rhino3d.com/) is a 3D modelling program used by architects and designers. Many of them write small Python scripts, in Rhino or in its visual programming tool Grasshopper, that generate or change shapes: points, curves, surfaces, meshes.

When such a script goes wrong, the question is almost always about a shape. Is the curve where I expect it? Which way does this vector point? Why does this surface twist? Rhino 8's built-in debugger can pause a script, but it shows the script's values only as text, such as "Curve" or a list of numbers. You can't see the shape, so you guess, add print statements, or bake test geometry by hand.

Other problems make it worse:

- While a script is paused, Rhino itself is blocked, so you can't even look around the model.
- A script that hangs can only be ended by closing Rhino.
- A Grasshopper component runs once for every item of its input data. When one run fails, Rhino doesn't say which one.

miniVivisection is a plug-in for Rhino 8 that fixes this. Its core idea is simple: **when the script pauses, show its values as geometry in the 3D view, and let you change them there.** Around that it adds the debugger features that were missing.

## Highlights

- **See values as shapes:** while a script is paused, its points, curves, meshes, planes and vectors are drawn in Rhino's 3D view, labeled with their names. Values also show in the code itself, as tooltips and at the ends of lines.
- **Fix values with Rhino's tools:** move or reshape a value and write it back into the paused script, then let it run on with the change. Undo works.
- **Rhino stays usable:** while the script waits, you can select, measure and run commands.
- **Pause at any time:** Ctrl+Alt+B pauses a running script at its next line, even one that hangs.
- **Errors you can look at:** when a script started from Rhino's command line crashes or is stopped, the editor opens at that line, with the values of that moment.
- **Smarter breakpoints:** stop only when a condition is true (for example `i > 100`), after a number of hits, or just log a message without stopping.
- **Threads:** for scripts that do several things at once, it shows where each thread is and finds the ones that are stuck.
- **Grasshopper:**
  - A slider change runs the component in the debugger and stops at your breakpoints.
  - A failing component names the runs that failed, and the data each one got, such as item `{0;3}[2]`.
  - One click debugs exactly that failing run.
  - A component that hangs can be paused with Ctrl+Alt+B, even if its editor was never opened; its editor comes up at that line.
- **V°Edit component:** edit Grasshopper geometry with Rhino's tools; the edits flow back into the definition and are saved with it.
- **Prompts on the paused script:** a Python prompt that sees the script's variables, a PowerShell prompt, and a chat with Claude (an AI assistant) that asks before it runs any code.
- **Lessons:** 17 script lessons and 7 Grasshopper lessons, each with a bug for the debugger to find.

## Rhino 8 alone vs. Rhino 8 with miniVivisection

| Task | Rhino 8 alone | With miniVivisection |
|---|---|---|
| See a paused script's geometry | Values as text in the Variables tab | Drawn in the 3D view with name labels; also in tooltips, at line ends, in a value tree |
| Change a value while paused | Not possible | Edit it with any Rhino tool and write it back into the script, with Undo |
| Keep a value for later | Not possible | Bake it into the model |
| Show your own types | Text only | Visualizers draw them |
| Breakpoints | Plain line breakpoints | Conditions, hit counts, log messages, stop once per line |
| Pause a running script | Only at a breakpoint | Ctrl+Alt+B, at the next line, even on Rhino's main thread |
| Use Rhino while paused | Rhino is blocked | Select, measure, run commands |
| A script from the command line stops or crashes | An error message on the command line | The editor opens at that line, with the call stack and values of that moment |
| Threads | No overview of the script's threads | Where each thread is, what it waits for; finds hangs and deadlocks |
| Grasshopper: debug on a slider change | Start each debug run by hand | Every recompute stops at your breakpoints |
| Grasshopper: which data failed | One error message on the component | Each failing run named by run, branch, item and line; copy them to a Panel |
| Grasshopper: debug one failing run | Step through every run until the bad one | Debug failing run N, or a condition like `gh_item == "{0;3}[2]"` |
| Grasshopper: a component hangs | Wait, or end Rhino | Ctrl+Alt+B pauses it at that line (or stops it), and its editor comes up |
| Grasshopper: recompute while paused | An "expired during a solution" error dialog | The recompute waits until the solution ends |
| Grasshopper geometry in Rhino | Bake, edit, set it back into the definition by hand | V°Edit: edit with Rhino's tools; the changes flow back and are saved |
| Prompts on the paused script | None | Python in the paused script, PowerShell, Claude (asks before running code) |
| Learning material | Rhino's general examples | 17 script lessons and 7 Grasshopper lessons, each with a bug to find |

miniVivisection 0.2.0 is experimental. It hooks into Rhino 8's internal editor code, so a Rhino update can break parts of it. Every feature can be turned off in its settings, or all at once.
