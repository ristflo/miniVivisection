using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Eto.Drawing;
using Eto.Forms;
using Rhino.Runtime.Code.Storage;

namespace MiniVivisection
{
    /// <summary>
    /// "V Help" in the Script Editor's top bar, next to the V AI and V Debug switches and
    /// drawn like them (a gray caption, then a pill -- here a blue "?"): opens
    /// miniVivisection_Manual.md (next to the .rhp) in the editor, which shows Markdown
    /// rendered. Opened as a dropped file is (CodeOpenTargetCodeCommand.Open with a
    /// StorageEntry): the file itself, focused again when it's open already.
    /// </summary>
    internal sealed class HelpButton : Drawable
    {
        public const string ManualFile = "miniVivisection_Manual.md";
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private static readonly Color PillColor = Color.FromArgb(47, 128, 237); // the plug-in's blue
        private static readonly Font CaptionFont = new Font(SystemFont.Bold, 8);
        private static readonly Color CaptionColor = Color.FromArgb(150, 150, 150);
        private static string Caption => VNames.Of("Help");

        private readonly object _editor;
        private bool _hover;

        public HelpButton(object editor)
        {
            _editor = editor;
            CanFocus = true;
            Size = new Size((int)Math.Ceiling(CaptionFont.MeasureString(Caption).Width) + 6 + 22, 22);
            ToolTip = "The miniVivisection manual, opened here in the Script Editor (" + ManualFile + ").";
        }

        internal static string ManualPath =>
            Path.Combine(Path.GetDirectoryName(typeof(HelpButton).Assembly.Location) ?? "", ManualFile);

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Buttons == MouseButtons.Primary)
            {
                e.Handled = true;
                Open(_editor);
            }
            base.OnMouseDown(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyData == Keys.Space || e.KeyData == Keys.Enter)
            {
                e.Handled = true;
                Open(_editor);
            }
            base.OnKeyDown(e);
        }

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            _hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.AntiAlias = true;
            if (_hover)
                g.FillPath(Color.FromArgb(128, 128, 128, 48), GraphicsPath.GetRoundRect(new RectangleF(0, 0, Size.Width, Size.Height), 3));
            var caption = g.MeasureString(CaptionFont, Caption);
            g.DrawText(CaptionFont, CaptionColor, 3, (Size.Height - caption.Height) / 2, Caption);
            var pill = new RectangleF(caption.Width + 6, (Size.Height - 15) / 2f, 15, 15);
            g.FillEllipse(PillColor, pill);
            var font = new Font(SystemFont.Bold, 8);
            var mark = g.MeasureString(font, "?");
            g.DrawText(font, Colors.White, pill.X + (pill.Width - mark.Width) / 2, pill.Y + (pill.Height - mark.Height) / 2, "?");
        }

        /// <summary>RhinoCode's Markdown language, started if it isn't yet: languages start when
        /// first needed, and a Grasshopper component's editor starts only Python or C# -- the
        /// manual then had no language ("Can not determine language") and went to Windows'
        /// app. Started as Grasshopper starts its languages (RhinoCodePlatform.Rhino3D's
        /// Registrar.StartScriptingLanguages).</summary>
        private static void StartMarkdown()
        {
            try
            {
                var spec = Rhino.Runtime.Code.Languages.LanguageSpec.Markdown;
                if (Rhino.Runtime.Code.RhinoCode.Languages.QueryLatest(spec) != null)
                    return;
                var platform = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "RhinoCodePlatform.Rhino3D");
                var start = platform?.GetType("RhinoCodePlatform.Rhino3D.Registrar")?.GetMethod("StartScriptingLanguages",
                    BindingFlags.Public | BindingFlags.Static, null, new[] { spec.GetType(), typeof(bool) }, null);
                if (start == null)
                {
                    Log.Info(LogArea.Tabs, "manual: RhinoCode's Registrar.StartScriptingLanguages isn't there");
                    return;
                }
                start.Invoke(null, new object[] { spec, true });
                Log.Info(LogArea.Tabs, "manual: Markdown started ({0})", Rhino.Runtime.Code.RhinoCode.Languages.QueryLatest(spec)?.Id);
            }
            catch (Exception ex)
            {
                Log.Info(LogArea.Tabs, "manual: starting Markdown: {0}", Log.Describe(ex));
            }
        }

        /// <summary>The manual in <paramref name="editor"/> (an RCE window).</summary>
        public static void Open(object editor)
        {
            var path = ManualPath;
            if (!File.Exists(path))
            {
                UserLog.Warning("The manual isn't next to the plug-in: {0}", path);
                return;
            }
            try
            {
                // The script, made here (the editor's own Open swallows its errors): as
                // Markdown, which the editor shows rendered.
                StartMarkdown();
                Rhino.Runtime.Code.Code code;
                try
                {
                    code = new StorageEntry(new Uri(path), Rhino.Runtime.Code.Languages.LanguageSpec.Markdown).CreateCode();
                }
                catch (Exception ex)
                {
                    Log.Info(LogArea.Tabs, "manual as Markdown: {0}; as the file says", Log.Describe(ex));
                    code = new StorageEntry(new Uri(path)).CreateCode();
                }
                if (code == null)
                    throw new InvalidOperationException("no language for " + Path.GetExtension(path) + " files");
                // Opened as the editor opens a script: BaseCommand.OpenCode(RCE, Code, AddOptions).
                var assembly = editor.GetType().Assembly;
                var baseCommand = assembly.GetType("RhinoCodeEditor.Editor.Commands.BaseCommand", throwOnError: true);
                var openCode = baseCommand.GetMethods(Any).FirstOrDefault(m => m.Name == "OpenCode" && m.IsStatic && m.GetParameters().Length == 3 &&
                        m.GetParameters()[1].ParameterType == typeof(Rhino.Runtime.Code.Code))
                    ?? throw new MissingMethodException(baseCommand.FullName, "OpenCode");
                openCode.Invoke(null, new object[] { editor, code, null });
                Log.Info(LogArea.Tabs, "opened the manual ({0}, editor kind {1}): {2}", code.Language?.Id, code.Language?.Capabilities?.EditorKind, path);
            }
            catch (Exception ex)
            {
                while (ex is TargetInvocationException && ex.InnerException != null)
                    ex = ex.InnerException;
                UserLog.Warning("Can't open the manual in the Script Editor ({0}) -- opened with Windows' app for .md instead: {1}", ex.Message, path);
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                }
                catch (Exception shell)
                {
                    UserLog.Warning("Can't open the manual: {0}", shell.Message);
                }
            }
        }
    }
}
