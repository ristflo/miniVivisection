using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;

namespace MiniVivisection
{
    /// <summary>
    /// All of the plug-in's settings, kept in its PersistentSettings: one table of
    /// descriptors -- key, group, label, description, kind, default -- from which the
    /// Options page is built, so no setting can be left off it; typed properties for the
    /// code. <see cref="Changed"/> tells the parts at run time. The MiniVivisection
    /// command changes the same values.
    /// </summary>
    internal static class MiniVivisectionSettings
    {
        public enum Kind
        {
            Bool,
            Int,
            Text,
            Path,
            Choice,
        }

        /// <summary>One setting, as the Options page shows it.</summary>
        public sealed class Setting
        {
            public string Key;
            public string Group;
            private string _label, _description;
            /// <summary>Its label, the V names in the chosen mark (VNames).</summary>
            public string Label { get => VNames.Text(_label); set => _label = value; }
            public string Description { get => VNames.Text(_description); set => _description = value; }
            public Kind Kind;
            public object Default;
            public string[] Choices = new string[0];
            /// <summary>Takes effect for Script Editor windows opened afterwards.</summary>
            public bool NewWindows;
            /// <summary>Not on the Settings and Options pages (the V AI and V Debug switches: the top bar).</summary>
            public bool Hidden;
            /// <summary>The settings view's tab: <see cref="GeneralTab"/>, <see cref="RhinoTab"/> or <see cref="GrasshopperTab"/>.</summary>
            public string Tab = GeneralTab;
            /// <summary>A Markdown style file's path: an Edit button beside it (MarkdownStyleEditor).</summary>
            public MarkdownStyle.Target? StyleFile;

            /// <summary>This setting, a path to a Markdown style file for <paramref name="target"/>.</summary>
            public Setting StyleFileOf(MarkdownStyle.Target target)
            {
                StyleFile = target;
                return this;
            }

            /// <summary>This setting, for Script Editor windows opened afterwards.</summary>
            public Setting ForNewWindows()
            {
                NewWindows = true;
                return this;
            }

            /// <summary>This setting on the settings view's tab <paramref name="tab"/>.</summary>
            public Setting In(string tab)
            {
                Tab = tab;
                return this;
            }
        }

        public const string GeneralGroup = "Watches and values";
        public const string VisualizerGroup = "Visualizers";
        public const string DebuggerGroup = "Debugger";
        public const string EditorGroup = "Editor tabs and pages";
        public const string ClaudeGroup = "Claude";
        public const string PowerShellGroup = "PowerShell";
        public const string MarkdownGroup = "Markdown";
        public const string LogGroup = "Logs";
        public const string RhinoWatchGroup = "Watches in Rhino";
        public const string SelectionGroup = "Selection";
        public const string RhinoRunGroup = "Running and pausing in Rhino";
        public const string GrasshopperGroup = "Script components and Panels";
        public const string GhWindowGroup = "The prompts window";
        public const string GhClaudeGroup = "V Claude in Grasshopper";
        public const string GhShellsGroup = "V Python and V PowerShell in Grasshopper";
        public const string HelpGroup = "Help and examples";
        public const string GhHelpGroup = "Help and examples in Grasshopper";

        /// <summary>The settings view's tabs: what the Script Editor and Grasshopper's script
        /// components share (and Markdown, Rhino's and Grasshopper's alike), what happens in
        /// Rhino, what happens in Grasshopper.</summary>
        public const string GeneralTab = "General";
        public const string RhinoTab = "Rhino";
        public const string GrasshopperTab = "Grasshopper";

        public static readonly IReadOnlyList<Setting> All = new List<Setting>
        {
            // --- tab General ------------------------------------------------------------------------------------------

            Bool("PluginOn", GeneralGroup, "Debugging features on", "The V Debug switch in the Script Editor's top bar. Off: the V Watch, V Threads, V Python and V PowerShell tabs, the V Watch, V Threads, V PowerShell and V Log pages, drawing, tooltip values, links and trees, inline values, the hover preview, watch expressions, conditional breakpoints, FreePause and the menu items are switched off in every Script Editor; on: all back as they were.", true, hidden: true),
            Bool("HoverPreview", GeneralGroup, "Hover preview", "While paused, show the geometry of the name under the mouse in the code.", true),
            Bool("HoverValues", GeneralGroup, "Values in the code's tooltips", "While paused, the tooltip over a name starts with its value (count = 5) and key facts.", true),
            Bool("TooltipActions", GeneralGroup, "Links in the tooltips", "Under a value in the code's tooltip: Watch in Rhino, Edit in Rhino, Bake (for geometry) and Tree (the value's members, in a small window).", true),
            Bool("InlineValues", GeneralGroup, "Values at the ends of lines", "While paused, the values of the variables on each line of the paused function (up to the paused line) in gray after the line, as in VS Code and PyCharm.", true),
            Bool("ShowChanges", GeneralGroup, "What changed since the last pause", "After a step: changed values in the inline values are colored, and the tooltip says what a value was before (\"was 5\").", true),
            Bool("WatchExpressions", GeneralGroup, "Expressions in the editor's Watch tab", "Evaluate any Python expression in the editor's own Watch tab, not only variable names.", true),

            Text("VisualizerScripts", VisualizerGroup, "Visualizer scripts", "Python files with @rhinoviv.visualize / @rhinoviv.inverse functions, separated by ';'. Run at the first pause of every run, in the paused script's context.", ""),
            Bool("ReloadVisualizersOnSave", VisualizerGroup, "Reload a visualizer script when it's saved", "While paused right away, else at the next pause.", true),

            Bool("ConditionalBreakpoints", DebuggerGroup, "Conditional breakpoints, hit counts and logpoints", "A breakpoint's condition, hit count or log message decides whether it pauses (right-click a line, or the Breakpoints tab). Off: every breakpoint pauses.", true),
            Bool("DebugRunButton", DebuggerGroup, "Run and Debug buttons", "Two fixed buttons instead of the editor's switching Run button: Run always runs normally (also with breakpoints), Debug always runs in the debugger (also without a breakpoint, so Break now can pause it). The editor's own button shows while debugging, as Continue. Off: the editor's Run button as it was.", true),
            Bool("KeepEditorWhileDebugging", DebuggerGroup, "Keep the Script Editor open while debugging", "The editor minimizes itself when a script runs and comes back at a pause. While debugging, and for scripts with '# async:true', it stays open, so V Threads (Where is everyone?, Break now) can be used while the script runs. A plain Run of other scripts still minimizes.", true),
            Choice("BreakHotkey", DebuggerGroup, "Break now hotkey", "Pauses a script running in the debugger at its next line -- also one on Rhino's UI thread, which handles no other keys while it runs. A Windows hotkey, taken only while a script runs.", "Ctrl+Alt+B", "Ctrl+Alt+B", "Ctrl+Shift+B", "Pause", "Ctrl+Break", "Off"),
            Bool("BreakInScript", DebuggerGroup, "Break now stops in the script only", "Break now (its hotkey, V Threads) pauses at the next line of the script itself: while the script is inside a module of its own or another file, it runs on until it's back in the script. Off: at the next line anywhere.", true),
            Bool("OncePerLine", DebuggerGroup, "Stop once per line", "A line that runs several times (a comprehension, a one-line loop) pauses once. Off: once per item, as pdb does.", true),
            Bool("ThreadTags", DebuggerGroup, "Logpoints name their thread", "A logpoint's line starts with its thread ([Worker-2] i=3) while the script runs threads of its own.", true),
            Bool("FreezeGuard", DebuggerGroup, "Freeze guard for threads", "A script without '# async:true' runs on Rhino's UI thread: a breakpoint hit in one of its worker threads would freeze Rhino. Such a pause is skipped, and V Log and the Terminal say why.", true),
            Int("PausedThreadTimeout", DebuggerGroup, "Wait for the paused script (ms)", "How long the tabs wait for the paused script's thread to answer (Watch tab expressions, members, Claude's tools).", 2000),

            Choice("NameMark", EditorGroup, "Mark in the V names", "What stands between V and the name: \u00B0, a bar, or a bar with thin spaces. For Script Editors opened afterwards, and V Edit and V Text components placed afterwards.", VNames.DefaultChoice, VNames.Marks.Select(m => m.Choice).ToArray()).ForNewWindows(),
            Int("TerminalFontSize", EditorGroup, "Prompt font size", "The text size of V Python, V PowerShell and V Claude. Also Ctrl+mouse wheel in any of them.", 10),
            Bool("ColorsPython", EditorGroup, "Syntax colors: V Python", "What you type in V Python, in the Script Editor's Python colors.", true),
            Bool("ColorsPowerShell", EditorGroup, "Syntax colors: V PowerShell", "What you type in V PowerShell, in the Script Editor's colors for PowerShell.", true),
            Bool("ColorsClaude", EditorGroup, "Syntax colors: V Claude", "Code blocks in Claude's answers (```python, ```powershell), in the Script Editor's colors.", true),
            Bool("TabRhinoWatch", EditorGroup, "Tab (bottom): V Watch", "V Watch at the bottom of the editor.", true),
            Bool("TabThreads", EditorGroup, "Tab (bottom): V Threads", "V Threads at the bottom of the editor: the script's threads, where each one is, its stack, Break now and \"Where is everyone?\".", true),
            Bool("TabPython", EditorGroup, "Tab (bottom): V Python", "V Python, the Python prompt, at the bottom of the editor.", true),
            Bool("TabPowerShell", EditorGroup, "Tab (bottom): V PowerShell", "V PowerShell, the PowerShell prompt, at the bottom of the editor.", true),
            Bool("TabClaude", EditorGroup, "Tab (bottom): V Claude", "V Claude, the Claude chat, at the bottom of the editor.", true),
            Bool("PageClaude", EditorGroup, "Page (left): V Claude", "V Claude in the editor's left panel.", true),
            Bool("PageRhinoWatch", EditorGroup, "Page (left): V Watch", "V Watch in the editor's left panel.", true),
            Bool("PageThreads", EditorGroup, "Page (left): V Threads", "V Threads in the editor's left panel.", true),
            Bool("PagePowerShell", EditorGroup, "Page (left): V PowerShell", "V PowerShell in the editor's left panel.", true),
            Bool("PageLog", EditorGroup, "Page (left): V Log", "V Log, the user log, in the editor's left panel.", true),
            Bool("ExamplesTab", EditorGroup, "Lessons in the Examples tab", "The miniVivisection lessons (the examples folder next to the plug-in) in a 'miniVivisection' folder of the editor's Examples tab (left panel); they open your own copies (in %LOCALAPPDATA%\\miniVivisection\\lessons).", true),
            Bool("FolderDrop", EditorGroup, "Drop a folder: open it in the editor's Explorer", "A folder dropped anywhere on the Script Editor opens in its Explorer (left panel). Files dropped still open as scripts.", true),
            Bool("PageSettings", EditorGroup, "Page (left): V Settings", "These settings in the editor's left panel, applied at once. Shown whatever the V AI and V Debug switches say.", true),

            Bool("AiOn", ClaudeGroup, "AI features on", "The V AI switch in the Script Editor's top bar. Off: V Claude is hidden in every Script Editor (its sessions keep running); on: back as it was.", true, hidden: true),
            Path("ClaudePath", ClaudeGroup, "Claude Code (claude.exe)", "Claude Code's executable. Empty: found automatically (MINIVIVISECTION_CLAUDE, the PATH, the usual install folders).", ""),
            Path("RhinoMcpRouterPath", ClaudeGroup, "RhinoAI's MCP router", "rhino-mcp-router.exe. Empty: found automatically (MINIVIVISECTION_RHINO_MCP, RhinoAI's folders).", ""),
            Bool("OfferRhinoAi", ClaudeGroup, "Offer to install RhinoAI", "When Rhino has started and RhinoAI (Claude working in the Rhino model) isn't installed, offer to install it (Rhino's package Rhino-MCP-Platform). Once per Rhino session, while the V AI switch is on.", true),
            Choice("ClaudeRunCode", ClaudeGroup, "Claude may run Python", "Code Claude wants to run in Rhino (it can change the script, the model, files). Ask: runs after /run. Off: never. On: without asking. Reading a value is always allowed.", "Ask", "Off", "Ask", "On"),
            Bool("ClaudeStartRhinoAi", ClaudeGroup, "Start RhinoAI's server for Claude", "Before each message, start RhinoAI's MCP server in this Rhino when it isn't running (RhinoAI's MCPStart). It listens on localhost without a token: any program on this computer can then use RhinoAI's tools. Off: Claude works in the model only while you've started it yourself.", false),
            Bool("ClaudeDebuggerControl", ClaudeGroup, "Claude may control the debugger", "Breakpoints, continue, step, stop and start debugging. Each step is shown in V Claude.", true),
            Int("ClaudeStepTimeout", ClaudeGroup, "Wait for a step (s)", "How long Claude's continue/step waits for the next pause before it answers \"still running\".", 30),
            Choice("ClaudeEditApproval", ClaudeGroup, "Claude's edits to the script", "Automatic: applied right away (while paused: when the run ends). Ask: shown as a change, applied with /apply.", "Automatic", "Automatic", "Ask"),
            Text("ClaudeModel", ClaudeGroup, "Claude model", "Claude Code's --model (e.g. sonnet, opus). Empty: Claude Code's default.", ""),
            Bool("ClaudeAllowRhinoTools", ClaudeGroup, "Allow RhinoAI's commands and scripts", "Claude may use RhinoAI's destructive tools (run_command, run_python, ...) without asking. The /allow command changes it for a chat.", false),

            Path("PowerShellPath", PowerShellGroup, "PowerShell executable", "pwsh.exe or powershell.exe. Empty: PowerShell 7 if installed, else Windows PowerShell.", ""),
            Bool("PowerShellLoadProfile", PowerShellGroup, "Load the PowerShell profile", "Start PowerShell with the user's profile (slower; a profile that waits for input blocks the tab).", false),

            Choice("GrasshopperMarkdownParser", MarkdownGroup, "Markdown parser", "For Markdown in Grasshopper Panels and the MarkdownText command. Rhino: Markdig, Rhino's own -- as the Script Editor's preview: CommonMark's rules, nested lists and quotes, task lists, footnotes. miniVivisection: the plug-in's own, simpler parser.", "Rhino", "Rhino", "miniVivisection"),
            Choice("MarkdownLinkFollow", MarkdownGroup, "Markdown links: follow with", "How a link in Markdown text is followed -- in Rhino (MarkdownText, V Text baked) and in Grasshopper Panels alike. Click: a click on it (a double-click still edits the text). Ctrl+click: a click with Ctrl held (a plain click selects, as on any object). Off: links aren't followed; the hint over one says how to switch them on.", "Click", "Click", "Ctrl+click", "Off"),
            Path("MarkdownStyleRhino", MarkdownGroup, "Markdown style for Rhino", "The CSS file that says how MarkdownText draws Markdown -- fonts, sizes, spacing, colors, bullets, task boxes, the quote bar, links (styles/markdown_rhino.css next to the plug-in shows all it can say). Empty: miniVivisection's default. A Rhino file keeps a copy of its style; MarkdownText's dialog changes it for all of the file's texts. Edit: a small editor (the default is read-only: Save as makes your own).", "").StyleFileOf(MarkdownStyle.Target.Rhino),
            Path("MarkdownStyleGrasshopper", MarkdownGroup, "Markdown style for Grasshopper", "The CSS file that says how Panels shown as Markdown draw it -- V Text draws Rhino text, with the Rhino style, as MarkdownText (styles/markdown_grasshopper.css next to the plug-in shows all it can say). Empty: miniVivisection's default. A change shows at once. Edit: a small editor (the default is read-only: Save as makes your own).", "").StyleFileOf(MarkdownStyle.Target.Grasshopper),
            Bool("MarkdownLinksColored", MarkdownGroup, "Markdown links in color", "Links in Markdown text (MarkdownText, V Text, Markdown in Panels) in the link color of the Markdown style (a { color: ... }): their words and the line under them (in Rhino solid hatches: the letters filled over the text's, Rhino's text having no colors of its own). Off: in the text's color.", true),
            Int("LinkObjectSize", MarkdownGroup, "Links to Rhino objects: their size in the view (%)", "A Markdown link to a Rhino object (rhino:Name) zooms the view so the object takes about this share of it -- its surroundings in sight -- and marks it with a red circle. 100: it fills the view.", 10),
            Bool("GrasshopperMarkdown", MarkdownGroup, "Markdown in Panels", "A Panel set to Show as Markdown -- or on Autodetect, and plainly Markdown -- shows it rendered (headings, bold, italic, code, lists, quotes, links, tables, rules); a double-click edits its source where you clicked. Off: such Panels show their text as Grasshopper does.", true),

            Choice("UserLogOnCommandLine", LogGroup, "User log on the command line", "Which lines of V Log also go to Rhino's command line.", "Warnings and errors", "Warnings and errors", "All", "Off"),
            Int("UserLogKeep", LogGroup, "User log lines kept", "How many lines V Log keeps.", 1000),
            Choice("LogLevel", LogGroup, "Detailed log", "The detailed log's level (minivivisection.log, for diagnosis). Off: no detailed log.", "Info", Enum.GetNames(typeof(LogLevel))),
            Text("LogAreaLevels", LogGroup, "Detailed log: levels per area", "Levels for single areas, e.g. Pause=Trace; Claude=Debug. Areas: " + string.Join(", ", Enum.GetNames(typeof(LogArea))) + ".", ""),

            // --- tab Rhino --------------------------------------------------------------------------------------------

            Bool("Draw", RhinoWatchGroup, "Draw watches in Rhino", "Draw the V Watch tab's values in the viewports while the debugger is paused.", true).In(RhinoTab),
            Bool("NameDots", RhinoWatchGroup, "Name dots", "A dot with the watch's name (and item count) next to each drawn value.", true).In(RhinoTab),
            Int("NameDotOffset", RhinoWatchGroup, "Name dot offset (pixels)", "How far up and right of its value a name dot sits, with a tail curving down from its bottom edge to the value, so it doesn't cover a point. 0: on the value.", 30).In(RhinoTab),
            Choice("NameDotPointer", RhinoWatchGroup, "Name dot pointer", "From the dot to its value: Tail, a curved shape like a speech bubble's; Line, a thin straight line (the simplest to draw).", "Tail", "Tail", "Line").In(RhinoTab),
            Int("NameDotOpacity", RhinoWatchGroup, "Name dot opacity (%)", "100: solid; lower lets the geometry behind a dot show through.", 50).In(RhinoTab),
            Bool("RhinoWatchesAutoEdit", RhinoWatchGroup, "Auto edit", "At every pause, every shown watch's geometry becomes Rhino objects you can edit; changes go back into the script.", true).In(RhinoTab),

            Bool("SelectFromTabs", SelectionGroup, "Selecting a row highlights it in Rhino", "Rows selected in the V Watch tab or the editor's Variables and Watch tabs highlight what's drawn for them and select their objects being edited.", true).In(RhinoTab),
            Choice("PickDrawn", SelectionGroup, "Select a watch by clicking its drawing", "Plain click: also when Rhino selects an object there. Alt+click: only with Alt held, taken by the plug-in. Off.", "Plain click", "Plain click", "Alt+click", "Off").In(RhinoTab),
            Bool("SelectFromRhino", SelectionGroup, "Selecting edited objects highlights their watch", "Objects being edited (Edit in Rhino, Auto edit) selected in Rhino highlight their watch and select their variable.", true).In(RhinoTab),

            Choice("RunCommand", RhinoRunGroup, "RunPythonScript command", "Normal: runs plainly; the Break now hotkey (Ctrl+Alt+B by default) can't stop it. Stop and record: runs plainly; the hotkey stops it and the Script Editor then opens it at that line (errors too). Debug: Python 3 scripts run in the editor's debugger, the editor out of sight until they pause. Also ScriptEditor Run's Debug option.", "Stop and record", "Normal", "Stop and record", "Debug").In(RhinoTab),
            Bool("FreePause", RhinoRunGroup, "Keep Rhino usable while paused (FreePause)", "Rhino keeps its normal command prompt while the debugger is paused, instead of the Select Action prompt. From the next pause.", true).In(RhinoTab),
            Bool("HelpToolbar", HelpGroup, "Help, examples and settings toolbar", "The miniVivisection toolbar in Rhino: the manual (right-click: as Markdown, in the Script Editor), the examples' overview in Rhino (your copy of 00_overview.3dm) and in Grasshopper (gh0_overview.gh, Grasshopper started for it), the folder of your lesson copies, the settings (this page, in Rhino's Options), MarkdownText and ProtectModel. Its commands work without it: miniVivisectionManual, miniVivisectionExamples, miniVivisectionGrasshopperExamples, miniVivisectionLessons, miniVivisectionSettings.", true).In(RhinoTab),
            Choice("Pump", RhinoRunGroup, "Message loop while paused (diagnosis)", "How Rhino keeps going while a script pauses on its UI thread. Rhino: the default. Eto: for diagnosis only.", "Rhino", "Rhino", "Eto").In(RhinoTab),

            // --- tab Grasshopper --------------------------------------------------------------------------------------

            Choice("GrasshopperScripts", GrasshopperGroup, "Grasshopper script components", "Normal: plain runs; the Break now hotkey (Ctrl+Alt+B by default) leaves them alone. Stop and record: the hotkey stops a hanging component and opens its editor at the line. Always debug: every recompute runs in the debugger (editor open or hidden).", "Stop and record", "Normal", "Stop and record", "Always debug").In(GrasshopperTab),
            Bool("GrasshopperHiddenEditors", GrasshopperGroup, "Hidden editors for Grasshopper components", "With Always debug: every Python 3 script component gets its script editor, out of sight, so the Break now hotkey (Ctrl+Alt+B by default) can pause it (not only stop it) and a breakpoint pauses it even with its editor closed. The editor comes up at the pause, or when you open it. Each one is a Script Editor window, and every run is in the debugger (slower).", true).In(GrasshopperTab),
            Bool("GrasshopperAsync", GrasshopperGroup, "async:true runs off the UI thread", "With '# async:true' a component runs on a thread of its own: Rhino and Grasshopper stay usable and its threads can pause. Its script mustn't use Rhino's UI.", true).In(GrasshopperTab),
            Bool("GrasshopperCloseEditors", GrasshopperGroup, "Close script editors when the definition changes", "Opening another definition, or switching the canvas to another open one, first closes the script editors of the definition it leaves (an editor paused in the debugger stays). Off: they stay open.", true).In(GrasshopperTab),
            Bool("GrasshopperButton", GrasshopperGroup, "Grasshopper toolbar button", "The V\u00B0 button on Grasshopper's toolbar: each click goes to the next mode (Stop and record, Always debug, Normal).", true).In(GrasshopperTab),
            Bool("GrasshopperScriptPanels", GrasshopperGroup, "Scripts in Panels shown as code", "A Panel holding a Python or C# script shows it as code (line numbers, colors, no wrapping). Double-click to edit it in place.", true).In(GrasshopperTab),
            Bool("GrasshopperPausedRecompute", GrasshopperGroup, "While paused: compute changes downstream", "While a script component is paused in its solution, a change elsewhere (a slider, a wire, a Panel) is computed at once with what is downstream of it -- the paused component's outputs as its last finished run left them, script components not run. Everything is computed properly when the pause ends. Off: all of it waits for the pause to end.", true).In(GrasshopperTab),
            Bool("GrasshopperPausedMarks", GrasshopperGroup, "While paused: mark what depends on the paused script", "While a script component is paused, everything downstream of it gets an orange veil and dashed wires on the canvas: its data most probably isn't right any more.", true).In(GrasshopperTab),

            Bool("GhPromptsButton", GhWindowGroup, "Prompts button on Grasshopper's toolbar", "A button on Grasshopper's toolbar (next to the V° button) opens miniVivisection's prompts window: V Claude, V Python and V PowerShell for the definition.", true).In(GrasshopperTab),
            Bool("GhPromptClaude", GhWindowGroup, "Tab: V Claude", "Claude working in the Grasshopper definition: it reads the selection, the data and the scripts, places, wires and sets components (with RhinoAI), writes scripts and notes. Also needs the V AI switch on.", true).In(GrasshopperTab),
            Bool("GhPromptPython", GhWindowGroup, "Tab: V Python", "The Python prompt: in the paused frame of a paused Grasshopper Python component, else in its own namespace.", true).In(GrasshopperTab),
            Bool("GhPromptPowerShell", GhWindowGroup, "Tab: V PowerShell", "The PowerShell prompt.", true).In(GrasshopperTab),
            Bool("GhWindowOwned", GhWindowGroup, "The window stays with Grasshopper", "The prompts window stays above Grasshopper's window and is minimized with it. Off: a window of its own. For windows opened afterwards.", true).In(GrasshopperTab),

            Choice("GhClaudeChanges", GhClaudeGroup, "Claude may change the definition", "Read and change: Claude places, wires, sets and deletes components, writes scripts and notes. Read only: Claude reads and explains; changes it proposes in words.", "Read and change", "Read and change", "Read only").In(GrasshopperTab),
            Bool("GhClaudeUndo", GhClaudeGroup, "One undo step per answer", "Everything one answer changed in the definition is one step in Grasshopper's undo list (Ctrl+Z, or /undo in the chat).", true).In(GrasshopperTab),
            Bool("GhClaudeSelection", GhClaudeGroup, "The selection goes with each message", "Each message names the components selected on the canvas, so 'this' and 'these' mean them.", true).In(GrasshopperTab),
            Bool("GhClaudeRhinoAi", GhClaudeGroup, "Use RhinoAI's Grasshopper tools", "RhinoAI's g1 tools: the canvas graph, the component library, placing, wiring, deleting, solving. Without them Claude can read and set values, but not place components.", true).In(GrasshopperTab),
            Int("GhClaudeItems", GhClaudeGroup, "Items Claude reads per parameter", "How many items of a parameter's data Claude reads at once (gh_get_data), each with its facts (coordinates, length, area, volume, closed).", 50).In(GrasshopperTab),
            Bool("GhClaudeCapture", GhClaudeGroup, "Claude may look at the canvas", "Claude can capture the Grasshopper canvas as an image and look at it (gh_capture_canvas). The Rhino view: RhinoAI's get_viewport_image.", true).In(GrasshopperTab),

            Bool("GhHelpComponents", GhHelpGroup, "Help, examples and settings components", "On Grasshopper's miniVivisection tab, Help: V Examples (opens the Grasshopper examples' overview, your copy of gh0_overview.gh; its menu: the lessons folder), V Manual (the manual; its menu: as Markdown, in the Script Editor) and V Settings (these settings, in Rhino's Options) -- each a button. Shown or hidden from Grasshopper's next start.", true).In(GrasshopperTab),
            Bool("GhPythonHelpers", GhShellsGroup, "V Python: Grasshopper helpers", "ghdoc() the open definition, ghselected() the selected objects, ghcanvas() the canvas, ghfind('name') an object by nickname.", true).In(GrasshopperTab),
            Bool("GhPowerShellFolder", GhShellsGroup, "V PowerShell: start in the definition's folder", "PowerShell starts in the open definition's folder (else your Documents folder).", true).In(GrasshopperTab),
        };

        /// <summary>A setting changed (its key); on the thread that changed it.</summary>
        public static event Action<string> Changed;

        /// <summary>The plug-in as a whole (PluginSwitch). While it's off, the features'
        /// properties below read as off; Get/Set and the Options page see what's stored.</summary>
        public static bool PluginOn { get => GetBool("PluginOn"); set => Set("PluginOn", value); }
        public static bool Draw { get => PluginOn && GetBool("Draw"); set => Set("Draw", value); }
        public static bool NameDots => GetBool("NameDots");
        public static int NameDotOffset => Math.Max(0, Math.Min(200, GetInt("NameDotOffset")));
        public static bool NameDotTail => GetText("NameDotPointer") != "Line";
        public static int NameDotOpacity => Math.Max(10, Math.Min(100, GetInt("NameDotOpacity")));
        public static bool HoverPreview { get => PluginOn && GetBool("HoverPreview"); set => Set("HoverPreview", value); }
        public static bool HoverValues => PluginOn && GetBool("HoverValues");
        public static bool TooltipActions => PluginOn && GetBool("TooltipActions");
        /// <summary>The tooltips' Tree link: always there for now (no setting; it may come back).</summary>
        public static bool TooltipTree => PluginOn;
        public static bool InlineValues => PluginOn && GetBool("InlineValues");
        public static bool ShowChanges => PluginOn && GetBool("ShowChanges");
        public static bool AiOn { get => GetBool("AiOn"); set => Set("AiOn", value); }
        public static bool WatchExpressions { get => PluginOn && GetBool("WatchExpressions"); set => Set("WatchExpressions", value); }
        public static bool AutoEdit { get => PluginOn && GetBool("RhinoWatchesAutoEdit"); set => Set("RhinoWatchesAutoEdit", value); }
        public static bool FreePause { get => PluginOn && GetBool("FreePause"); set => Set("FreePause", value); }
        public static string Pump { get => GetText("Pump"); set => Set("Pump", value); }
        public static TimeSpan PausedThreadTimeout => TimeSpan.FromMilliseconds(Math.Max(200, GetInt("PausedThreadTimeout")));
        public static bool TabRhinoWatch => GetBool("TabRhinoWatch");
        public static bool FolderDrop => PluginOn && GetBool("FolderDrop");
        public static bool TabPython => GetBool("TabPython");
        public static int TerminalFontSize => Math.Max(6, Math.Min(36, GetInt("TerminalFontSize")));
        public static bool ColorsPython => GetBool("ColorsPython");
        public static bool ColorsPowerShell => GetBool("ColorsPowerShell");
        public static bool ColorsClaude => GetBool("ColorsClaude");
        public static bool TabThreads => GetBool("TabThreads");
        public static bool PageThreads => GetBool("PageThreads");
        public static bool ExamplesTab => PluginOn && GetBool("ExamplesTab");
        public static bool ThreadTags => GetBool("ThreadTags");
        public static string BreakHotkey => GetText("BreakHotkey");
        public static string GrasshopperScripts => GetText("GrasshopperScripts");
        public static bool RunCommandRecord => PluginOn && GetText("RunCommand") == "Stop and record";
        public static bool RunCommandDebug => PluginOn && GetText("RunCommand") == "Debug";
        public static bool GrasshopperButton => GetBool("GrasshopperButton");
        public static string NameMark => GetText("NameMark");
        public static bool GrasshopperAlwaysDebug => PluginOn && GrasshopperScripts == "Always debug";
        /// <summary>Ctrl+Alt+B stops a running component that the debugger can't pause (Stop and record; Always debug's fallback).</summary>
        public static bool GrasshopperStops => PluginOn && GrasshopperScripts != "Normal";
        public static bool GrasshopperAsync => PluginOn && GetBool("GrasshopperAsync");
        public static bool GrasshopperPausedRecompute => PluginOn && GetBool("GrasshopperPausedRecompute");
        public static bool GrasshopperPausedMarks => PluginOn && GetBool("GrasshopperPausedMarks");
        public static bool GrasshopperHiddenEditors => GrasshopperAlwaysDebug && GetBool("GrasshopperHiddenEditors");

        /// <summary>Read by miniVivisection.GH's ScriptPanels (by reflection) at every Panel it draws.</summary>
        public static bool GrasshopperScriptPanels => GetBool("GrasshopperScriptPanels");

        /// <summary>Read by miniVivisection.GH's ScriptPanels (by reflection): Markdown in Panels on.</summary>
        public static bool GrasshopperMarkdown => GetBool("GrasshopperMarkdown");

        /// <summary>Read by miniVivisection.GH's ScriptPanels (by reflection): "Rhino" (Markdig) or "miniVivisection".</summary>
        public static string GrasshopperMarkdownParser => GetText("GrasshopperMarkdownParser");

        /// <summary>Markdown links in the style's link color (also read by miniVivisection.GH, by
        /// reflection); off: in the text's color.</summary>
        public static bool MarkdownLinksColored => GetBool("MarkdownLinksColored");

        /// <summary>How Markdown links are followed (also read by miniVivisection.GH, by reflection):
        /// "Click", "Ctrl+click" or "Off".</summary>
        public static string MarkdownLinkFollow => GetText("MarkdownLinkFollow");

        /// <summary>The Markdown style files of the settings (empty: the default).</summary>
        public static string MarkdownStyleRhino => GetText("MarkdownStyleRhino");
        public static string MarkdownStyleGrasshopper => GetText("MarkdownStyleGrasshopper");

        /// <summary>The Grasshopper style's CSS (read by miniVivisection.GH, by reflection).</summary>
        public static string MarkdownStyleGrasshopperCss => MarkdownStyleFiles.SettingCss(MarkdownStyle.Target.Grasshopper);

        /// <summary>The active Rhino file's Markdown style (read by miniVivisection.GH's V Text, by reflection).</summary>
        public static string MarkdownStyleRhinoCss => MarkdownStyleFiles.DocumentCss(RhinoDoc.ActiveDoc);

        /// <summary>The miniVivisection toolbar in Rhino shown (RhinoToolbar).</summary>
        public static bool HelpToolbar => GetBool("HelpToolbar");

        /// <summary>V Examples, V Manual and V Settings on Grasshopper's miniVivisection tab (read by miniVivisection.GH, by reflection).</summary>
        public static bool GhHelpComponents => GetBool("GhHelpComponents");

        /// <summary>The folder of the user's lesson copies, made or brought up to date first (read by miniVivisection.GH, by reflection).</summary>
        public static string LessonsFolder => EditorExamples.WorkingCopies(EditorExamples.ShippedFolder, EditorExamples.WorkingFolder);

        /// <summary>Read by miniVivisection.GH (by reflection): the script editors of a definition left are closed.</summary>
        public static bool GrasshopperCloseEditors => PluginOn && GetBool("GrasshopperCloseEditors");

        /// <summary>A link to a Rhino object: how much of the view the object takes after the zoom (%).</summary>
        public static int LinkObjectSize => Math.Max(1, Math.Min(100, GetInt("LinkObjectSize")));
        public static bool DebugRunButton => PluginOn && GetBool("DebugRunButton");
        public static bool KeepEditorWhileDebugging => PluginOn && GetBool("KeepEditorWhileDebugging");
        public static bool FreezeGuard => PluginOn && GetBool("FreezeGuard");
        public static bool TabPowerShell => GetBool("TabPowerShell");
        public static bool TabClaude => GetBool("TabClaude");
        public static bool PageClaude => GetBool("PageClaude");
        public static bool PageRhinoWatch => GetBool("PageRhinoWatch");
        public static bool PagePowerShell => GetBool("PagePowerShell");
        public static bool PageLog => GetBool("PageLog");
        public static bool PageSettings => GetBool("PageSettings");
        public static string ClaudePath => GetText("ClaudePath");
        public static string[] VisualizerScripts => GetText("VisualizerScripts").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim().Trim('"')).Where(p => p.Length > 0).ToArray();
        public static bool ReloadVisualizersOnSave => GetBool("ReloadVisualizersOnSave");
        public static bool SelectFromTabs => PluginOn && GetBool("SelectFromTabs");
        public static bool BreakInScript => GetBool("BreakInScript");
        public static bool OncePerLine => PluginOn && GetBool("OncePerLine");
        public static bool ConditionalBreakpoints => PluginOn && GetBool("ConditionalBreakpoints");
        public static string PickDrawn => PluginOn ? GetText("PickDrawn") : "Off";
        public static bool SelectFromRhino => PluginOn && GetBool("SelectFromRhino");
        public static string RhinoMcpRouterPath => GetText("RhinoMcpRouterPath");
        public static bool ClaudeAllowRhinoTools => GetBool("ClaudeAllowRhinoTools");
        public static bool ClaudeDebuggerControl => GetBool("ClaudeDebuggerControl");
        public static string ClaudeRunCode => GetText("ClaudeRunCode");
        public static bool ClaudeStartRhinoAi => GetBool("ClaudeStartRhinoAi");
        public static bool OfferRhinoAi => GetBool("OfferRhinoAi");
        public static int ClaudeStepTimeout => GetInt("ClaudeStepTimeout");
        public static string ClaudeEditApproval => GetText("ClaudeEditApproval");
        public static string ClaudeModel => GetText("ClaudeModel");
        public static string PowerShellPath => GetText("PowerShellPath");
        public static bool PowerShellLoadProfile => GetBool("PowerShellLoadProfile");
        public static string UserLogOnCommandLine => GetText("UserLogOnCommandLine") == "Warnings and errors" ? "Warnings" : GetText("UserLogOnCommandLine");
        public static int UserLogKeep => GetInt("UserLogKeep");

        public static bool GhPromptsButton => GetBool("GhPromptsButton");
        public static bool GhPromptClaude => AiOn && GetBool("GhPromptClaude");
        public static bool GhPromptPython => PluginOn && GetBool("GhPromptPython");
        public static bool GhPromptPowerShell => PluginOn && GetBool("GhPromptPowerShell");
        /// <summary>Any tab the window would show.</summary>
        public static bool GhPromptsAny => GhPromptClaude || GhPromptPython || GhPromptPowerShell;
        public static bool GhWindowOwned => GetBool("GhWindowOwned");
        public static bool GhClaudeReadOnly => GetText("GhClaudeChanges") == "Read only";
        public static bool GhClaudeUndo => GetBool("GhClaudeUndo");
        public static bool GhClaudeSelection => GetBool("GhClaudeSelection");
        public static bool GhClaudeRhinoAi => GetBool("GhClaudeRhinoAi");
        public static int GhClaudeItems => Math.Max(1, Math.Min(5000, GetInt("GhClaudeItems")));
        public static bool GhClaudeCapture => GetBool("GhClaudeCapture");
        public static bool GhPythonHelpers => GetBool("GhPythonHelpers");
        public static bool GhPowerShellFolder => GetBool("GhPowerShellFolder");

        /// <summary>The detailed log's levels from the settings.</summary>
        public static void ApplyLogLevels()
        {
            Log.Configure(Enum.TryParse(GetText("LogLevel"), out LogLevel level) ? level : LogLevel.Info, GetText("LogAreaLevels"));
        }

        /// <summary>Settings from older versions of the plug-in.</summary>
        public static void Migrate()
        {
            var store = Store;
            if (store == null || store.GetInteger("SettingsVersion", 0) >= 1)
                return;
            if (store.TryGetBool("LeftPanelPages", out var pages) && !pages)
            {
                Set("PageClaude", false);
                Set("PageRhinoWatch", false);
                Set("PagePowerShell", false);
            }
            store.SetInteger("SettingsVersion", 1);
        }

        /// <summary>Settings renamed since: their old values carried over once.</summary>
        public static void MigrateRenamed()
        {
            var store = Store;
            if (store == null || store.GetInteger("SettingsVersion", 0) >= 3)
                return;
            if (store.GetInteger("SettingsVersion", 0) < 2)
            {
                // "Claude may run Python" was on/off (on by default); now Off/Ask/On, Ask by default.
                if (store.TryGetBool("ClaudeRunPython", out var runPython) && !runPython)
                    Set("ClaudeRunCode", "Off");
            }
            // "Grasshopper script components": Normal stopped a component on Ctrl+Alt+B -- that's
            // Stop and record now; Normal leaves it alone.
            if (store.TryGetString("GrasshopperScripts", out var grasshopper) && grasshopper == "Normal")
                Set("GrasshopperScripts", "Stop and record");
            store.SetInteger("SettingsVersion", 3);
        }

        /// <summary>An environment variable MINIVIVISECTION_<paramref name="name"/> (or the old
        /// SCRIPTEDITORWATCHVIZ_<paramref name="name"/>); null when neither is set.</summary>
        public static string Environment(string name) =>
            System.Environment.GetEnvironmentVariable("MINIVIVISECTION_" + name) ?? System.Environment.GetEnvironmentVariable("SCRIPTEDITORWATCHVIZ_" + name);

        public static Setting Find(string key) => All.First(s => s.Key == key);

        public static object Get(string key)
        {
            var setting = Find(key);
            switch (setting.Kind)
            {
                case Kind.Bool:
                    return GetBool(key);
                case Kind.Int:
                    return GetInt(key);
                default:
                    return GetText(key);
            }
        }

        public static void Set(string key, object value)
        {
            var store = Store;
            if (store == null || Equals(Get(key), value))
                return;
            switch (value)
            {
                case bool flag:
                    store.SetBool(key, flag);
                    break;
                case int number:
                    store.SetInteger(key, number);
                    break;
                default:
                    store.SetString(key, value?.ToString() ?? "");
                    break;
            }
            Log.Info(LogArea.Settings, "{0} = {1}", key, value);
            if (key == "LogLevel" || key == "LogAreaLevels")
                ApplyLogLevels();
            try
            {
                Changed?.Invoke(key);
            }
            catch (Exception ex)
            {
                Log.Error(LogArea.Settings, "reacting to {0}: {1}", key, Log.Describe(ex));
            }
        }

        private static PersistentSettings Store => MiniVivisectionPlugin.Instance?.Settings;

        private static bool GetBool(string key) => Store?.GetBool(key, (bool)Find(key).Default) ?? (bool)Find(key).Default;

        private static int GetInt(string key) => Store?.GetInteger(key, (int)Find(key).Default) ?? (int)Find(key).Default;

        private static string GetText(string key)
        {
            var setting = Find(key);
            var value = Store?.GetString(key, (string)setting.Default) ?? (string)setting.Default;
            return setting.Kind == Kind.Choice && !setting.Choices.Contains(value) ? (string)setting.Default : value;
        }

        private static Setting Bool(string key, string group, string label, string description, bool value, bool newWindows = false, bool hidden = false) =>
            new Setting { Key = key, Group = group, Label = label, Description = description, Kind = Kind.Bool, Default = value, NewWindows = newWindows, Hidden = hidden };

        private static Setting Int(string key, string group, string label, string description, int value) =>
            new Setting { Key = key, Group = group, Label = label, Description = description, Kind = Kind.Int, Default = value };

        private static Setting Text(string key, string group, string label, string description, string value) =>
            new Setting { Key = key, Group = group, Label = label, Description = description, Kind = Kind.Text, Default = value };

        private static Setting Path(string key, string group, string label, string description, string value) =>
            new Setting { Key = key, Group = group, Label = label, Description = description, Kind = Kind.Path, Default = value };

        private static Setting Choice(string key, string group, string label, string description, string value, params string[] choices) =>
            new Setting { Key = key, Group = group, Label = label, Description = description, Kind = Kind.Choice, Default = value, Choices = choices };
    }
}
