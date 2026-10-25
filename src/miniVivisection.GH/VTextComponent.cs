using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using GH_IO.Serialization;
using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using MiniVivisection;

namespace MiniVivisection.GH
{
    /// <summary>
    /// V Text: text as Rhino text, as the Panel it comes from shows it -- Markdown laid out as
    /// MarkdownText does, a Python or C# script as code (Consolas, line numbers, syntax colors),
    /// anything else as plain text (RhinoTextLayout). Inputs: the text (a list: its lines), its
    /// height (a paragraph's or a line's; in points, made model units -- or in model units: the
    /// menu), the anchor point -- the corner of the text the menu says, top left by default --,
    /// an angle about it in the world's XY, and the width text wraps in (nothing: as wide as the
    /// Panel). Outputs: the text's bounding rectangle, aligned with it, and the anchor.
    /// Previewed in the syntax colors; baked (Bake...) as Rhino objects, grouped -- a Markdown
    /// text with its Markdown and frame, so MarkdownText edits it again.
    ///
    /// The Panel's look is followed: after a canvas paint, a V Text whose Panel changed its Show
    /// as, width, font or wrapping -- or whose settings changed -- is solved again.
    /// </summary>
    public sealed class VTextComponent : GH_Component
    {
        internal enum Corner
        {
            TopLeft,
            TopRight,
            BottomLeft,
            BottomRight,
        }

        internal enum Format
        {
            Auto,
            Markdown,
            Python,
            CSharp,
            Text,
        }

        private static readonly Color NumbersColor = Color.FromArgb(140, 140, 140);
        private const string Sample = "The quick brown fox jumps over the lazy dog"; // to measure a Panel's width in
        private static readonly Dictionary<string, double> s_samples = new Dictionary<string, double>();

        private static readonly List<WeakReference<VTextComponent>> s_all = new List<WeakReference<VTextComponent>>();
        private static GH_Canvas s_canvas;

        private Corner _corner = Corner.TopLeft;
        private Format _format = Format.Auto;
        private bool _points = true;
        private string _look; // the Panel's look at the last solution (null: not followed now)

        /// <summary>A solution's text, placed.</summary>
        private sealed class Result
        {
            public List<RhinoTextLayout.Piece> Pieces;
            public List<RhinoTextLayout.Piece> Letters; // the links' letters in the link color, drawn over the text (not baked)
            public ScriptTokens.Language Language;
            public string Text;
            public Plane TopLeft; // the layout's origin, placed
            public double Height;
            public double Width;
            public string Font;
            public RhinoTextLayout Layout; // before it was placed: the parts' places in it
        }

        private readonly List<Result> _results = new List<Result>();
        private BoundingBox _box = BoundingBox.Empty;

        public VTextComponent()
            : base(GhNames.Of("Text"), GhNames.Of("Text"),
                GhNames.Text("A Panel's text as Rhino text, as the Panel shows it: Markdown, code in colors, or plain text. Bake for Rhino objects."),
                "miniVivisection", "Text")
        {
            lock (s_all)
            {
                s_all.RemoveAll(r => !r.TryGetTarget(out _));
                s_all.Add(new WeakReference<VTextComponent>(this));
            }
            FollowCanvas();
        }

        public override Guid ComponentGuid => new Guid("71b06edf-0e1a-44d3-82f6-52b288b44386");

        protected override Bitmap Icon => Icons.Text;

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("Text", "T", "The text -- connect a Panel: shown as it shows it (Markdown rendered, a Python or C# script as code, else plain text; or as the menu's Show as says). A list: its lines.", GH_ParamAccess.list);
            pManager.AddNumberParameter("Height", "H", "The text height of a paragraph (Markdown) or a line: in points (converted to model units), or in model units (menu).", GH_ParamAccess.item, 10);
            pManager.AddPointParameter("Anchor", "A", "Where the text goes: its corner the menu says (top left by default) at this point.", GH_ParamAccess.item, Point3d.Origin);
            pManager.AddNumberParameter("Angle", "R", "The text turned about the anchor, in the world's XY plane (radians; Degrees in this input's menu).", GH_ParamAccess.item, 0);
            ((Param_Number)pManager[3]).AngleParameter = true;
            pManager.AddNumberParameter("Width", "W", "The width Markdown and plain text wrap in, in the Height's unit; 0: no wrapping. Nothing: as wide as the Panel the text comes from (plain text when it wraps), else no wrapping.", GH_ParamAccess.item);
            pManager[4].Optional = true;
            pManager.AddParameter(new FontNameParameter(), "Font", "F", "The base font of Markdown and plain text (code stays in Consolas). Nothing: the dimension style's. Right-click > Set one Text: the installed fonts to pick from.", GH_ParamAccess.item);
            pManager[5].Optional = true;
        }

        // The outputs' order since 2026-10-17 (OutputsVersion 2): Text, Decorators, Bounds, Anchor. Before:
        // Bounds, Anchor (the release), then Bounds, Anchor, Text (a day). Read keeps older files' wires.
        private const int OutputsVersion = 2;
        private static readonly string[] OldOrder = { "Bounds", "Anchor", "Text" };

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGeometryParameter("Text", "T", "The Rhino text objects as the component draws them, a token or a part each -- " +
                "placed and turned as the text is; for Bake components, transforms, V Edit.", GH_ParamAccess.list);
            pManager.AddGeometryParameter("Decorators", "D", "Everything else the text is drawn with: the lines of code frames, rules, " +
                "quotes' bars, tables' grids -- placed and turned as the text is.", GH_ParamAccess.list);
            pManager.AddRectangleParameter("Bounds", "B", "The text's bounding rectangle, aligned with it (turned by Angle).", GH_ParamAccess.item);
            pManager.AddPointParameter("Anchor", "A", "The anchor point: the corner of Bounds the menu says.", GH_ParamAccess.item);
            // Their previews off: the component draws the text in its colors already (DrawViewportWires)
            foreach (var index in new[] { 0, 1 })
            {
                if (pManager[index] is IGH_PreviewObject preview)
                    preview.Hidden = true;
            }
        }

        protected override void BeforeSolveInstance()
        {
            _results.Clear();
            _box = BoundingBox.Empty;
            FollowCanvas();
            base.BeforeSolveInstance();
        }

        protected override void AfterSolveInstance()
        {
            _look = Look();
            base.AfterSolveInstance();
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            var lines = new List<string>();
            if (!da.GetDataList(0, lines))
                return;
            var text = string.Join("\n", lines.Select(l => l ?? "")).Replace("\r\n", "\n");
            var height = 10.0;
            da.GetData(1, ref height);
            var anchor = Point3d.Origin;
            da.GetData(2, ref anchor);
            var angle = 0.0;
            da.GetData(3, ref angle);
            if (((Param_Number)Params.Input[3]).UseDegrees)
                angle = RhinoMath.ToRadians(angle);
            var width = 0.0;
            var hasWidth = da.GetData(4, ref width);
            string font = null;
            da.GetData(5, ref font);
            font = string.IsNullOrWhiteSpace(font) ? null : font.Trim();
            if (!(height > 0) || double.IsInfinity(height))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Height must be more than 0.");
                return;
            }

            var doc = RhinoDoc.ActiveDoc;
            var scale = _points ? RhinoMath.UnitScale(UnitSystem.Inches, doc?.ModelUnitSystem ?? UnitSystem.Millimeters) / 72.0 : 1.0;
            var h = height * scale;
            var panel = SourcePanel();
            var language = LanguageOf(panel, text);
            var code = language == ScriptTokens.Language.Python || language == ScriptTokens.Language.CSharp;
            var style = doc?.DimStyles.Current;
            var wrap = code ? 0 : hasWidth ? Math.Max(0, width) * scale : PanelWidth(panel, language, style, h, font);
            RhinoTextLayout layout;
            if (language == ScriptTokens.Language.Markdown)
                layout = RhinoTextLayout.Markdown(WithFields(MarkdownOf(text), text), style, h, wrap, font, text, ScriptPanels.RhinoStyle, ScriptPanels.LinksColored, letters: true);
            else if (code)
                layout = RhinoTextLayout.Code(text, language, style, h, kind => ScriptPanels.ColorOf(kind, ScriptPanels.Light, Color.Empty), NumbersColor);
            else
                layout = RhinoTextLayout.Plain(text, style, h, wrap, font);

            // The anchor's corner of the text's bounds at the anchor, turned about it
            var bounds = layout.Bounds.IsValid ? layout.Bounds : new BoundingBox(Point3d.Origin, Point3d.Origin);
            var left = _corner == Corner.TopLeft || _corner == Corner.BottomLeft;
            var top = _corner == Corner.TopLeft || _corner == Corner.TopRight;
            var corner = new Point3d(left ? bounds.Min.X : bounds.Max.X, top ? bounds.Max.Y : bounds.Min.Y, 0);
            var plane = new Plane(anchor, Vector3d.XAxis, Vector3d.YAxis);
            plane.Rotate(angle, plane.ZAxis);
            var xform = Transform.PlaneToPlane(Plane.WorldXY, plane) * Transform.Translation(-(Vector3d)corner);
            var pieces = layout.Placed(xform).ToList();
            var rectangle = new Rectangle3d(plane, new Interval(bounds.Min.X - corner.X, bounds.Max.X - corner.X), new Interval(bounds.Min.Y - corner.Y, bounds.Max.Y - corner.Y));
            var topLeft = plane;
            topLeft.Origin = plane.PointAt(-corner.X, -corner.Y);
            _results.Add(new Result { Pieces = pieces, Letters = layout.Placed(xform, layout.Letters).ToList(), Language = language, Text = text, TopLeft = topLeft, Height = h, Width = wrap, Font = font, Layout = layout });
            foreach (var piece in pieces)
                _box.Union(piece.Geometry.GetBoundingBox(true));

            da.SetDataList(0, pieces.Where(p => p.Geometry is TextEntity).Select(p => new GH_TextEntity((TextEntity)p.Geometry.Duplicate())));
            da.SetDataList(1, pieces.Where(p => !(p.Geometry is TextEntity)).Select(p => p.Geometry is Hatch hatch ? (IGH_GeometricGoo)new GH_Hatch((Hatch)hatch.Duplicate()) : GH_Convert.ToGeometricGoo(p.Geometry.Duplicate())).Where(g => g != null));
            da.SetData(2, rectangle);
            da.SetData(3, anchor);
            Message = NameOf(language) + ", " + NameOf(_corner);
        }

        // --- what and how wide ----------------------------------------------------------------------

        private GH_Panel SourcePanel() => Params.Input[0].Sources.OfType<GH_Panel>().FirstOrDefault();

        private ScriptTokens.Language LanguageOf(GH_Panel panel, string text)
        {
            switch (_format)
            {
                case Format.Markdown:
                    return ScriptTokens.Language.Markdown;
                case Format.Python:
                    return ScriptTokens.Language.Python;
                case Format.CSharp:
                    return ScriptTokens.Language.CSharp;
                case Format.Text:
                    return ScriptTokens.Language.None;
                default:
                    return ScriptPanels.LanguageShown(panel, text);
            }
        }

        private static List<MarkdownBlocks.Block> MarkdownOf(string text)
        {
            if (ScriptPanels.UseMarkdig)
            {
                try
                {
                    return MarkdownMarkdig.Parse(text);
                }
                catch (Exception)
                {
                    // miniVivisection's parser, as the Panel does
                }
            }
            return MarkdownBlocks.Parse(text);
        }

        // The Panel's width as the text's: its text area, measured in a sample sentence in its
        // font, made as many of the sample in Rhino's text. Markdown wraps in it, plain text when
        // the Panel wraps; 0 without a Panel.
        private static double PanelWidth(GH_Panel panel, ScriptTokens.Language language, DimensionStyle style, double h, string font)
        {
            if (panel?.Attributes == null || (language == ScriptTokens.Language.None && !panel.Properties.Wrap))
                return 0;
            var sample = PanelSample(panel.Properties.Font ?? GH_FontServer.StandardAdjusted);
            if (!(sample > 0))
                return 0;
            var textWidth = Math.Max(10.0, panel.Attributes.Bounds.Width - 18.0); // less the scroll bar and margins
            return textWidth / sample * RhinoTextLayout.PlainWidth(Sample, style, h, font);
        }

        // The sample's width in a Panel's font on the canvas (canvas units), as the Panel measures it
        private static double PanelSample(System.Drawing.Font font)
        {
            var canvas = Instances.ActiveCanvas;
            var onCanvas = canvas != null && canvas.IsHandleCreated && !canvas.InvokeRequired;
            var key = font.Name + "|" + font.SizeInPoints + "|" + font.Style + "|" + (onCanvas ? canvas.DeviceDpi : 0);
            lock (s_samples)
            {
                if (s_samples.TryGetValue(key, out var width))
                    return width;
                using (var bitmap = onCanvas ? null : new Bitmap(1, 1))
                using (var graphics = onCanvas ? canvas.CreateGraphics() : Graphics.FromImage(bitmap))
                    width = graphics.MeasureString(Sample, font, PointF.Empty, StringFormat.GenericTypographic).Width;
                return s_samples[key] = width;
            }
        }

        // The fields ({{protected}}, {{modified}}): the definition's V°Protect's state
        private List<MarkdownBlocks.Block> WithFields(List<MarkdownBlocks.Block> blocks, string text) =>
            MarkdownFields.Has(text) ? MarkdownFields.Expand(blocks, ProtectComponent.FieldState(OnPingDocument())) : blocks;

        // What the text's look depends on besides its data: the Panel's, the settings', the fields' state
        private string Look()
        {
            var panel = SourcePanel();
            var look = ScriptPanels.Look + "|" + _format + "|" + ProtectComponent.FieldState(OnPingDocument()).Signature;
            if (panel == null)
                return look;
            var doc = Rhino.RhinoDoc.ActiveDoc;
            return look + "|" + ScriptPanels.ShowAsOf(panel) + "|" + panel.SourceCount + "|" + Math.Round(panel.Attributes?.Bounds.Width ?? 0) + "|" +
                (panel.Properties.Font?.SizeInPoints ?? 0) + "|" + panel.Properties.Font?.Name + "|" + panel.Properties.Font?.Style + "|" + panel.Properties.Wrap + "|" +
                (Instances.ActiveCanvas?.DeviceDpi ?? 96) + "|" + doc?.ModelUnitSystem + "|" + doc?.DimStyles.Current?.Id; // the model's units and style: the text's size and font
        }

        private static void FollowCanvas()
        {
            var canvas = Instances.ActiveCanvas;
            if (canvas == null || ReferenceEquals(canvas, s_canvas))
                return;
            if (s_canvas != null)
                s_canvas.CanvasPaintEnd -= AfterPaint;
            s_canvas = canvas;
            canvas.CanvasPaintEnd += AfterPaint;
        }

        // After a canvas paint: the V Texts whose Panel or settings look different now, solved again
        private static void AfterPaint(GH_Canvas canvas)
        {
            try
            {
                List<VTextComponent> all;
                lock (s_all)
                    all = s_all.Select(r => r.TryGetTarget(out var v) ? v : null).Where(v => v != null).ToList();
                foreach (var v in all)
                {
                    var doc = v.OnPingDocument();
                    if (doc == null || !ReferenceEquals(doc, canvas.Document) || v._look == null || v.Locked || v.Look() == v._look)
                        continue;
                    v._look = null; // once: solved, it's followed again
                    doc.ScheduleSolution(1, d => v.ExpireSolution(false));
                }
            }
            catch (Exception)
            {
                // a paint isn't the place for it
            }
        }

        private static string NameOf(ScriptTokens.Language language) =>
            language == ScriptTokens.Language.Markdown ? "Markdown"
            : language == ScriptTokens.Language.Python ? "Python"
            : language == ScriptTokens.Language.CSharp ? "C#" : "Text";

        private static string NameOf(Corner corner) =>
            corner == Corner.TopLeft ? "top left" : corner == Corner.TopRight ? "top right" : corner == Corner.BottomLeft ? "bottom left" : "bottom right";

        // --- preview and bake -----------------------------------------------------------------------

        public override BoundingBox ClippingBox
        {
            get
            {
                var box = base.ClippingBox;
                box.Union(_box);
                return box;
            }
        }

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            base.DrawViewportWires(args); // Bounds and Anchor
            var selected = Attributes?.Selected == true;
            var wire = selected ? args.WireColour_Selected : args.WireColour;
            foreach (var result in _results)
            {
                foreach (var piece in result.Pieces)
                {
                    var color = selected || piece.Color.IsEmpty ? wire : piece.Color;
                    if (piece.Geometry is TextEntity text)
                        args.Display.DrawText(text, color);
                    else if (piece.Geometry is Curve curve)
                        args.Display.DrawCurve(curve, color);
                    else if (piece.Geometry is Hatch hatch)
                        args.Display.DrawHatch(hatch, color, color); // a link's bar
                }
                if (!selected)
                {
                    foreach (var letter in result.Letters ?? new List<RhinoTextLayout.Piece>())
                        args.Display.DrawHatch((Hatch)letter.Geometry, letter.Color, letter.Color); // a link's letters, over the text
                }
            }
        }

        public override bool IsBakeCapable => _results.Count > 0;

        public override void BakeGeometry(RhinoDoc doc, List<Guid> obj_ids) => BakeGeometry(doc, null, obj_ids);

        /// <summary>Each solution's text as Rhino objects, grouped; a token's color as the object's;
        /// Markdown keeping what MarkdownText's parts keep (its Markdown, frame, id and each part's
        /// place, characters and links): edited again by MarkdownText or a double-click, its links
        /// followed.</summary>
        public override void BakeGeometry(RhinoDoc doc, ObjectAttributes att, List<Guid> obj_ids)
        {
            if (doc == null)
                return;
            foreach (var result in _results)
            {
                var attributes = (att ?? doc.CreateDefaultAttributes()).Duplicate();
                var markdown = result.Language == ScriptTokens.Language.Markdown;
                var textId = Guid.NewGuid();
                if (markdown)
                {
                    attributes.SetUserString(RhinoTextLayout.MarkdownKey, result.Text);
                    attributes.SetUserString(RhinoTextLayout.FrameKey, RhinoTextLayout.FrameText(result.TopLeft, result.Height, result.Width, result.Font));
                }
                var ids = new List<Guid>();
                for (var index = 0; index < result.Pieces.Count; index++)
                {
                    var piece = result.Pieces[index];
                    var pieceAttributes = attributes;
                    if (markdown)
                    {
                        pieceAttributes = attributes.Duplicate();
                        foreach (var (key, value) in RhinoTextLayout.PartStrings(piece, result.Layout.Pieces[index], index, textId, result.Layout.BlocksText()))
                            pieceAttributes.SetUserString(key, value);
                    }
                    if (!piece.Color.IsEmpty)
                    {
                        pieceAttributes = pieceAttributes.Duplicate();
                        pieceAttributes.ObjectColor = piece.Color;
                        pieceAttributes.ColorSource = ObjectColorSource.ColorFromObject;
                    }
                    var id = doc.Objects.Add(piece.Geometry.Duplicate(), pieceAttributes);
                    if (id != Guid.Empty)
                        ids.Add(id);
                }
                if (ids.Count > 1)
                    doc.Groups.Add(ids);
                obj_ids?.AddRange(ids);
            }
        }

        // --- menu and file --------------------------------------------------------------------------

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            Menu_AppendSeparator(menu);
            foreach (var corner in new[] { Corner.TopLeft, Corner.TopRight, Corner.BottomLeft, Corner.BottomRight })
            {
                var item = Menu_AppendItem(menu, "Anchor: " + NameOf(corner), (s, e) => Set("Anchor", () => _corner = corner), true, _corner == corner);
                item.ToolTipText = "The anchor point is the text's " + NameOf(corner) + " corner.";
            }
            Menu_AppendSeparator(menu);
            var showAs = Menu_AppendItem(menu, "Show as");
            showAs.ToolTipText = "Autodetect: as the Panel the text comes from shows it (its Show as; Markdown, a script as code, else plain text).";
            foreach (var (format, text) in new[] { (Format.Auto, "Autodetect (code or Markdown)"), (Format.Markdown, "Markdown (rendered)"), (Format.Python, "Python code"), (Format.CSharp, "C# code"), (Format.Text, "Plain text") })
                showAs.DropDownItems.Add(new ToolStripMenuItem(text, null, (s, e) => Set("Show as", () => _format = format)) { Checked = _format == format });
            Menu_AppendSeparator(menu);
            Menu_AppendItem(menu, "Sizes in points (pt)", (s, e) => Set("Sizes", () => _points = true), true, _points).ToolTipText = "Height and Width in points (converted to model units; 10 pt: 3.53 mm).";
            Menu_AppendItem(menu, "Sizes in model units", (s, e) => Set("Sizes", () => _points = false), true, !_points).ToolTipText = "Height and Width in the model's units.";
        }

        private void Set(string what, Action change)
        {
            RecordUndoEvent(what);
            change();
            ExpireSolution(true);
        }

        public override bool Write(GH_IWriter writer)
        {
            writer.SetInt32("MiniVivisectionCorner", (int)_corner);
            writer.SetInt32("MiniVivisectionFormat", (int)_format);
            writer.SetBoolean("MiniVivisectionPoints", _points);
            writer.SetInt32("MiniVivisectionOutputs", OutputsVersion);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            var value = 0;
            if (reader.TryGetInt32("MiniVivisectionCorner", ref value) && Enum.IsDefined(typeof(Corner), value))
                _corner = (Corner)value;
            if (reader.TryGetInt32("MiniVivisectionFormat", ref value) && Enum.IsDefined(typeof(Format), value))
                _format = (Format)value;
            var points = true;
            if (reader.TryGetBoolean("MiniVivisectionPoints", ref points))
                _points = points;
            var version = 0;
            reader.TryGetInt32("MiniVivisectionOutputs", ref version);
            if (version >= OutputsVersion)
                return base.Read(reader);
            // An older file: its outputs read in their old order -- as many as it has -- then put in today's
            // order; the wires belong to the outputs (by their ids), so they stay with Bounds and Anchor.
            var saved = 0;
            while (reader.ChunkExists("param_output", saved))
                saved++;
            var now = Params.Output.ToList();
            var old = OldOrder.Take(saved).Select(name => now.FirstOrDefault(p => p.Name == name)).Where(p => p != null).ToList();
            Params.Output.Clear();
            Params.Output.AddRange(old);
            bool read;
            try
            {
                read = base.Read(reader);
            }
            finally
            {
                Params.Output.Clear();
                Params.Output.AddRange(now);
                Params.OnParametersChanged();
            }
            return read;
        }
    }
}
