using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace MiniVivisection
{
    /// <summary>
    /// The letters of Markdown texts' links in the link color (setting "Markdown links in color";
    /// the color: the document's Markdown style, a { color }), drawn over the text: a Rhino text object has one color, and
    /// its rich text none of its own. Drawn, not made into objects -- a letter as an object of its
    /// own would lie on its text, and a click there would ask which of the two (Rhino's selection
    /// menu). Each link's glyphs (TextEntity.CreateCurvesGrouped, counted as the links are) filled
    /// as solid hatches, a hair in front of the text, kept per object until the document changes;
    /// not while the text is selected (its highlight shows), nor after its words were changed in
    /// Rhino (its characters may not be where they were). A change of the color shows at once.
    /// </summary>
    internal sealed class MarkdownLinkLetters : DisplayConduit
    {
        private static MarkdownLinkLetters s_instance;

        // By the object's id and serial number (RhinoCommon makes a new wrapper at every lookup)
        private static readonly Dictionary<(Guid, uint), Hatch[]> s_letters = new Dictionary<(Guid, uint), Hatch[]>();

        public static void Install()
        {
            if (s_instance != null)
                return;
            s_instance = new MarkdownLinkLetters { Enabled = true };
            MiniVivisectionSettings.Changed += key =>
            {
                if (key == "MarkdownLinksColored")
                    RhinoDoc.ActiveDoc?.Views.Redraw();
            };
        }

        /// <summary>An object changed (MarkdownTextMouse): its letters made again when drawn
        /// (Guid.Empty: every object's -- another document).</summary>
        public static void Forget(Guid id)
        {
            lock (s_letters)
            {
                if (id == Guid.Empty)
                    s_letters.Clear();
                else
                {
                    foreach (var key in s_letters.Keys.Where(k => k.Item1 == id).ToList())
                        s_letters.Remove(key);
                }
            }
            lock (s_meshes)
            {
                if (id == Guid.Empty)
                    s_meshes.Clear();
                else
                {
                    foreach (var key in s_meshes.Keys.Where(k => k.Item1 == id).ToList())
                        s_meshes.Remove(key);
                }
            }
        }

        // A text part's letters as one mesh, in a color: one draw for all of them (a hatch each was a
        // draw each -- a model with a thousand link letters drew slowly at every change)
        private static readonly Dictionary<(Guid, uint), (Mesh Mesh, Color Color)> s_meshes = new Dictionary<(Guid, uint), (Mesh, Color)>();

        protected override void PostDrawObjects(DrawEventArgs e)
        {
            try
            {
                var doc = e.RhinoDoc;
                if (!MiniVivisectionSettings.MarkdownLinksColored || doc == null)
                    return;
                var color = MarkdownStyleFiles.ForDocument(doc).LinkColor;
                foreach (var part in MarkdownTextMouse.LinkedParts(doc))
                {
                    if (part.IsSelected(false) > 0)
                        continue;
                    var mesh = MeshOf(doc, part, color);
                    if (mesh != null)
                        e.Display.DrawMeshFalseColors(mesh);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.General, "MarkdownText: links' letters: {0}", Log.Describe(ex));
            }
        }

        /// <summary>A text part's links' letters as one mesh in <paramref name="color"/> (its vertex
        /// colors), made from their outlines; null: none. Kept per object, colored again when the color changes.</summary>
        internal static Mesh MeshOf(RhinoDoc doc, RhinoObject part, Color color)
        {
            var key = (part.Id, part.RuntimeSerialNumber);
            (Mesh Mesh, Color Color) known;
            lock (s_meshes)
                s_meshes.TryGetValue(key, out known);
            if (known.Mesh == null && !s_meshes.ContainsKey(key))
            {
                Mesh mesh = null;
                var letters = LettersOf(doc, part);
                if (letters.Length > 0)
                {
                    try
                    {
                        var tolerance = Math.Max(doc.ModelAbsoluteTolerance * 0.1, 1e-6);
                        var outlines = letters.SelectMany(h => h.Get3dCurves(true).Concat(h.Get3dCurves(false))).ToList();
                        var breps = Brep.CreatePlanarBreps(outlines, tolerance) ?? new Brep[0];
                        mesh = new Mesh();
                        foreach (var brep in breps)
                        {
                            foreach (var piece in Mesh.CreateFromBrep(brep, MeshingParameters.FastRenderMesh) ?? new Mesh[0])
                                mesh.Append(piece);
                        }
                        if (mesh.Faces.Count == 0)
                            mesh = null;
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(LogArea.General, "MarkdownText: links' letters as a mesh: {0}", Log.Describe(ex));
                        mesh = null;
                    }
                }
                known = (mesh, Color.Empty);
            }
            if (known.Mesh != null && known.Color != color)
            {
                known.Mesh.VertexColors.CreateMonotoneMesh(color);
                known = (known.Mesh, color);
            }
            lock (s_meshes)
                s_meshes[key] = known;
            return known.Mesh;
        }

        /// <summary>A text part's links' letters, filled (world; none when its words were changed
        /// in Rhino). Also for a test.</summary>
        internal static Hatch[] LettersOf(RhinoDoc doc, RhinoObject part)
        {
            var key = (part.Id, part.RuntimeSerialNumber);
            lock (s_letters)
            {
                if (s_letters.TryGetValue(key, out var known))
                    return known;
            }
            var letters = new List<Hatch>();
            if (part.Geometry is TextEntity text && RhinoTextLayout.Shown(text) == part.Attributes.GetUserString(RhinoTextLayout.ShownKey))
            {
                var links = RhinoTextLayout.ParseLinks(part.Attributes.GetUserString(RhinoTextLayout.LinksKey));
                var style = doc.DimStyles.FindId(text.DimensionStyleId) ?? doc.DimStyles.Current;
                var glyphs = text.CreateCurvesGrouped(style, false, 1.0, 0.0) ?? new List<Curve[]>();
                if (glyphs.Count == RhinoTextLayout.Shown(text).Count(c => !char.IsWhiteSpace(c)))
                {
                    var h = text.TextHeight;
                    var lift = text.Plane.ZAxis * (h * RhinoTextLayout.LetterLift);
                    foreach (var (start, length, _) in links)
                    {
                        for (var i = Math.Max(0, start); i < start + length && i < glyphs.Count; i++)
                        {
                            foreach (var hatch in RhinoTextLayout.LetterHatches(glyphs[i], h))
                            {
                                hatch.Translate(lift);
                                letters.Add(hatch);
                            }
                        }
                    }
                }
            }
            var result = letters.ToArray();
            lock (s_letters)
                s_letters[key] = result;
            return result;
        }
    }
}
