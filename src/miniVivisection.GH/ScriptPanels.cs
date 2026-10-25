using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using Grasshopper;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using HarmonyLib;
using MiniVivisection;

namespace MiniVivisection.GH
{
    /// <summary>
    /// A Panel holding a Python or C# script, shown as code: a monospace font (Grasshopper's
    /// console font), line numbers, syntax colors, no line wrapping, and a scroll bar along the
    /// bottom for long lines next to the Panel's own one on the right. Double-clicking it edits
    /// it in place, looking the same (CodeBox); its menu also opens it in the Script Editor (the
    /// .rhp's GrasshopperInputScripts.OpenPanel: a working file, saved back into the Panel).
    /// Shift+double-click keeps Grasshopper's own text box (then without wrapping, in the
    /// console font, Tab as four spaces), Ctrl+double-click its dialog. After Ctrl+Alt+B stopped
    /// the Panel's script, the .rhp opens it in place at that line (EditAtLine).
    ///
    /// A script Panel: its own text (no sources, Multiline Data on), connected to a script
    /// component's "script" input (its language), or marked or recognized as Python or C#
    /// (ScriptTokens.Detect) -- or set so in its menu's "Show as" (Python, C#, or Plain text
    /// for never; saved with the Panel: GH_Panel.Write/Read postfixes). Setting "Grasshopper:
    /// scripts in Panels shown as code".
    ///
    /// Patches on GH_PanelAttributes, not generic: RenderItems (the text) and RenderScrollBar
    /// draw script Panels themselves -- the right scroll bar's grip set where Grasshopper's own
    /// drag code finds it (m_uiScrollGripBounds / m_uiScrollGripControl), so it scrolls as
    /// before --; RespondToMouseDown/Move/Up drag the bottom bar; RespondToMouseDoubleClick
    /// edits in place; ShowCanvasTextBox's text box gets no wrapping. GH_Panel's AppendMenuItems
    /// adds "Edit the script here" and "Edit the script in the Script Editor".
    /// </summary>
    internal static class ScriptPanels
    {
        private const int BarWidth = 8; // GH_PanelAttributes.ScrollBarWidth
        private static readonly Guid PlugInId = new Guid("c198ec17-3078-46a0-8f9b-94a40e9ba298"); // miniVivisection.rhp

        private sealed class State
        {
            public string Text;
            public ScriptTokens.Language RecipientsLanguage; // from a script input it feeds
            public ShowAs Mode;      // the Panel's "Show as" (saved with it)
            public ShowAs ModeUsed;  // the one the rest was made for
            public MarkdownView Markdown; // "Show as" Markdown: its layout, as drawn last
            public ScriptTokens.Language Language;
            public string[] Lines;
            public List<ScriptTokens.Token>[] Tokens;
            public int Columns;

            // Laid out at the last drawing (canvas coordinates), for the scroll bars and the mouse
            public float ContentHeight, ViewHeight, ContentWidth, ViewWidth, CodeLeft, BarTop;
            public double Horizontal; // 0 to 1: the bottom bar
            public RectangleF Track; // the bottom bar's track
            public float GripWidth;
            public Rectangle TrackControl, GripControl; // control coordinates; empty: no bar
            public bool Dragging;
            public float DragX;
            public double DragRatio;

            // Where Ctrl+Alt+B stopped the Panel's script (1-based; 0: none), with what the
            // Script Editor's overlay would say -- shown until the Panel's text changes
            public int StopLine;
            public string StopTitle;
            public string[] StopLines;
            public string StopText;

            // A Panel with sources: its data's text, the same string while it's the same -- made
            // again only when the data changed (the Panel expired, or its tree's count differs)
            public string SourcedText;
            public object SourcedData;   // the VolatileData the text was made from
            public int SourcedCount = -1;
            public bool SourcedDirty = true, SourcedFollowed;

            // A link pressed in a Markdown Panel (followed on release, unless a double-click follows)
            public string LinkDown;
            public PointF LinkDownAt;
            public bool LinkDownCtrl; // with Ctrl (setting: Ctrl+click): followed at once
        }

        /// <summary>The text a Panel shows: its own -- or, with sources, its data's, an item a line.</summary>
        internal static string TextOf(GH_Panel panel)
        {
            if (panel.SourceCount == 0)
                return panel.UserText ?? "";
            var state = s_states.GetValue(panel, _ => new State());
            if (!state.SourcedFollowed)
            {
                // The data changes with a solution: the Panel expires before it
                state.SourcedFollowed = true;
                panel.SolutionExpired += (s, e) => { if (s_states.TryGetValue(panel, out var st)) st.SourcedDirty = true; };
            }
            var data = panel.VolatileData;
            var count = data?.DataCount ?? 0;
            if (state.SourcedText == null || state.SourcedDirty || !ReferenceEquals(state.SourcedData, data) || count != state.SourcedCount)
            {
                var text = string.Join("\n", data.AllData(true).Select(g => g is Grasshopper.Kernel.Types.GH_String s ? s.Value ?? "" : g?.ToString() ?? ""));
                if (!string.Equals(state.SourcedText, text, StringComparison.Ordinal))
                    state.SourcedText = text;
                state.SourcedData = data;
                state.SourcedCount = count;
                state.SourcedDirty = false;
            }
            return state.SourcedText;
        }

        /// <summary>The stop shown on the Panel (line, title, explanation), or nothing.</summary>
        internal static (int Line, string Title, string[] Lines) StopOf(GH_Panel panel) =>
            s_states.TryGetValue(panel, out var state) && state.StopLine > 0 && ReferenceEquals(state.StopText, panel.UserText)
                ? (state.StopLine, state.StopTitle, state.StopLines) : (0, null, null);

        private static readonly ConditionalWeakTable<GH_Panel, State> s_states = new ConditionalWeakTable<GH_Panel, State>();
        private static bool s_reported;

        // Harmony's types in a class of their own: should 0Harmony.dll be missing (the .gha
        // copied without the .rhp's folder), ScriptPanels itself still loads -- only Install
        // fails, and says so.
        private static class Hooks
        {
            public static Harmony Harmony;
            public static AccessTools.FieldRef<GH_PanelAttributes, Rectangle> GripBounds;
            public static AccessTools.FieldRef<GH_PanelAttributes, Rectangle> GripControl;
        }

        /// <summary>When the .gha loads (MiniVivisectionLoad): the patches, once.</summary>
        public static void Install()
        {
            if (Hooks.Harmony != null)
                return;
            try
            {
                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                var attributes = typeof(GH_PanelAttributes);
                MethodInfo Method(Type type, string name) => type.GetMethod(name, any) ?? throw new MissingMethodException(type.FullName, name);
                Hooks.GripBounds = AccessTools.FieldRefAccess<GH_PanelAttributes, Rectangle>("m_uiScrollGripBounds");
                Hooks.GripControl = AccessTools.FieldRefAccess<GH_PanelAttributes, Rectangle>("m_uiScrollGripControl");
                var patches = new (MethodInfo Original, string Prefix, string Postfix)[]
                {
                    (Method(attributes, "RenderItems"), nameof(RenderItemsPrefix), null),
                    (Method(attributes, "RenderScrollBar"), nameof(RenderScrollBarPrefix), null),
                    (Method(attributes, "RespondToMouseDown"), nameof(MouseDownPrefix), null),
                    (Method(attributes, "RespondToMouseMove"), nameof(MouseMovePrefix), null),
                    (Method(attributes, "RespondToMouseUp"), nameof(MouseUpPrefix), null),
                    (Method(attributes, "RespondToMouseDoubleClick"), nameof(DoubleClickPrefix), null),
                    (Method(attributes, "ShowCanvasTextBox"), null, nameof(TextBoxPostfix)),
                    (Method(typeof(GH_Panel), "AppendMenuItems"), null, nameof(MenuPostfix)),
                    // A Panel's "Show as" in the .gh file
                    (Method(typeof(GH_Panel), "Write"), null, nameof(WritePostfix)),
                    (Method(typeof(GH_Panel), "Read"), null, nameof(ReadPostfix)),
                    // Grasshopper's window previews every key (KeyPreview): arrows pan the canvas,
                    // letters open the search -- except for a focused TextBox. The code box gets them.
                    (Method(typeof(GH_DocumentEditor), "EditorKeyDown"), nameof(EditorKeyDownPrefix), null),
                };
                var harmony = new Harmony("minivivisection.gh.scriptpanels");
                try
                {
                    foreach (var (original, prefix, postfix) in patches)
                    {
                        harmony.Patch(original,
                            prefix: prefix == null ? null : new HarmonyMethod(typeof(ScriptPanels), prefix),
                            postfix: postfix == null ? null : new HarmonyMethod(typeof(ScriptPanels), postfix));
                    }
                }
                catch
                {
                    harmony.UnpatchAll(harmony.Id); // all or none
                    throw;
                }
                Hooks.Harmony = harmony;
            }
            catch (Exception ex)
            {
                Rhino.RhinoApp.WriteLine("miniVivisection: scripts in Panels can't be shown as code: {0}", ex.Message);
            }
        }

        /// <summary>For the live test: whether the patches are in.</summary>
        internal static bool Installed => Hooks.Harmony != null;

        // --- which Panels -----------------------------------------------------------------------

        private static Func<bool> s_setting;
        private static long s_settingLookedFor;

        // The .rhp's setting, live (MiniVivisectionSettings.GrasshopperScriptPanels, by reflection); its
        // saved value until the .rhp is found; on without either.
        private static bool Enabled
        {
            get
            {
                if (s_setting == null && Environment.TickCount64 - s_settingLookedFor > 2000)
                {
                    s_settingLookedFor = Environment.TickCount64;
                    try
                    {
                        var getter = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "miniVivisection")
                            ?.GetType("MiniVivisection.MiniVivisectionSettings")
                            ?.GetProperty("GrasshopperScriptPanels", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetGetMethod(true);
                        if (getter != null)
                            s_setting = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), getter);
                    }
                    catch (Exception)
                    {
                        // the saved value below
                    }
                }
                try
                {
                    return s_setting?.Invoke() ?? Rhino.PlugIns.PlugIn.GetPluginSettings(PlugInId, false)?.GetBool("GrasshopperScriptPanels", true) ?? true;
                }
                catch (Exception)
                {
                    return true;
                }
            }
        }

        private static readonly Dictionary<string, Delegate> s_getters = new Dictionary<string, Delegate>();
        private static bool s_reportedDefaults;
        private static readonly Dictionary<string, long> s_gettersMissing = new Dictionary<string, long>();

        // A setting of the .rhp, live (MiniVivisectionSettings.<property>, by reflection); the fallback
        // until the .rhp is found (looked for again every 2 s).
        internal static T Live<T>(string property, T fallback)
        {
            try
            {
                if (!s_getters.TryGetValue(property, out var getter))
                {
                    if (s_gettersMissing.TryGetValue(property, out var since) && Environment.TickCount64 - since < 2000)
                        return fallback;
                    var method = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "miniVivisection")
                        ?.GetType("MiniVivisection.MiniVivisectionSettings")
                        ?.GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetGetMethod(true);
                    if (method == null)
                    {
                        if (s_gettersMissing.Count == 0 && !s_reportedDefaults)
                        {
                            // The .rhp isn't loaded (yet): the settings' defaults then, said once
                            s_reportedDefaults = true;
                            Rhino.RhinoApp.WriteLine("miniVivisection.GH: the miniVivisection plug-in isn't loaded -- Panels as code and Markdown use the defaults (V Settings are in the plug-in).");
                        }
                        s_gettersMissing[property] = Environment.TickCount64;
                        return fallback;
                    }
                    s_getters[property] = getter = Delegate.CreateDelegate(typeof(Func<T>), method);
                }
                return ((Func<T>)getter)();
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        /// <summary>Setting Grasshopper > "Markdown in Panels": Show as Markdown and Autodetect's
        /// Markdown. Off: such a Panel is shown as Grasshopper shows it.</summary>
        private static bool MarkdownOn => Live("GrasshopperMarkdown", true);

        /// <summary>Setting Grasshopper > "Markdown parser": Rhino (Markdig, as the Script Editor's
        /// preview) or miniVivisection (its own).</summary>
        internal static bool UseMarkdig => Live("GrasshopperMarkdownParser", "Rhino") != "miniVivisection";

        /// <summary>The settings a Panel's look depends on, in a word (V Text follows them).</summary>
        internal static string Look => Enabled + "|" + MarkdownOn + "|" + UseMarkdig + "|" + LinksColored + "|" + Style.Css.GetHashCode() + "|" + RhinoStyle.Css.GetHashCode();

        /// <summary>Setting Markdown > "Markdown style for Grasshopper": how Panels draw Markdown
        /// (the .rhp reads the file; without it, the default).</summary>
        internal static MarkdownStyle Style => MarkdownStyle.Parse(Live<string>("MarkdownStyleGrasshopperCss", null) ?? MarkdownStyle.DefaultCss(MarkdownStyle.Target.Grasshopper));

        /// <summary>The active Rhino file's Markdown style (else "Markdown style for Rhino"): V Text
        /// draws Rhino text, as MarkdownText does.</summary>
        internal static MarkdownStyle RhinoStyle => MarkdownStyle.Parse(Live<string>("MarkdownStyleRhinoCss", null) ?? MarkdownStyle.DefaultCss(MarkdownStyle.Target.Rhino));

        /// <summary>Setting Markdown > "Markdown links in color": the links in the style's link color.</summary>
        internal static bool LinksColored => Live("MarkdownLinksColored", true);

        /// <summary>The links' color in Panels (Color.Empty: "Markdown links in color" off, the text's color).</summary>
        internal static Color LinkColor => LinksColored ? Style.LinkColor : Color.Empty;

        /// <summary>Setting Markdown > "Markdown links: follow with": Click (the default), Ctrl+click or Off.</summary>
        internal static string LinkFollow => Live("MarkdownLinkFollow", "Click");

        /// <summary>What <paramref name="text"/> shows as in <paramref name="panel"/> (without one: in
        /// a Panel on Autodetect): Python or C# code, Markdown, or plain text (None) -- the Panel's
        /// Show as, else the language of a script input it feeds, else detected (ScriptTokens.Detect);
        /// plain text when the settings are off. V Text's Auto.</summary>
        internal static ScriptTokens.Language LanguageShown(GH_Panel panel, string text)
        {
            if (!Enabled)
                return ScriptTokens.Language.None;
            ScriptTokens.Language language;
            switch (panel == null ? ShowAs.Auto : ShowAsOf(panel))
            {
                case ShowAs.Python:
                    return ScriptTokens.Language.Python;
                case ShowAs.CSharp:
                    return ScriptTokens.Language.CSharp;
                case ShowAs.Text:
                    return ScriptTokens.Language.None;
                case ShowAs.Markdown:
                    language = ScriptTokens.Language.Markdown;
                    break;
                default:
                    language = panel == null ? ScriptTokens.Language.None : LanguageOfRecipients(panel);
                    if (language == ScriptTokens.Language.None)
                        language = ScriptTokens.Detect(text ?? "");
                    break;
            }
            return language == ScriptTokens.Language.Markdown && !MarkdownOn ? ScriptTokens.Language.None : language;
        }

        /// <summary>The Panel's script and its layout, or null when it isn't a script Panel (or
        /// the setting is off).</summary>
        internal static object ScriptOf(GH_Panel panel) => Script(panel);

        /// <summary>For a test: a Markdown Panel's layout as drawn last (MarkdownView), or null.</summary>
        internal static object MarkdownViewOf(GH_Panel panel) => MarkdownOf(panel)?.Markdown;

        /// <summary>How a Panel's text is shown: as detected (Auto), always as Python or C# code, or
        /// always as plain text -- its menu's "Show as", saved with the definition.</summary>
        internal enum ShowAs
        {
            Auto,
            Python,
            CSharp,
            Text,
            Markdown,
        }

        internal static ShowAs ShowAsOf(GH_Panel panel) => s_states.TryGetValue(panel, out var state) ? state.Mode : ShowAs.Auto;

        private const string ShowAsKey = "MiniVivisectionShowAs"; // in the Panel's chunk of the .gh file

        private static State Script(GH_Panel panel)
        {
            if (panel == null || !Enabled)
                return null;
            var mode = ShowAsOf(panel);
            if (mode == ShowAs.Text || mode == ShowAs.Markdown || (mode == ShowAs.Auto && panel.SourceCount == 0 && !panel.Properties.Multiline))
                return null;
            var text = TextOf(panel);
            var state = s_states.GetValue(panel, _ => new State());
            var fromRecipients = LanguageOfRecipients(panel);
            if (!ReferenceEquals(state.Text, text) || fromRecipients != state.RecipientsLanguage || mode != state.ModeUsed)
            {
                state.Text = text;
                state.RecipientsLanguage = fromRecipients;
                state.ModeUsed = mode;
                state.Language = mode == ShowAs.Python ? ScriptTokens.Language.Python
                    : mode == ShowAs.CSharp ? ScriptTokens.Language.CSharp
                    : fromRecipients != ScriptTokens.Language.None ? fromRecipients : ScriptTokens.Detect(text, data: panel.SourceCount > 0);
                var language = state.Language;
                state.Lines = ScriptTokens.Lines(text).Select(ScriptTokens.ExpandTabs).ToArray();
                state.Tokens = IsCode(language) ? ScriptTokens.Tokenize(state.Lines, language) : null;
                state.Columns = state.Lines.Length == 0 ? 0 : state.Lines.Max(l => l.Length);
            }
            return IsCode(state.Language) ? state : null;
        }

        private static bool IsCode(ScriptTokens.Language language) =>
            language == ScriptTokens.Language.Python || language == ScriptTokens.Language.CSharp;

        /// <summary>A Panel set to "Show as" Plain text (its own text): drawn by miniVivisection too,
        /// from the top left as code is -- in Grasshopper's font, wrapped as its Wrap Items says --,
        /// not centered as Grasshopper draws a Panel's own text. Null for any other Panel.</summary>
        private static State Plain(GH_Panel panel) =>
            panel != null && Enabled && panel.SourceCount == 0 && ShowAsOf(panel) == ShowAs.Text ? s_states.GetValue(panel, _ => new State()) : null;

        /// <summary>A Panel set to "Show as" Markdown, or on Autodetect and plainly Markdown (its
        /// own text, ScriptTokens.Detect): drawn rendered (MarkdownView); a double-click edits its
        /// source where it was clicked. Null for any other Panel.</summary>
        private static State MarkdownOf(GH_Panel panel)
        {
            if (panel == null || !Enabled || !MarkdownOn)
                return null;
            var mode = ShowAsOf(panel);
            if (mode == ShowAs.Markdown)
                return s_states.GetValue(panel, _ => new State());
            if (mode != ShowAs.Auto || (panel.SourceCount == 0 && !panel.Properties.Multiline) || Script(panel) != null)
                return null;
            // Script() brought the detection up to date
            return s_states.TryGetValue(panel, out var state) && state.Language == ScriptTokens.Language.Markdown ? state : null;
        }

        // A Panel miniVivisection draws: as code, as Markdown, or as plain text.
        private static State Shown(GH_Panel panel) => Script(panel) ?? MarkdownOf(panel) ?? Plain(panel);

        // A script component's "script" input fed by the Panel: that component's language.
        private static ScriptTokens.Language LanguageOfRecipients(GH_Panel panel)
        {
            foreach (var recipient in panel.Recipients)
            {
                if (recipient.Name != "script")
                    continue;
                // "Input Is Path": the Panel holds a file's name, not the script
                if (recipient.GetType().GetProperty("TreatInputAsPath")?.GetValue(recipient) is bool path && path)
                    continue;
                var owner = recipient.Attributes?.GetTopLevel?.DocObject?.GetType().Name ?? "";
                if (owner.IndexOf("Python", StringComparison.OrdinalIgnoreCase) >= 0)
                    return ScriptTokens.Language.Python;
                if (owner.IndexOf("CSharp", StringComparison.OrdinalIgnoreCase) >= 0)
                    return ScriptTokens.Language.CSharp;
            }
            return ScriptTokens.Language.None;
        }

        // --- drawing ----------------------------------------------------------------------------

        internal sealed class Palette
        {
            public Color Keyword, Builtin, String, Comment, Number, Definition, Decorator;
        }

        // Visual Studio Code's Light+ and Dark+ colors: for a light Panel (Grasshopper's yellow,
        // white) or a dark one.
        internal static readonly Palette Light = new Palette
        {
            Keyword = Color.FromArgb(0, 0, 255),
            Builtin = Color.FromArgb(38, 127, 153),
            String = Color.FromArgb(163, 21, 21),
            Comment = Color.FromArgb(0, 128, 0),
            Number = Color.FromArgb(9, 134, 88),
            Definition = Color.FromArgb(121, 94, 38),
            Decorator = Color.FromArgb(175, 0, 219),
        };

        internal static readonly Palette Dark = new Palette
        {
            Keyword = Color.FromArgb(86, 156, 214),
            Builtin = Color.FromArgb(78, 201, 176),
            String = Color.FromArgb(206, 145, 120),
            Comment = Color.FromArgb(106, 153, 85),
            Number = Color.FromArgb(181, 206, 168),
            Definition = Color.FromArgb(220, 220, 170),
            Decorator = Color.FromArgb(197, 134, 192),
        };

        internal static Color ColorOf(ScriptTokens.Kind kind, Palette palette, Color plain)
        {
            switch (kind)
            {
                case ScriptTokens.Kind.Keyword: return palette.Keyword;
                case ScriptTokens.Kind.Builtin: return palette.Builtin;
                case ScriptTokens.Kind.String: return palette.String;
                case ScriptTokens.Kind.Comment: return palette.Comment;
                case ScriptTokens.Kind.Number: return palette.Number;
                case ScriptTokens.Kind.Definition: return palette.Definition;
                case ScriptTokens.Kind.Decorator: return palette.Decorator;
                default: return plain;
            }
        }

        private static readonly Dictionary<float, Font> s_fonts = new Dictionary<float, Font>();
        /// <summary>Where Ctrl+Alt+B stopped: a band over the line, an arrow in the gutter.</summary>
        internal static readonly Color StopBand = Color.FromArgb(110, 255, 128, 0);
        internal static readonly Color StopArrow = Color.FromArgb(230, 230, 100, 0);

        internal static readonly StringFormat Typographic = MakeFormat(StringAlignment.Near);
        internal static readonly StringFormat TypographicFar = MakeFormat(StringAlignment.Far);

        private static StringFormat MakeFormat(StringAlignment alignment)
        {
            var format = (StringFormat)StringFormat.GenericTypographic.Clone();
            format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip | StringFormatFlags.MeasureTrailingSpaces;
            format.Alignment = alignment;
            return format;
        }

        // Grasshopper's console font, as large as the Panel's own.
        internal static Font CodeFont(GH_Panel panel)
        {
            var size = Math.Max(4f, panel.Properties.Font?.SizeInPoints ?? 8f);
            lock (s_fonts)
            {
                if (!s_fonts.TryGetValue(size, out var font))
                    s_fonts[size] = font = GH_FontServer.NewFont(GH_FontServer.FamilyConsole, size, FontStyle.Regular);
                return font;
            }
        }

        internal static Color Fore(GH_PanelAttributes attributes) =>
            attributes.Selected ? attributes.Owner.Properties.InferredForeColourSelected : attributes.Owner.Properties.InferredForeColour;

        internal static Color Back(GH_PanelAttributes attributes) =>
            attributes.Selected ? attributes.Owner.Properties.InferredBackColourSelected : attributes.Owner.Properties.InferredBackColour;

        internal static bool IsDark(Color color) => (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0 < 0.45;

        // GH_PanelAttributes.RenderItems: a script Panel's text drawn as code.
        private static bool RenderItemsPrefix(GH_PanelAttributes __instance, GH_Canvas canvas, Graphics graphics)
        {
            try
            {
                var state = Script(__instance.Owner);
                var markdown = state == null ? MarkdownOf(__instance.Owner) : null;
                var plain = state == null && markdown == null ? Plain(__instance.Owner) : null;
                if (state == null && markdown == null && plain == null)
                    return true;
                if (canvas.Viewport.Zoom < 0.5f)
                    return false; // as Grasshopper: no text this far out
                canvas.SetSmartTextRenderingHint();
                if (state != null)
                    Paint(__instance, graphics, state);
                else if (markdown != null)
                    PaintMarkdown(__instance, graphics, markdown);
                else
                    PaintPlain(__instance, graphics, plain);
                return false;
            }
            catch (Exception ex)
            {
                Report(ex);
                return true;
            }
        }

        private static void Paint(GH_PanelAttributes attributes, Graphics graphics, State state)
        {
            var panel = attributes.Owner;
            var font = CodeFont(panel);
            var fore = Fore(attributes);
            var palette = IsDark(Back(attributes)) ? Dark : Light;
            var lineHeight = font.GetHeight(graphics);
            var charWidth = graphics.MeasureString(new string('0', 40), font, PointF.Empty, Typographic).Width / 40f;
            var content = attributes.ContentBounds;
            var scroller = attributes.ScrollerBounds;
            var digits = Math.Max(2, state.Lines.Length.ToString().Length);
            var gutter = (digits + 1.5f) * charWidth;
            var codeLeft = content.Left + gutter + charWidth * 0.6f;
            var codeRight = scroller.Left - 2f;
            var top = content.Top + 2f;
            var viewWidth = Math.Max(1f, codeRight - codeLeft);
            var contentWidth = (state.Columns + 1) * charWidth;
            var wide = contentWidth > viewWidth;
            var bottom = content.Bottom - 2f - (wide ? BarWidth + 2 : 0);
            var viewHeight = Math.Max(1f, bottom - top);
            var contentHeight = state.Lines.Length * lineHeight;
            state.ContentHeight = contentHeight;
            state.ViewHeight = viewHeight;
            state.ContentWidth = contentWidth;
            state.ViewWidth = viewWidth;
            state.CodeLeft = codeLeft;
            state.BarTop = content.Bottom - BarWidth - 2f;
            if (!wide)
                state.Horizontal = 0;
            var offsetY = (float)(panel.ScrollRatio * Math.Max(0, contentHeight - viewHeight));
            var offsetX = (float)(state.Horizontal * Math.Max(0, contentWidth - viewWidth));
            var first = Math.Max(0, (int)(offsetY / lineHeight));
            var last = Math.Min(state.Lines.Length - 1, (int)((offsetY + viewHeight) / lineHeight) + 1);

            // The gutter: a faint band, the line numbers right-aligned, a line along its edge
            using (var band = new SolidBrush(Color.FromArgb(IsDark(Back(attributes)) ? 28 : 20, fore)))
                graphics.FillRectangle(band, content.Left + 1, content.Top, gutter, content.Height - 1);
            using (var edge = new Pen(Color.FromArgb(45, fore)))
                graphics.DrawLine(edge, content.Left + 1 + gutter, content.Top, content.Left + 1 + gutter, content.Bottom - 1);
            graphics.SetClip(new RectangleF(content.Left, top, gutter, viewHeight));
            using (var numbers = new SolidBrush(Color.FromArgb(120, fore)))
            {
                for (var i = first; i <= last; i++)
                    graphics.DrawString((i + 1).ToString(), font, numbers, content.Left + gutter - charWidth * 0.5f, top + i * lineHeight - offsetY, TypographicFar);
            }

            // The code: each line in pieces, plain between the colored ones
            graphics.SetClip(new RectangleF(codeLeft - 1, top, viewWidth + 1, viewHeight));
            var stop = StopOf(panel);
            if (stop.Line > 0 && stop.Line <= state.Lines.Length)
            {
                // Orange, to stand out on Grasshopper's yellow Panels; an arrow at the line number
                var y = top + (stop.Line - 1) * lineHeight - offsetY;
                using (var band = new SolidBrush(StopBand))
                    graphics.FillRectangle(band, codeLeft - 1, y, viewWidth + 1, lineHeight);
                graphics.SetClip(new RectangleF(content.Left, top, gutter, viewHeight));
                using (var arrow = new SolidBrush(StopArrow))
                    graphics.FillPolygon(arrow, new[] { new PointF(content.Left + 2, y + 2), new PointF(content.Left + 2 + lineHeight * 0.45f, y + lineHeight / 2), new PointF(content.Left + 2, y + lineHeight - 2) });
                graphics.SetClip(new RectangleF(codeLeft - 1, top, viewWidth + 1, viewHeight));
            }
            var brushes = new Dictionary<Color, SolidBrush>();
            SolidBrush BrushOf(Color color)
            {
                if (!brushes.TryGetValue(color, out var brush))
                    brushes[color] = brush = new SolidBrush(color);
                return brush;
            }
            try
            {
                var firstColumn = Math.Max(0, (int)(offsetX / charWidth) - 1);
                var lastColumn = (int)((offsetX + viewWidth) / charWidth) + 1;
                for (var i = first; i <= last; i++)
                {
                    var line = state.Lines[i];
                    var y = top + i * lineHeight - offsetY;
                    var column = 0;
                    foreach (var token in state.Tokens[i])
                    {
                        if (token.Start > column)
                            Piece(line, column, token.Start, fore);
                        Piece(line, token.Start, token.Start + token.Length, ColorOf(token.Kind, palette, fore));
                        column = token.Start + token.Length;
                    }
                    if (column < line.Length)
                        Piece(line, column, line.Length, fore);

                    // [start, end) of the line, where it shows, without its leading spaces
                    void Piece(string text, int start, int end, Color color)
                    {
                        start = Math.Max(start, firstColumn);
                        end = Math.Min(end, Math.Min(text.Length, lastColumn));
                        while (start < end && text[start] == ' ')
                            start++;
                        if (start >= end)
                            return;
                        graphics.DrawString(text.Substring(start, end - start), font, BrushOf(color), codeLeft + start * charWidth - offsetX, y, Typographic);
                    }
                }
            }
            finally
            {
                foreach (var brush in brushes.Values)
                    brush.Dispose();
                graphics.ResetClip();
            }
            // Where it stopped, in a word, along the bottom: double-click for the rest
            if (stop.Line > 0)
            {
                var banner = new RectangleF(content.Left + 2, bottom - lineHeight - 2, content.Width - 4 - BarWidth, lineHeight + 2);
                graphics.SetClip(banner);
                using (var fill = new SolidBrush(Color.FromArgb(235, 255, 214, 92)))
                    graphics.FillRectangle(fill, banner);
                using (var edge = new Pen(Color.FromArgb(200, 191, 128, 0)))
                    graphics.DrawRectangle(edge, banner.X, banner.Y, banner.Width - 1, banner.Height - 1);
                using (var text = new SolidBrush(Color.FromArgb(255, 60, 40, 0)))
                    graphics.DrawString((char)0x25A0 + " " + stop.Title + " -- double-click for more", font, text, banner.X + charWidth * 0.5f, banner.Y + 1, Typographic);
                graphics.ResetClip();
            }
        }

        private static readonly StringFormat PlainWrapped = MakePlainFormat(wrap: true);
        private static readonly StringFormat PlainUnwrapped = MakePlainFormat(wrap: false);

        private static StringFormat MakePlainFormat(bool wrap)
        {
            var format = new StringFormat(StringFormat.GenericDefault) { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near, Trimming = StringTrimming.None };
            format.FormatFlags |= StringFormatFlags.NoClip;
            if (!wrap)
                format.FormatFlags |= StringFormatFlags.NoWrap;
            return format;
        }

        // A Markdown Panel: rendered from the top left in the Panel's width, its layout kept for a
        // click (laid out again when the text, the width or the font change).
        private static void PaintMarkdown(GH_PanelAttributes attributes, Graphics graphics, State state)
        {
            var panel = attributes.Owner;
            var text = TextOf(panel);
            var font = panel.Properties.Font ?? GH_FontServer.StandardAdjusted;
            var content = attributes.ContentBounds;
            var scroller = attributes.ScrollerBounds;
            var left = content.Left + 4f;
            var top = content.Top + 3f;
            var viewWidth = Math.Max(10f, scroller.Left - 3f - left);
            var bottom = content.Bottom - 2f;
            var viewHeight = Math.Max(1f, bottom - top);
            var layout = state.Markdown;
            var markdig = UseMarkdig;
            var style = Style;
            // The fields ({{protected}}, {{modified}}): the definition's V°Protect's state
            var fields = MarkdownFields.Has(text) ? ProtectComponent.FieldState(panel.OnPingDocument()) : null;
            if (layout == null || !ReferenceEquals(layout.Text, text) || Math.Abs(layout.Width - viewWidth) > 0.5f || Math.Abs(layout.FontSize - font.SizeInPoints) > 0.01f || layout.Markdig != markdig ||
                layout.FontName != font.Name || layout.FontStyle != font.Style || Math.Abs(layout.DpiX - graphics.DpiX) > 0.01f || !ReferenceEquals(layout.Style, style) ||
                layout.Fields != fields?.Signature)
                state.Markdown = layout = MarkdownView.Layout(graphics, text, viewWidth, font, markdig, style, fields);
            state.ContentHeight = layout.Height + 4f;
            state.ViewHeight = viewHeight;
            state.ContentWidth = viewWidth;
            state.ViewWidth = viewWidth;
            state.CodeLeft = left;
            state.BarTop = content.Bottom - BarWidth - 2f;
            state.Horizontal = 0;
            var offsetY = (float)(panel.ScrollRatio * Math.Max(0, state.ContentHeight - viewHeight));
            var clip = new RectangleF(left - 2, top, viewWidth + 4, viewHeight);
            graphics.SetClip(clip);
            try
            {
                var back = Back(attributes);
                layout.Draw(graphics, new PointF(left, top), offsetY, clip, Fore(attributes), back, IsDark(back) ? Dark : Light);
            }
            finally
            {
                graphics.ResetClip();
            }
        }

        // A plain text Panel: its text from the top left, in its own font; wrapped as its Wrap Items
        // says, else unwrapped with the bottom scroll bar; the right one for its height.
        private static void PaintPlain(GH_PanelAttributes attributes, Graphics graphics, State state)
        {
            var panel = attributes.Owner;
            var text = TextOf(panel);
            var font = panel.Properties.Font ?? GH_FontServer.StandardAdjusted;
            var wrap = panel.Properties.Wrap;
            var format = wrap ? PlainWrapped : PlainUnwrapped;
            var content = attributes.ContentBounds;
            var scroller = attributes.ScrollerBounds;
            var left = content.Left + 3f;
            var top = content.Top + 2f;
            var viewWidth = Math.Max(1f, scroller.Left - 2f - left);
            var size = graphics.MeasureString(text, font, wrap ? new SizeF(viewWidth, 100000f) : new SizeF(100000f, 100000f), format);
            var contentWidth = wrap ? viewWidth : size.Width + 4f;
            var wide = contentWidth > viewWidth;
            var bottom = content.Bottom - 2f - (wide ? BarWidth + 2 : 0);
            var viewHeight = Math.Max(1f, bottom - top);
            state.ContentHeight = size.Height + 2f;
            state.ViewHeight = viewHeight;
            state.ContentWidth = contentWidth;
            state.ViewWidth = viewWidth;
            state.CodeLeft = left;
            state.BarTop = content.Bottom - BarWidth - 2f;
            if (!wide)
                state.Horizontal = 0;
            var offsetY = (float)(panel.ScrollRatio * Math.Max(0, state.ContentHeight - viewHeight));
            var offsetX = (float)(state.Horizontal * Math.Max(0, contentWidth - viewWidth));
            graphics.SetClip(new RectangleF(left - 1, top, viewWidth + 1, viewHeight));
            try
            {
                using (var brush = new SolidBrush(Fore(attributes)))
                    graphics.DrawString(text, font, brush, new RectangleF(left - offsetX, top - offsetY, wrap ? viewWidth : contentWidth + 8f, size.Height + 8f), format);
            }
            finally
            {
                graphics.ResetClip();
            }
        }

        // GH_PanelAttributes.RenderScrollBar: the right bar for the code's height (where
        // Grasshopper's drag finds its grip), and the bottom one for its width.
        private static bool RenderScrollBarPrefix(GH_PanelAttributes __instance, GH_Canvas canvas, Graphics graphics)
        {
            try
            {
                var state = Shown(__instance.Owner);
                if (state == null)
                    return true;
                ref var grip = ref Hooks.GripBounds(__instance);
                ref var gripControl = ref Hooks.GripControl(__instance);
                grip = Rectangle.Empty;
                gripControl = Rectangle.Empty;
                state.TrackControl = Rectangle.Empty;
                state.GripControl = Rectangle.Empty;
                if (canvas.Viewport.Zoom < 0.5f || state.ViewHeight <= 0)
                    return false;
                using (var brush = new SolidBrush(Color.FromArgb(100, Fore(__instance))))
                {
                    var scroller = __instance.ScrollerBounds;
                    if (state.ContentHeight > state.ViewHeight && scroller.Height > 2 * scroller.Width)
                    {
                        // As GH_StringList.RenderScrollBar: the grip as long as the share shown
                        var length = Math.Max(2 * scroller.Width, (int)(scroller.Height * (double)state.ViewHeight / state.ContentHeight));
                        var at = (int)((scroller.Height - length) * __instance.Owner.ScrollRatio);
                        grip = new Rectangle(scroller.Left, scroller.Top + at, scroller.Width, length);
                        using (var shape = Capsule(grip, vertical: true))
                            graphics.FillPath(brush, shape);
                        gripControl = GH_Convert.ToRectangle(canvas.Viewport.ProjectRectangle(grip));
                    }
                    if (state.ContentWidth > state.ViewWidth)
                    {
                        var track = new RectangleF(state.CodeLeft, state.BarTop, state.ViewWidth, BarWidth);
                        var length = Math.Max(2f * BarWidth, track.Width * state.ViewWidth / state.ContentWidth);
                        var at = (float)((track.Width - length) * state.Horizontal);
                        var bar = new RectangleF(track.Left + at, track.Top, length, BarWidth);
                        using (var shape = Capsule(Rectangle.Round(bar), vertical: false))
                            graphics.FillPath(brush, shape);
                        state.Track = track;
                        state.GripWidth = length;
                        state.TrackControl = GH_Convert.ToRectangle(canvas.Viewport.ProjectRectangle(track));
                        state.GripControl = GH_Convert.ToRectangle(canvas.Viewport.ProjectRectangle(bar));
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                Report(ex);
                return true;
            }
        }

        // A grip: a bar with round ends.
        private static GraphicsPath Capsule(Rectangle rectangle, bool vertical)
        {
            var path = new GraphicsPath();
            var d = vertical ? rectangle.Width : rectangle.Height;
            if (vertical)
            {
                path.AddArc(rectangle.Left, rectangle.Top + 1, d, d, 180f, 180f);
                path.AddArc(rectangle.Left, rectangle.Bottom - (d + 1), d, d, 0f, 180f);
            }
            else
            {
                path.AddArc(rectangle.Left + 1, rectangle.Top, d, d, 90f, 180f);
                path.AddArc(rectangle.Right - (d + 1), rectangle.Top, d, d, 270f, 180f);
            }
            path.CloseAllFigures();
            return path;
        }

        // --- the bottom bar, by the mouse -------------------------------------------------------

        private static bool MouseDownPrefix(GH_PanelAttributes __instance, GH_Canvas sender, GH_CanvasMouseEvent e, ref GH_ObjectResponse __result)
        {
            try
            {
                // A link in a Markdown Panel: followed when let go there (MouseUpPrefix) -- clicked
                // as the setting says (Click: plainly, Ctrl+click: Ctrl held, Off: not), where the
                // text is drawn (not below half zoom: the layout is of the last drawing then)
                var follow = LinkFollow;
                var keys = follow == "Click" ? Keys.None : follow == "Ctrl+click" ? Keys.Control : (Keys?)null;
                if (e.Button == MouseButtons.Left && keys != null && Control.ModifierKeys == keys && sender.Viewport.Zoom >= 0.5f && MarkdownOf(__instance.Owner) is State markdown &&
                    markdown.Markdown?.LinkAt(e.CanvasLocation) is string url)
                {
                    markdown.LinkDown = url;
                    markdown.LinkDownCtrl = keys == Keys.Control;
                    markdown.LinkDownAt = e.CanvasLocation;
                    HideLinkTip(sender);
                    __result = GH_ObjectResponse.Capture;
                    return false;
                }
                if (e.Button != MouseButtons.Left || !s_states.TryGetValue(__instance.Owner, out var state) || Shown(__instance.Owner) == null ||
                    state.TrackControl.IsEmpty || !state.TrackControl.Contains(e.ControlLocation))
                    return true;
                if (!state.GripControl.Contains(e.ControlLocation))
                {
                    // On the track: the grip jumps there (centered), and drags from there
                    var free = state.Track.Width - state.GripWidth;
                    if (free > 0)
                        state.Horizontal = Math.Clamp((e.CanvasX - state.Track.Left - state.GripWidth / 2) / free, 0, 1);
                    sender.Refresh();
                }
                state.Dragging = true;
                state.DragX = e.CanvasX;
                state.DragRatio = state.Horizontal;
                __result = GH_ObjectResponse.Capture;
                return false;
            }
            catch (Exception ex)
            {
                Report(ex);
                return true;
            }
        }

        private static bool MouseMovePrefix(GH_PanelAttributes __instance, GH_Canvas sender, GH_CanvasMouseEvent e, ref GH_ObjectResponse __result)
        {
            try
            {
                // Over a link in a Markdown Panel: the hand (links followed), and the hint
                if (e.Button == MouseButtons.None)
                {
                    var url = sender.Viewport.Zoom >= 0.5f ? MarkdownOf(__instance.Owner)?.Markdown?.LinkAt(e.CanvasLocation) : null;
                    if (url != null)
                    {
                        sender.Cursor = LinkFollow == "Off" ? Cursors.Default : Cursors.Hand;
                        s_hand = true;
                        ShowLinkTip(sender, e.ControlLocation, url, __instance.Owner);
                        __result = GH_ObjectResponse.Handled;
                        return false;
                    }
                    if (s_hand)
                    {
                        sender.Cursor = Cursors.Default;
                        s_hand = false;
                        HideLinkTip(sender);
                    }
                }
                if (!s_states.TryGetValue(__instance.Owner, out var state))
                    return true;
                if (state.Dragging && e.Button == MouseButtons.Left)
                {
                    var free = state.Track.Width - state.GripWidth;
                    if (free > 0)
                        state.Horizontal = Math.Clamp(state.DragRatio + (e.CanvasX - state.DragX) / free, 0, 1);
                    sender.Refresh();
                    __result = GH_ObjectResponse.Handled;
                    return false;
                }
                if (e.Button == MouseButtons.None && !state.TrackControl.IsEmpty && state.TrackControl.Contains(e.ControlLocation) && Shown(__instance.Owner) != null)
                {
                    sender.Cursor = Cursors.Default;
                    __result = GH_ObjectResponse.Handled;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Report(ex);
                return true;
            }
        }

        private static bool MouseUpPrefix(GH_PanelAttributes __instance, GH_CanvasMouseEvent e, ref GH_ObjectResponse __result)
        {
            if (!s_states.TryGetValue(__instance.Owner, out var state))
                return true;
            if (state.LinkDown != null)
            {
                // Let go on the link pressed: followed -- a plain click once a double-click can't
                // come any more, a Ctrl+click at once
                var url = state.LinkDown;
                state.LinkDown = null;
                var moved = Math.Abs(e.CanvasLocation.X - state.LinkDownAt.X) + Math.Abs(e.CanvasLocation.Y - state.LinkDownAt.Y);
                if (moved < 6 && state.Markdown?.LinkAt(e.CanvasLocation) == url)
                {
                    if (state.LinkDownCtrl)
                        Follow(url, FolderOf(__instance.Owner), __instance.Owner);
                    else
                        FollowSoon(url, FolderOf(__instance.Owner), __instance.Owner);
                }
                __result = GH_ObjectResponse.Release;
                return false;
            }
            if (!state.Dragging)
                return true;
            state.Dragging = false;
            __result = GH_ObjectResponse.Release;
            return false;
        }

        private static bool s_hand;
        private static ToolTip s_tip;
        private static string s_tipUrl;
        private static GH_Panel s_tipPanel;
        private static readonly HashSet<GH_Canvas> s_tipCanvases = new HashSet<GH_Canvas>();

        /// <summary>For a test: the link the hint is over now (null: none).</summary>
        internal static string LinkTip => s_tipUrl;

        // Over a link: a hint by the mouse -- where it goes, and its target. Grasshopper tells a
        // Panel of the mouse only while it is over it: the canvas itself says when it left.
        private static void ShowLinkTip(GH_Canvas canvas, Point at, string url, GH_Panel panel)
        {
            if (s_tipCanvases.Add(canvas))
            {
                canvas.MouseMove += (s, e) => HideLinkTipAway((GH_Canvas)s, e.Location);
                canvas.MouseLeave += (s, e) => HideLinkTipAway((GH_Canvas)s, null);
            }
            if (s_tipUrl == url)
                return;
            s_tipUrl = url;
            s_tipPanel = panel;
            if (s_tip == null)
                s_tip = new ToolTip { ShowAlways = true };
            var task = MarkdownView.TaskOf(url);
            s_tip.Show(task != null
                ? (task.Value.Done ? "Task: uncheck it" : "Task: check it") + MarkdownLinks.HowToFollow(LinkFollow, true)
                : MarkdownLinks.Describe(url) + MarkdownLinks.HowToFollow(LinkFollow, true) + Environment.NewLine + url, canvas, at.X + 14, at.Y + 22);
        }

        // The mouse not over the hint's link any more (elsewhere on the canvas, or off it): the hint goes
        private static void HideLinkTipAway(GH_Canvas canvas, Point? control)
        {
            try
            {
                if (s_tipUrl == null && !s_hand)
                    return;
                if (control is Point p && canvas.Viewport.Zoom >= 0.5f && s_tipPanel != null &&
                    MarkdownOf(s_tipPanel)?.Markdown?.LinkAt(canvas.Viewport.UnprojectPoint(p)) == s_tipUrl)
                    return;
                HideLinkTip(canvas);
                if (s_hand)
                {
                    canvas.Cursor = Cursors.Default;
                    s_hand = false;
                }
            }
            catch (Exception)
            {
                // the hint stays
            }
        }

        private static void HideLinkTip(GH_Canvas canvas)
        {
            if (s_tipUrl == null)
                return;
            s_tipUrl = null;
            s_tipPanel = null;
            s_tip?.Hide(canvas);
        }

        private static System.Windows.Forms.Timer s_linkTimer;
        private static string s_linkPending;
        private static string s_linkFolder;
        private static GH_Panel s_linkPanel;

        // The folder of the definition a Panel is in: a link's relative file is from there
        private static string FolderOf(GH_Panel panel)
        {
            var path = panel.OnPingDocument()?.FilePath;
            return string.IsNullOrEmpty(path) ? null : System.IO.Path.GetDirectoryName(path);
        }

        // A link clicked: followed after the double-click time (a double-click edits instead)
        private static void FollowSoon(string url, string folder, GH_Panel panel)
        {
            s_linkPending = url;
            s_linkFolder = folder;
            s_linkPanel = panel;
            if (s_linkTimer == null)
            {
                s_linkTimer = new System.Windows.Forms.Timer();
                s_linkTimer.Tick += (s, e) =>
                {
                    s_linkTimer.Stop();
                    var pending = s_linkPending;
                    s_linkPending = null;
                    if (pending != null)
                        Follow(pending, s_linkFolder, s_linkPanel);
                };
            }
            s_linkTimer.Stop();
            s_linkTimer.Interval = Math.Max(100, SystemInformation.DoubleClickTime);
            s_linkTimer.Start();
        }

        /// <summary>A link followed (MarkdownLinks; a relative file from <paramref name="folder"/>);
        /// what went wrong, also on Rhino's command line. A task box of <paramref name="panel"/>:
        /// checked or unchecked.</summary>
        internal static string Follow(string url, string folder, GH_Panel panel = null)
        {
            var error = MarkdownView.TaskOf(url) is var (line, column, _) ? ToggleTask(panel, line, column)
                : url.Trim().StartsWith("protect:", StringComparison.OrdinalIgnoreCase) ? ProtectComponent.FollowLink(panel?.OnPingDocument() ?? Instances.ActiveCanvas?.Document, url.Trim().Substring("protect:".Length))
                : MarkdownLinks.Follow(url, folder);
            if (error != null)
                Rhino.RhinoApp.WriteLine("miniVivisection: " + error);
            return error;
        }

        /// <summary>The task at (<paramref name="line"/>, <paramref name="column"/>) of
        /// <paramref name="panel"/>'s Markdown checked or unchecked ("- [ ]" and "- [x]"), undoable.
        /// What went wrong, or null.</summary>
        internal static string ToggleTask(GH_Panel panel, int line, int column)
        {
            if (panel == null)
                return "The task's Panel is gone.";
            if (panel.SourceCount > 0)
                return "The Panel shows its input's data: check the task where that text comes from.";
            var toggled = MarkdownBlocks.ToggleTask(panel.UserText, line, column);
            if (toggled == null)
                return "No task box at line " + (line + 1) + " of the Panel's text.";
            panel.RecordUndoEvent("Check task");
            panel.TriggerAutoSave();
            panel.SetUserText(toggled);
            Instances.ActiveCanvas?.Refresh();
            return null;
        }

        // --- editing ----------------------------------------------------------------------------

        // A plain double-click on a script Panel: the Script Editor. Shift: Grasshopper's text
        // box; Ctrl: its dialog (Grasshopper's own code, which follows).
        private static bool DoubleClickPrefix(GH_PanelAttributes __instance, GH_CanvasMouseEvent e, ref GH_ObjectResponse __result)
        {
            try
            {
                // A double-click: a link clicked just before isn't followed -- nor the one under
                // its second press (its MouseUp comes after this)
                s_linkPending = null;
                s_linkTimer?.Stop();
                if (MarkdownOf(__instance.Owner) is State pressed)
                    pressed.LinkDown = null;
                if (e.Button != MouseButtons.Left || Control.ModifierKeys != Keys.None || __instance.Owner.SourceCount > 0)
                    return true; // a Panel with sources shows its data: not edited here
                var markdown = MarkdownOf(__instance.Owner);
                if (markdown?.Markdown != null)
                {
                    // Its source, the caret where the click was in the rendered text
                    var (line, column) = markdown.Markdown.SourceAt(e.CanvasLocation);
                    if (!CodeBox.Open(__instance.Owner, ScriptTokens.Language.Markdown, line + 1, false, column))
                        return true;
                    __result = GH_ObjectResponse.Handled;
                    return false;
                }
                var state = Script(__instance.Owner);
                if (state == null)
                    return true;
                if (!state.TrackControl.IsEmpty && state.TrackControl.Contains(e.ControlLocation))
                {
                    __result = GH_ObjectResponse.Handled; // a quick double-click on the bottom bar
                    return false;
                }
                if (!CodeBox.Open(__instance.Owner, state.Language))
                    return true; // too small on screen: Grasshopper's own (its dialog)
                __result = GH_ObjectResponse.Handled;
                return false;
            }
            catch (Exception ex)
            {
                Report(ex);
                return true;
            }
        }

        /// <summary>For the .rhp, by reflection, after Ctrl+Alt+B stopped a component running this
        /// Panel's script: the Panel edited in place, at <paramref name="line"/> (marked). False
        /// when it isn't a script Panel shown as code, or isn't on screen: the Script Editor then.</summary>
        internal static bool EditAtLine(object panel, int line, string title, string[] lines)
        {
            if (!(panel is GH_Panel ghPanel) || ghPanel.SourceCount > 0)
                return false;
            var state = Script(ghPanel);
            if (state == null)
                return false;
            // The Panel keeps the mark and a line about it until its text changes
            state.StopLine = line;
            state.StopTitle = title;
            state.StopLines = lines ?? new string[0];
            state.StopText = ghPanel.UserText;
            Instances.InvalidateCanvas();
            return CodeBox.Open(ghPanel, state.Language, line, mark: true);
        }

        /// <summary>The Panel's script in the Script Editor: the .rhp's
        /// GrasshopperInputScripts.OpenPanel. False without the .rhp.</summary>
        internal static bool OpenInScriptEditor(GH_Panel panel, ScriptTokens.Language language)
        {
            var open = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "miniVivisection")
                ?.GetType("MiniVivisection.GrasshopperInputScripts")
                ?.GetMethod("OpenPanel", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (open == null)
                return false;
            open.Invoke(null, new object[] { panel, language == ScriptTokens.Language.CSharp ? ".cs" : language == ScriptTokens.Language.Markdown ? ".md" : ".py" });
            return true;
        }

        // Grasshopper's text box (Shift+double-click) on a script Panel: no wrapping, the
        // console font, left-aligned, Tab as four spaces.
        private static void TextBoxPostfix(GH_PanelAttributes __instance, GH_Canvas canvas, bool __result)
        {
            try
            {
                if (!__result)
                    return;
                if (Script(__instance.Owner) == null)
                {
                    // Plain text: left, as it's shown
                    if ((Plain(__instance.Owner) != null || MarkdownOf(__instance.Owner) != null) && canvas.Controls.OfType<TextBox>().LastOrDefault() is TextBox plainBox)
                        plainBox.TextAlign = HorizontalAlignment.Left;
                    return;
                }
                var box = canvas.Controls.OfType<TextBox>().LastOrDefault();
                if (box == null)
                    return;
                box.WordWrap = false;
                box.ScrollBars = ScrollBars.Both;
                box.TextAlign = HorizontalAlignment.Left;
                box.Font = GH_FontServer.NewFont(GH_FontServer.FamilyConsole, box.Font.SizeInPoints, FontStyle.Regular);
                box.AcceptsTab = true;
                box.KeyDown += (sender, e) =>
                {
                    if (e.KeyCode == Keys.Tab && e.Modifiers == Keys.None)
                    {
                        box.SelectedText = new string(' ', ScriptTokens.TabSize);
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                    }
                };
            }
            catch (Exception ex)
            {
                Report(ex);
            }
        }

        // The Panel's menu: "Edit the script in the Script Editor", after Grasshopper's "Edit Notes."
        private static void WritePostfix(GH_Panel __instance, GH_IO.Serialization.GH_IWriter writer)
        {
            var mode = ShowAsOf(__instance);
            if (mode != ShowAs.Auto)
                writer.SetString(ShowAsKey, mode.ToString());
        }

        private static void ReadPostfix(GH_Panel __instance, GH_IO.Serialization.GH_IReader reader)
        {
            string saved = null;
            if (reader.TryGetString(ShowAsKey, ref saved) && Enum.TryParse(saved, out ShowAs mode))
                s_states.GetValue(__instance, _ => new State()).Mode = mode;
            else if (s_states.TryGetValue(__instance, out var state))
                state.Mode = ShowAs.Auto; // undone, or read again
        }

        private static void SetShowAs(GH_Panel panel, ShowAs mode) => ChangeShowAs(panel, mode, recordUndo: true);

        /// <summary>The Panel's "Show as"; V°Claude's tools without an undo step (its answer is one).</summary>
        internal static void ChangeShowAs(GH_Panel panel, ShowAs mode, bool recordUndo)
        {
            if (ShowAsOf(panel) == mode)
                return;
            if (recordUndo)
                panel.RecordUndoEvent("Show as " + (mode == ShowAs.CSharp ? "C#" : mode.ToString()));
            s_states.GetValue(panel, _ => new State()).Mode = mode;
            panel.Attributes?.ExpireLayout();
            Instances.InvalidateCanvas();
        }

        private static void MenuPostfix(GH_Panel __instance, ToolStripDropDown menu)
        {
            try
            {
                if (!Enabled || Hooks.Harmony == null)
                    return;
                var panel = __instance;
                var notes = menu.Items.Cast<ToolStripItem>().ToList().FindIndex(i => i.Text != null && i.Text.StartsWith("Edit Notes", StringComparison.Ordinal));
                var at = notes >= 0 ? notes + 1 : menu.Items.Count;
                // "Show as": as detected, or always code (Python, C#), or always text
                var current = ShowAsOf(panel);
                var showAs = new ToolStripMenuItem(GhNames.Menu("Show as"))
                {
                    ToolTipText = "How this Panel shows its text. Saved with the definition.",
                };
                var markdownOn = MarkdownOn;
                foreach (var (mode, text) in new[] { (ShowAs.Auto, markdownOn ? "Autodetect (code or Markdown)" : "Autodetect (code)"), (ShowAs.Python, "Python code"), (ShowAs.CSharp, "C# code"), (ShowAs.Markdown, "Markdown (rendered)"), (ShowAs.Text, "Plain text") })
                {
                    var item = new ToolStripMenuItem(text, null, (s, e) => SetShowAs(panel, mode)) { Checked = current == mode };
                    if (mode == ShowAs.Markdown && !markdownOn)
                    {
                        item.Enabled = false;
                        item.ToolTipText = GhNames.Text("Markdown in Panels is off (V Settings > Markdown > Markdown in Panels).");
                    }
                    showAs.DropDownItems.Add(item);
                }
                menu.Items.Insert(at, showAs);
                if (__instance.SourceCount > 0)
                    return; // it shows its data: edited where the data comes from
                if (MarkdownOf(__instance) != null)
                {
                    var hasRhp = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "miniVivisection");
                    menu.Items.Insert(at + 1, new ToolStripMenuItem(GhNames.Menu("Edit the Markdown here"), null, (s, e) => CodeBox.Open(panel, ScriptTokens.Language.Markdown))
                    {
                        ToolTipText = "Its Markdown source, in the Panel itself (double-click it, too: the caret goes where you clicked). Click elsewhere to keep it, Esc to drop the changes.",
                    });
                    if (hasRhp)
                        menu.Items.Insert(at + 2, new ToolStripMenuItem(GhNames.Menu("Edit the Markdown in the Script Editor"), null, (s, e) => OpenInScriptEditor(panel, ScriptTokens.Language.Markdown))
                        {
                            ToolTipText = "Its Markdown source in the Script Editor, which shows it rendered too (Rhino's preview: raw HTML in it is rendered); saving writes it back into the Panel.",
                        });
                    return;
                }
                var state = Script(__instance);
                if (state == null)
                    return;
                var language = state.Language;
                var name = language == ScriptTokens.Language.CSharp ? "C#" : "Python";
                var here = new ToolStripMenuItem(GhNames.Menu("Edit the script here"), null, (s, e) => CodeBox.Open(panel, language))
                {
                    ToolTipText = name + ", in the Panel itself (double-click it, too): line numbers, colors, undo, Tab, Ctrl+/. Click elsewhere to keep it, Esc to drop the changes.",
                };
                menu.Items.Insert(at, here);
                // The Script Editor is the .rhp's; editing here is the .gha's own
                if (AppDomain.CurrentDomain.GetAssemblies().All(a => a.GetName().Name != "miniVivisection"))
                    return;
                var editor = new ToolStripMenuItem(GhNames.Menu("Edit the script in the Script Editor"), null, (s, e) => OpenInScriptEditor(panel, language))
                {
                    ToolTipText = name + ", in the Script Editor (completion and the rest); saving writes it back into the Panel.",
                };
                menu.Items.Insert(at + 1, editor);
            }
            catch (Exception ex)
            {
                Report(ex);
            }
        }

        private static bool EditorKeyDownPrefix() => !CodeBox.IsFocused;

        private static void Report(Exception ex)
        {
            if (s_reported)
                return;
            s_reported = true;
            Rhino.RhinoApp.WriteLine("miniVivisection: a script Panel: {0} -- shown as Grasshopper shows it.", ex.Message);
        }
    }

    /// <summary>Grasshopper loading the .gha: the script Panels' patches, and the double-click on a
    /// component with a script input.</summary>
    public sealed class MiniVivisectionLoad : GH_AssemblyPriority
    {
        public override GH_LoadingInstruction PriorityLoad()
        {
            // The .rhp of another build (a .ghlink left by a test Rhino, a Debug .rhp with a
            // Release .gha): the two share code by source, so they must be of one build
            try
            {
                var rhp = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "miniVivisection")
                    ?.GetType("MiniVivisection.VersionInfo")?.GetProperty("Build", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as int?;
                if (rhp is int build && build != MiniVivisection.VersionInfo.Build)
                    Rhino.RhinoApp.WriteLine("miniVivisection.GH: this .gha is build {0}, the loaded .rhp build {1} -- they belong together; use the same folder for both (the .ghlink in Grasshopper's Libraries names the .gha's).",
                        MiniVivisection.VersionInfo.Build, build);
            }
            catch (Exception)
            {
                // not known: no word
            }
            // The tab: "miniVivisection", "V°" when the tabs don't fit -- Grasshopper shortens tabs
            // from the right until they do, then shows single letters (V). Before the ribbon is made;
            // the first name given for a category counts, so a new mark shows from Grasshopper's next start.
            try
            {
                Grasshopper.Instances.ComponentServer.AddCategoryShortName(GhNames.Category, GhNames.TabShort);
                Grasshopper.Instances.ComponentServer.AddCategorySymbolName(GhNames.Category, 'V');
            }
            catch (Exception ex)
            {
                Rhino.RhinoApp.WriteLine("miniVivisection.GH: the tab's short name: {0}", ex.Message);
            }
            // Each on its own: one that can't be set up (a type gone after a Rhino update, a
            // missing 0Harmony.dll) leaves the components and the others
            foreach (var (what, install) in new (string, Action)[] { ("Panels as code and Markdown", ScriptPanels.Install), ("script inputs", ScriptInputComponents.Install), ("links", GhLinks.Install),
                ("closing script editors when the definition changes", GhEditorsOnSwitch.Install) })
            {
                try
                {
                    install();
                }
                catch (Exception ex)
                {
                    Rhino.RhinoApp.WriteLine("miniVivisection.GH: {0} couldn't be set up: {1}", what, ex.Message);
                }
            }
            return GH_LoadingInstruction.Proceed;
        }
    }
}
