using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Eto.Drawing;
using Eto.Forms;

namespace MiniVivisection
{
    /// <summary>
    /// A two-state button on Grasshopper's canvas toolbar (right, next to the preview
    /// buttons) for the option "Grasshopper script components": the Script Editor's Run icon
    /// (Normal: a component runs in the debugger only when started from its editor's Debug)
    /// or its Debug icon (Always debug: every solution of a component whose script editor is
    /// open runs in the debugger, so its breakpoints and Break now pause on any recompute).
    /// Click: the other state. It affects Grasshopper's script components only.
    /// Grasshopper is WinForms, which the plug-in doesn't reference: the toolbar
    /// (GH_DocumentEditor.CanvasToolbar) and the button are reached by reflection. Looked
    /// for every few seconds once Grasshopper is loaded (its window is made late, and can
    /// be made again). Icons: the Script Editor's own (its theme's "editor.runCode" and
    /// "editor.debugCode"), once an editor has loaded its theme; drawn ones until then.
    /// Options: "Grasshopper toolbar button".
    /// </summary>
    internal static class GrasshopperToolbar
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private const string ButtonName = "miniVivisectionDebugButton";
        private const string PromptsName = "miniVivisectionPromptsButton";
        private static dynamic s_prompts; // opens the prompts window (GrasshopperPrompts)
        private static UITimer s_timer;
        private static dynamic s_button;
        private static dynamic s_separator;
        private static object s_toolbar;
        private static bool s_editorIcons;
        private static bool s_reportedFailure;
        private static string s_shows; // the image the button has: "debug editor", "run drawn", ...

        /// <summary>At load: looks for Grasshopper's window from now on.</summary>
        public static void Start()
        {
            // Only once Grasshopper is loaded: until then there's nothing to look for.
            if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Grasshopper"))
            {
                StartLooking();
                return;
            }
            AppDomain.CurrentDomain.AssemblyLoad += (sender, e) =>
            {
                if (e.LoadedAssembly.GetName().Name == "Grasshopper")
                    Rhino.RhinoApp.InvokeOnUiThread(new Action(StartLooking));
            };
        }

        // Grasshopper's window may be made, and made again, any time: looked at every 3 s.
        private static void StartLooking()
        {
            if (s_timer != null)
                return;
            s_timer = new UITimer { Interval = 3 };
            s_timer.Elapsed += (s, e) => Ensure();
            s_timer.Start();
        }

        /// <summary>The setting, the V Debug switch, or the editor's theme: the button follows (UI thread).</summary>
        public static void Apply()
        {
            Ensure();
            if (s_button == null)
                return;
            try
            {
                s_button.Visible = MiniVivisectionSettings.PluginOn && MiniVivisectionSettings.GrasshopperButton;
                if (s_prompts != null)
                    s_prompts.Visible = MiniVivisectionSettings.GhPromptsButton && MiniVivisectionSettings.GhPromptsAny;
                if (s_separator != null)
                    s_separator.Visible = s_button.Visible || (s_prompts != null && (bool)s_prompts.Visible);
                ShowState();
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Tabs, "Grasshopper toolbar button: {0}", Log.Describe(ex));
            }
        }

        private static void Ensure()
        {
            try
            {
                var grasshopper = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Grasshopper");
                var editor = grasshopper?.GetType("Grasshopper.Instances")?.GetProperty("DocumentEditor", Any)?.GetValue(null);
                if (editor == null)
                    return;
                var toolbar = editor.GetType().GetProperty("CanvasToolbar", Any)?.GetValue(editor);
                if (toolbar == null || ReferenceEquals(toolbar, s_toolbar))
                {
                    if (s_button != null && (!s_editorIcons || ThemeName() != s_themeShown))
                        ShowState(); // the editor's icons, once there; its theme, when it changed
                    return;
                }
                Add(toolbar);
            }
            catch (Exception ex)
            {
                if (!s_reportedFailure)
                {
                    s_reportedFailure = true;
                    Log.Warning(LogArea.Tabs, "Grasshopper toolbar button: {0}", Log.Describe(ex));
                }
            }
        }

        private static void Add(object toolbar)
        {
            dynamic strip = toolbar;
            if ((bool)strip.Items.ContainsKey(ButtonName))
            {
                // Ours already (a second copy of the plug-in, or an earlier try): not twice.
                s_toolbar = toolbar;
                return;
            }
            var forms = toolbar.GetType().Assembly.GetName().Name == "System.Windows.Forms"
                ? toolbar.GetType().Assembly
                : AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "System.Windows.Forms");
            dynamic button = Activator.CreateInstance(forms.GetType("System.Windows.Forms.ToolStripButton", true));
            button.Name = ButtonName;
            // Enum values by reflection: dynamic won't convert a boxed enum to the property's type.
            var right = Enum.Parse(forms.GetType("System.Windows.Forms.ToolStripItemAlignment", true), "Right");
            SetProperty(button, "DisplayStyle", Enum.Parse(forms.GetType("System.Windows.Forms.ToolStripItemDisplayStyle", true), "Image"));
            SetProperty(button, "Alignment", right);
            button.Click += new EventHandler((s, e) => Toggle());
            dynamic separator = Activator.CreateInstance(forms.GetType("System.Windows.Forms.ToolStripSeparator", true));
            SetProperty(separator, "Alignment", right);
            // Right-aligned items line up from the right in their order: added last, these
            // come left of the preview buttons.
            // The prompts window's button, left of the mode button
            dynamic prompts = Activator.CreateInstance(forms.GetType("System.Windows.Forms.ToolStripButton", true));
            prompts.Name = PromptsName;
            SetProperty(prompts, "DisplayStyle", Enum.Parse(forms.GetType("System.Windows.Forms.ToolStripItemDisplayStyle", true), "Image"));
            SetProperty(prompts, "Alignment", right);
            prompts.ToolTipText = VNames.Text("miniVivisection prompts: V Claude, V Python and V PowerShell for this definition.\nClaude reads the selection and the data, " +
                                              "and (with RhinoAI) places, wires and sets components. Each answer is one undo step.");
            prompts.Click += new EventHandler((s, e) => GrasshopperPrompts.Show());
            var promptsImage = ToWinForms(DrawnIcons.Draw(DrawPrompts, new Size(24, 24), dimmed: false));
            if (promptsImage != null)
                SetProperty((object)prompts, "Image", promptsImage);
            strip.Items.Add(separator);
            strip.Items.Add(button);
            strip.Items.Add(prompts);
            s_prompts = prompts;
            s_toolbar = toolbar;
            s_button = button;
            s_separator = separator;
            s_editorIcons = false;
            s_shows = null;
            Apply();
            Log.Info(LogArea.Tabs, "Grasshopper toolbar: the Run/Debug button added");
        }

        private static void SetProperty(object target, string name, object value) =>
            target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance).SetValue(target, value);

        // The button goes round the three: Stop and record (the default) -> Always debug -> Normal.
        private static void Toggle()
        {
            var mode = MiniVivisectionSettings.GrasshopperScripts;
            var next = mode == "Stop and record" ? "Always debug" : mode == "Always debug" ? "Normal" : "Stop and record";
            MiniVivisectionSettings.Set("GrasshopperScripts", next); // SettingEffects: Apply
            UserLog.Info("Grasshopper script components: {0}", Explain(next));
        }

        private static string Explain(string mode) =>
            mode == "Always debug" ? "Always debug -- every recompute of a component whose script editor is open (or hidden) runs in the debugger: its breakpoints pause, and Ctrl+Alt+B pauses it." :
            mode == "Normal" ? "Normal -- they run plainly, in the debugger only when started from their editor's Debug; Ctrl+Alt+B leaves them alone." :
            "Stop and record -- they run plainly; Ctrl+Alt+B stops one that runs (hangs), and its editor opens at the line where it was.";

        private static void ShowState()
        {
            var mode = MiniVivisectionSettings.PluginOn ? MiniVivisectionSettings.GrasshopperScripts : "Normal";
            // Toggle goes on from the stored mode, V Debug off or not
            var stored = MiniVivisectionSettings.GrasshopperScripts;
            var next = stored == "Stop and record" ? "Always debug" : stored == "Always debug" ? "Normal" : "Stop and record";
            s_button.ToolTipText = "miniVivisection, Grasshopper script components: " + Explain(mode).Replace(" -- ", "\n") + "\nClick: " + next + "." +
                (MiniVivisectionSettings.PluginOn ? "" : "\n(V Debug is off: Normal until it is on again.)");
            // V°, miniVivisection's mark: gray (Normal); with a small green Run (Stop and record);
            // with the Debug icon (Always debug) -- the Script Editor's own, once it has loaded them.
            var debug = mode == "Always debug";
            var normal = mode == "Normal";
            var badge = normal ? null : EditorIcon(debug ? "editor.debugCode" : "editor.runCode");
            s_editorIcons = normal || badge != null;
            var theme = ThemeName();
            var shows = (debug ? "debug" : normal ? "run gray" : "record") + (s_editorIcons ? " editor" : " drawn") + "|" + theme;
            s_themeShown = theme;
            if (shows == s_shows)
                return; // that image already: not made again every 3 s
            var badgeImage = badge?.Frames.OrderBy(f => Math.Abs(f.PixelSize.Height - 24)).First().Bitmap;
            var icon = DrawnIcons.Draw(g =>
            {
                DrawMark(g);
                if (normal)
                    return;
                if (badgeImage != null)
                    g.DrawImage(badgeImage, 9f, 9f, 11f, 11f);
                else if (debug)
                    DrawDebugBadge(g);
                else
                    DrawRunBadge(g);
            }, new Size(24, 24), dimmed: normal);
            var image = ToWinForms(icon);
            if (image != null)
            {
                var old = s_button.Image as IDisposable;
                SetProperty((object)s_button, "Image", image);
                old?.Dispose();
                s_shows = shows;
            }
        }

        /// <summary>The Script Editor's icon <paramref name="iconId"/> in its theme now; null
        /// before an editor has loaded its theme.</summary>
        private static Type s_registry;   // RhinoCodeEditor.Themes.ThemeRegistry, once found
        private static string s_themeShown;

        // The Script Editor's theme now (its name, or an identity), "" before an editor has one
        private static string ThemeName()
        {
            try
            {
                if (s_registry == null)
                {
                    var editorAssembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "RhinoCodeEditor");
                    s_registry = editorAssembly?.GetType("RhinoCodeEditor.Themes.ThemeRegistry") ?? editorAssembly?.GetTypes().FirstOrDefault(t => t.Name == "ThemeRegistry");
                }
                var theme = s_registry?.GetProperty("ActiveTheme", Any)?.GetValue(null);
                return theme == null ? "" : theme.GetType().GetProperty("Name")?.GetValue(theme) as string ?? theme.GetType().Name + "#" + theme.GetHashCode();
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static Icon EditorIcon(string iconId)
        {
            try
            {
                var editorAssembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "RhinoCodeEditor");
                var iconSet = editorAssembly?.GetType("RhinoCodeEditor.Controls.IconSet");
                if (iconSet == null || ThemeName().Length == 0)
                    return null;
                var sizeType = iconSet.GetConstructors()[0].GetParameters()[1].ParameterType;
                var set = Activator.CreateInstance(iconSet, iconId, Enum.Parse(sizeType, "Default"));
                iconSet.GetMethod("UpdateTheme", Any)?.Invoke(set, null);
                var styleType = iconSet.GetMethod("GetIcon", Any).GetParameters()[0].ParameterType;
                return iconSet.GetMethod("GetIcon", Any).Invoke(set, new[] { Enum.ToObject(styleType, 0) }) as Icon;
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Tabs, "Grasshopper toolbar, the editor's icon {0}: {1}", iconId, Log.Describe(ex));
                return null;
            }
        }

        /// <summary>An Eto icon as a WinForms image (System.Drawing), through PNG.</summary>
        private static object ToWinForms(Icon icon)
        {
            try
            {
                var frame = icon.Frames.OrderBy(f => Math.Abs(f.PixelSize.Height - 24)).First();
                var stream = new MemoryStream();
                frame.Bitmap.Save(stream, ImageFormat.Png);
                stream.Position = 0;
                var image = Type.GetType("System.Drawing.Image, System.Drawing.Common", true);
                return image.GetMethod("FromStream", new[] { typeof(Stream) }).Invoke(null, new object[] { stream });
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Tabs, "Grasshopper toolbar icon: {0}", Log.Describe(ex));
                return null;
            }
        }

        // The button's icon (20 x 20 grid): V°, and a badge in the lower right.
        private static readonly Color MarkColor = Color.FromArgb(52, 58, 70);
        private static readonly Color Green = Color.FromArgb(46, 160, 67);

        private static void DrawMark(Graphics g)
        {
            using (var font = new Font("Segoe UI", 11.5f, FontStyle.Bold))
                g.DrawText(font, MarkColor, -0.5f, -3.5f, "V" + (char)0x00B0);
        }

        // The prompts button: a speech bubble with a prompt in it.
        private static void DrawPrompts(Graphics g)
        {
            using (var bubble = GraphicsPath.GetRoundRect(new RectangleF(1.5f, 2.5f, 19f, 13.5f), 3f))
                g.FillPath(MarkColor, bubble);
            g.FillPolygon(MarkColor, new PointF(4.5f, 15f), new PointF(4.5f, 20.5f), new PointF(10f, 15f));
            using (var pen = new Pen(Colors.White, 1.6f))
            {
                g.DrawLine(pen, 5.5f, 6f, 8.5f, 9.2f);
                g.DrawLine(pen, 8.5f, 9.2f, 5.5f, 12.4f);
                g.DrawLine(pen, 10.5f, 12.4f, 16f, 12.4f);
            }
        }

        // Drawn stand-ins for the editor's icons, before an editor has loaded them: Run, a green
        // triangle; Debug, the same with a red dot.
        private static void DrawRunBadge(Graphics g) =>
            g.FillPolygon(Green, new PointF(11, 10), new PointF(19.5f, 15), new PointF(11, 20));

        private static void DrawDebugBadge(Graphics g)
        {
            DrawRunBadge(g);
            g.FillEllipse(Color.FromArgb(207, 34, 46), 15.5f, 15.5f, 4.5f, 4.5f);
        }
    }
}
