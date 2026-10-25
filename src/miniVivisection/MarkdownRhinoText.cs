using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace MiniVivisection
{
    /// <summary>
    /// Markdown as Rhino objects (command MarkdownText): parsed as a Grasshopper Panel's
    /// Markdown is (Rhino's Markdig or miniVivisection's parser: setting "Markdown parser"),
    /// then laid out from a plane's origin down, in a width, in a base font (RhinoTextLayout,
    /// V Text's too) -- headings as larger bold text (H1 and H2 with a line under them),
    /// paragraphs as wrapped rich text, lists, quotes, code blocks framed, tables as grids, rules.
    ///
    /// The parts are linked by the text's id (user strings), not only by their group: each keeps
    /// the Markdown, the frame, which part it is (its block and source lines, its place in the
    /// layout, its characters' places in the source, its links). So any part -- also ungrouped,
    /// also picked alone -- edits the whole text; changes made to the parts in Rhino (a text's
    /// words, moving, turning or scaling all of it, deleting a block's parts) are taken back
    /// into the Markdown before it's edited (Reconcile); and an edit updates the parts in place,
    /// the same objects (Update) -- new ones only when the text needs more.
    /// </summary>
    internal static class MarkdownRhinoText
    {
        public const string SourceKey = RhinoTextLayout.MarkdownKey;
        public const string FrameKey = RhinoTextLayout.FrameKey;

        /// <summary>A text with fields ({{protected}}, {{modified}}): the state its parts show, in a
        /// word (MarkdownFields.State.Signature) -- laid out again when the state isn't that any more.</summary>
        public const string FieldsKey = "minivivisection.markdown.fields";

        /// <summary>Where and how large: the plane (origin: the top left), the text height of
        /// a paragraph, the width text wraps in (0: none, lines as wide as the widest text), the
        /// base font (null: the dimension style's).</summary>
        public sealed class Frame
        {
            public Plane Plane = Plane.WorldXY;
            public double Height = 1;
            public double Width = 60;
            public string Font;

            public override string ToString() => RhinoTextLayout.FrameText(Plane, Height, Width, Font);

            public Frame Copy() => new Frame { Plane = Plane, Height = Height, Width = Width, Font = Font };

            public static Frame Parse(string text)
            {
                var fields = (text ?? "").Split(';');
                if (fields.Length < 11)
                    return null;
                var v = fields.Take(11).Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN).ToArray();
                if (v.Any(double.IsNaN))
                    return null;
                return new Frame
                {
                    Plane = new Plane(new Point3d(v[0], v[1], v[2]), new Vector3d(v[3], v[4], v[5]), new Vector3d(v[6], v[7], v[8])),
                    Height = v[9],
                    Width = v[10],
                    Font = fields.Length > 11 && fields[11].Trim().Length > 0 ? fields[11].Trim() : null,
                };
            }

            public bool SameAs(Frame other, double tolerance) =>
                other != null && Plane.Origin.DistanceTo(other.Plane.Origin) <= tolerance && Plane.XAxis.EpsilonEquals(other.Plane.XAxis, 1e-9) &&
                Plane.YAxis.EpsilonEquals(other.Plane.YAxis, 1e-9) && Math.Abs(Height - other.Height) <= 1e-9 * Math.Max(1, Height) &&
                Math.Abs(Width - other.Width) <= 1e-9 * Math.Max(1, Width) && string.Equals(Font ?? "", other.Font ?? "", StringComparison.Ordinal);
        }

        /// <summary>A Markdown text found from one of its parts.</summary>
        public sealed class Text
        {
            public Guid Id;                  // Guid.Empty: made before the parts kept it (found by its group)
            public bool Shared;              // the id is a copy's too: an update gives this text its own
            public string Source;
            public Frame Frame;
            public List<RhinoObject> Parts;
            public RhinoObject Picked;       // the part picked alone; null: the whole text
        }

        /// <summary>The blocks of <paramref name="markdown"/>: by Markdig when the setting says
        /// Rhino (the default), else -- or when Markdig fails -- by miniVivisection's parser.</summary>
        public static List<MarkdownBlocks.Block> Parse(string markdown)
        {
            if (MiniVivisectionSettings.GrasshopperMarkdownParser != "miniVivisection")
            {
                try
                {
                    return MarkdownMarkdig.Parse(markdown);
                }
                catch (Exception ex)
                {
                    Log.Warning(LogArea.General, "MarkdownText: Markdig: {0} -- miniVivisection's parser instead", Log.Describe(ex));
                }
            }
            return MarkdownBlocks.Parse(markdown);
        }

        /// <summary>The layout of <paramref name="markdown"/> in <paramref name="doc"/>'s Markdown style (MarkdownStyleFiles).</summary>
        public static RhinoTextLayout Layout(RhinoDoc doc, string markdown, Frame frame) =>
            RhinoTextLayout.Markdown(WithFields(doc, Parse(markdown), markdown), doc.DimStyles.Current, frame.Height, frame.Width, frame.Font, markdown,
                MarkdownStyleFiles.ForDocument(doc), MiniVivisectionSettings.MarkdownLinksColored);

        // The fields' words put in: the file's protection now (RhinoProtect)
        private static List<MarkdownBlocks.Block> WithFields(RhinoDoc doc, List<MarkdownBlocks.Block> blocks, string markdown) =>
            MarkdownFields.Has(markdown) ? MarkdownFields.Expand(blocks, RhinoProtect.FieldState(doc)) : blocks;

        // The state the fields show, in a word (null: no fields)
        private static string FieldsOf(RhinoDoc doc, string markdown) => MarkdownFields.Has(markdown) ? RhinoProtect.FieldState(doc).Signature : null;

        // The parts' own keys (the Markdown and the frame are set every time)
        private static readonly string[] PartKeys =
        {
            RhinoTextLayout.IdKey, RhinoTextLayout.PartKey, RhinoTextLayout.BlocksKey, RhinoTextLayout.ShownKey,
            RhinoTextLayout.PlainKey, RhinoTextLayout.MapKey, RhinoTextLayout.LinksKey, RhinoTextLayout.TaskKey, FieldsKey,
        };

        /// <summary><paramref name="template"/>'s attributes, with what part <paramref name="index"/>
        /// of a Markdown text keeps.</summary>
        public static ObjectAttributes AttributesFor(ObjectAttributes template, RhinoTextLayout layout, RhinoTextLayout.Piece placed, int index, Guid id, string markdown, string frame, string fields = null)
        {
            var attributes = template.Duplicate();
            foreach (var key in PartKeys)
                attributes.DeleteUserString(key);
            attributes.SetUserString(SourceKey, markdown);
            attributes.SetUserString(FrameKey, frame);
            foreach (var (key, value) in RhinoTextLayout.PartStrings(placed, layout.Pieces[index], index, id, layout.BlocksText()))
                attributes.SetUserString(key, value);
            if (fields != null)
                attributes.SetUserString(FieldsKey, fields);
            if (!placed.Color.IsEmpty)
            {
                attributes.ObjectColor = placed.Color;
                attributes.ColorSource = ObjectColorSource.ColorFromObject;
            }
            return attributes;
        }

        /// <summary>The objects of <paramref name="markdown"/> in <paramref name="doc"/>, grouped;
        /// their ids (the group's index in <paramref name="group"/>).</summary>
        public static List<Guid> Create(RhinoDoc doc, string markdown, Frame frame, out int group)
        {
            MarkdownStyleFiles.KeepCurrent(doc); // the file keeps the style its texts are drawn with
            var layout = Layout(doc, markdown, frame);
            var fields = FieldsOf(doc, markdown);
            var template = doc.CreateDefaultAttributes();
            var id = Guid.NewGuid();
            var frameText = frame.ToString();
            var placed = layout.Placed(Transform.PlaneToPlane(Plane.WorldXY, frame.Plane)).ToList();
            var ids = new List<Guid>();
            for (var i = 0; i < placed.Count; i++)
            {
                var added = doc.Objects.Add(placed[i].Geometry, AttributesFor(template, layout, placed[i], i, id, markdown, frameText, fields));
                if (added != Guid.Empty)
                    ids.Add(added);
            }
            group = doc.Groups.Add(ids);
            return ids;
        }

        // --- finding a text -----------------------------------------------------------------------

        private sealed class Part
        {
            public RhinoObject Object;
            public int Index = -1, Block = -1, First, Last;
            public double X, Y, Height;

            public bool IsText => Object.Geometry is TextEntity;

            public int Kind => RhinoTextLayout.KindOf(Object.Geometry);

            public Point3d Anchor => RhinoTextLayout.AnchorOf(Object.Geometry);

            public static Part Of(RhinoObject o)
            {
                var part = new Part { Object = o };
                var fields = (o.Attributes.GetUserString(RhinoTextLayout.PartKey) ?? "").Split(';');
                if (fields.Length >= 7 && int.TryParse(fields[0], out var index) && int.TryParse(fields[1], out var block) &&
                    int.TryParse(fields[2], out var first) && int.TryParse(fields[3], out var last))
                {
                    part.Index = index;
                    part.Block = block;
                    part.First = first;
                    part.Last = last;
                    double.TryParse(fields[4], NumberStyles.Float, CultureInfo.InvariantCulture, out part.X);
                    double.TryParse(fields[5], NumberStyles.Float, CultureInfo.InvariantCulture, out part.Y);
                    double.TryParse(fields[6], NumberStyles.Float, CultureInfo.InvariantCulture, out part.Height);
                }
                return part;
            }
        }

        /// <summary>Whether <paramref name="o"/> is part of a Markdown text.</summary>
        public static bool IsPart(RhinoObject o) => o?.Attributes.GetUserString(SourceKey) != null;

        /// <summary>The part's source lines (0-based), when it keeps them.</summary>
        public static (int First, int Last)? LinesOf(RhinoObject o)
        {
            var part = Part.Of(o);
            return part.Block >= 0 ? (part.First, part.Last) : ((int, int)?)null;
        }

        // Every object of the document that may be a part: hidden and locked ones too
        private static IEnumerable<RhinoObject> AllObjects(RhinoDoc doc) => doc.Objects.GetObjectList(new ObjectEnumeratorSettings
        {
            NormalObjects = true, LockedObjects = true, HiddenObjects = true, ActiveObjects = true, ReferenceObjects = false,
            DeletedObjects = false, IncludeLights = false, IncludeGrips = false, IncludePhantoms = false, VisibleFilter = false,
        });

        /// <summary>The Markdown text <paramref name="any"/> belongs to: its parts (by the text's
        /// id; an older text's: its group), its source and frame -- or null when it isn't one.</summary>
        public static Text Find(RhinoDoc doc, RhinoObject any)
        {
            var source = any?.Attributes.GetUserString(SourceKey);
            var frame = Frame.Parse(any?.Attributes.GetUserString(FrameKey));
            if (source == null || frame == null)
                return null;
            var text = new Text { Source = source, Frame = frame, Picked = any };
            if (Guid.TryParse(any.Attributes.GetUserString(RhinoTextLayout.IdKey), out var id))
            {
                text.Id = id;
                var key = id.ToString("D");
                var all = AllObjects(doc).Where(o => string.Equals(o.Attributes.GetUserString(RhinoTextLayout.IdKey), key, StringComparison.OrdinalIgnoreCase)).ToList();
                if (all.All(o => o.Id != any.Id))
                    all.Add(any);
                text.Parts = Family(all, any, frame, out text.Shared);
            }
            else
            {
                var objects = new List<RhinoObject> { any };
                foreach (var index in any.Attributes.GetGroupList() ?? new int[0])
                    objects.AddRange(doc.Groups.GroupMembers(index).Where(o => o.Id != any.Id && IsPart(o)));
                text.Parts = objects;
            }
            return text;
        }

        // A copied text has the same id: when a part is there twice, the copy picked's parts --
        // those whose frame (inferred from where each is) is the picked one's; a part only the
        // other copy has (it was edited to be longer) is not this text's. Text parts give the
        // frame's origin exactly (their height says how the copy was scaled); a curve is taken
        // when it is nearer this copy's origin than any other copy's.
        private static List<RhinoObject> Family(List<RhinoObject> all, RhinoObject picked, Frame frame, out bool shared)
        {
            var parts = all.Select(Part.Of).ToList();
            shared = parts.GroupBy(p => p.Index).Any(g => g.Count() > 1);
            if (!shared)
                return all;
            double Scale(Part p) => p.IsText && p.Height > 0 && p.Object.Geometry is TextEntity t ? t.TextHeight / p.Height : 1;
            Point3d Origin(Part p) => p.Anchor - frame.Plane.XAxis * (p.X * Scale(p)) - frame.Plane.YAxis * (p.Y * Scale(p));
            var mine = parts.First(p => p.Object.Id == picked.Id);
            var at = Origin(mine);
            // The other copies' origins: those of the text parts that aren't at this one's
            var near = Math.Max(frame.Height, 1e-6) * 2;
            var others = parts.Where(p => p.IsText).Select(Origin).Where(o => o.DistanceTo(at) > near).ToList();
            var family = new List<RhinoObject>();
            foreach (var group in parts.GroupBy(p => p.Index))
            {
                var nearest = group.OrderBy(p => Origin(p).DistanceTo(at)).First();
                var distance = Origin(nearest).DistanceTo(at);
                if (nearest.IsText ? distance <= near : others.All(o => distance < o.DistanceTo(Origin(nearest))))
                    family.Add(nearest.Object);
            }
            if (family.All(o => o.Id != picked.Id))
                family.Add(picked);
            return family;
        }

        // --- what changed in Rhino ----------------------------------------------------------------

        /// <summary>The Markdown and frame of <paramref name="text"/> with what was changed in
        /// Rhino taken in: a text part's words (mapped back into the source, its formatting kept
        /// where untouched), the text moved, turned or scaled (from its first text part), a
        /// block's parts all deleted (its lines taken out). Notes say what was taken; the picked
        /// part's block is [PartStart, PartEnd) in the new source (-1: none).</summary>
        public static (string Source, Frame Frame, List<string> Notes, int PartStart, int PartEnd) Reconcile(RhinoDoc doc, Text text)
        {
            var source = text.Source;
            var frame = text.Frame.Copy();
            var notes = new List<string>();
            var edits = new List<(int Start, int End, string Text)>();
            // Parts of an older state of the text (left over when they couldn't be deleted) say
            // nothing about this source: their characters map into another one
            var parts = text.Parts.Select(Part.Of).Where(p => p.Index >= 0 && p.Object.Attributes.GetUserString(SourceKey) == source).ToList();
            var lineStarts = LineStarts(source);
            int LineStart(int line) => line <= 0 ? 0 : line < lineStarts.Count ? lineStarts[line] : source.Length;
            int LineEnd(int line) => line + 1 < lineStarts.Count ? lineStarts[line + 1] : source.Length; // with its line break

            // Moved, turned or scaled: from the first text part
            var reference = parts.Where(p => p.IsText && p.Height > 0).OrderBy(p => p.Index).FirstOrDefault();
            if (reference != null)
            {
                var entity = (TextEntity)reference.Object.Geometry;
                var plane = entity.Plane;
                var k = entity.TextHeight / reference.Height;
                var origin = plane.Origin - plane.XAxis * (reference.X * k) - plane.YAxis * (reference.Y * k);
                var moved = new Frame { Plane = new Plane(origin, plane.XAxis, plane.YAxis), Height = frame.Height * k, Width = frame.Width * k, Font = frame.Font };
                if (!moved.SameAs(frame, doc.ModelAbsoluteTolerance))
                {
                    var how = new List<string>();
                    if (origin.DistanceTo(frame.Plane.Origin) > doc.ModelAbsoluteTolerance)
                        how.Add("moved");
                    if (!plane.XAxis.EpsilonEquals(frame.Plane.XAxis, 1e-9) || !plane.YAxis.EpsilonEquals(frame.Plane.YAxis, 1e-9))
                        how.Add("turned");
                    if (Math.Abs(k - 1) > 1e-9)
                        how.Add("scaled");
                    if (how.Count > 0)
                        notes.Add("The text was " + string.Join(", ", how) + " in Rhino: kept.");
                    frame = moved;
                }
            }

            // A text part's words changed: the changes put into the source where its characters came from
            foreach (var part in parts.Where(p => p.IsText).OrderBy(p => p.Index))
            {
                var attributes = part.Object.Attributes;
                var then = attributes.GetUserString(RhinoTextLayout.ShownKey);
                var plain = attributes.GetUserString(RhinoTextLayout.PlainKey);
                var map = RhinoTextLayout.ParseMap(attributes.GetUserString(RhinoTextLayout.MapKey));
                if (then == null || plain == null || map == null || map.Length != plain.Length)
                    continue;
                var now = RhinoTextLayout.Shown((TextEntity)part.Object.Geometry);
                if (now == then)
                    continue;
                var code = IsCodeBlock(source, part, lineStarts);
                var toPlain = Align(then, plain);
                int Mapped(int i) => i >= 0 && i < toPlain.Length && toPlain[i] >= 0 ? map[toPlain[i]] : -1;
                var taken = 0;
                foreach (var (a0, a1, inserted) in Diff(then, now))
                {
                    var offsets = Enumerable.Range(a0, a1 - a0).Select(Mapped).Where(o => o >= 0).ToList();
                    int start, end;
                    if (offsets.Count > 0)
                    {
                        start = offsets.Min();
                        end = offsets.Max() + 1;
                    }
                    else
                    {
                        // Nothing of the source changed there: inserted after the character before, or before the one after
                        var before = Enumerable.Range(0, a0).Reverse().Select(Mapped).FirstOrDefault(o => o >= 0, -1);
                        var after = Enumerable.Range(a1, Math.Max(0, then.Length - a1)).Select(Mapped).FirstOrDefault(o => o >= 0, -1);
                        if (inserted.Length == 0 || (before < 0 && after < 0))
                            continue;
                        start = end = before >= 0 ? before + 1 : after;
                    }
                    edits.Add((start, end, code ? inserted : inserted.Replace('\n', ' ')));
                    taken++;
                }
                if (taken > 0)
                    notes.Add("Text changed in Rhino (" + LinesName(part.First, part.Last) + "): taken into the Markdown.");
            }

            // Blocks whose parts were all deleted: their lines taken out
            var blocks = BlocksOf(text.Parts.Select(o => o.Attributes.GetUserString(RhinoTextLayout.BlocksKey)).FirstOrDefault(b => b != null));
            if (blocks != null && parts.Count > 0)
            {
                var present = new HashSet<int>(parts.Select(p => p.Block));
                foreach (var (block, first, last) in blocks.Distinct().Where(b => b.Block >= 0 && !present.Contains(b.Block)).OrderBy(b => b.First))
                {
                    var start = LineStart(first);
                    var end = LineEnd(last);
                    // and one empty line after it, so no two are left
                    if (end < source.Length && LineIsBlank(source, end, lineStarts) && (start == 0 || LineIsBlank(source, LineStart(first - 1), lineStarts)))
                        end = LineEnd(LineOf(end, lineStarts));
                    edits.Add((start, end, ""));
                    notes.Add("Deleted in Rhino (" + LinesName(first, last) + "): taken out of the Markdown.");
                }
            }

            // The picked part's block, where it is after the changes
            var partStart = -1;
            var partEnd = -1;
            var picked = text.Picked == null ? null : Part.Of(text.Picked);
            if (picked != null && picked.Block >= 0)
            {
                partStart = LineStart(picked.First);
                partEnd = Math.Min(source.Length, LineEnd(picked.Last));
            }

            // The edits, from the end (one overlapping another left out)
            var applied = new List<(int Start, int End, string Text)>();
            foreach (var edit in edits.OrderByDescending(e => e.Start).ThenByDescending(e => e.End))
            {
                if (edit.Start < 0 || edit.End > source.Length || edit.Start > edit.End)
                    continue; // not of this source
                if (applied.Count > 0 && edit.End > applied[applied.Count - 1].Start)
                    continue;
                source = source.Substring(0, edit.Start) + edit.Text + source.Substring(edit.End);
                applied.Add(edit);
            }
            if (partStart >= 0)
            {
                foreach (var (s, e, t) in applied)
                {
                    partStart = Shift(partStart, s, e, t);
                    partEnd = Shift(partEnd, s, e, t);
                }
            }
            return (source, frame, notes, partStart, partEnd);
        }

        // A place in the source after the edit [start, end) -> text
        private static int Shift(int at, int start, int end, string text) =>
            at >= end ? at + text.Length - (end - start) : at > start ? start + Math.Min(at - start, text.Length) : at;

        private static string LinesName(int first, int last) => first == last ? "line " + (first + 1) : "lines " + (first + 1) + "-" + (last + 1);

        private static List<int> LineStarts(string source)
        {
            var starts = new List<int> { 0 };
            for (var i = 0; i < source.Length; i++)
            {
                if (source[i] == '\n')
                    starts.Add(i + 1);
            }
            return starts;
        }

        private static int LineOf(int offset, List<int> starts)
        {
            var line = starts.BinarySearch(offset);
            return line >= 0 ? line : ~line - 1;
        }

        private static bool LineIsBlank(string source, int start, List<int> starts)
        {
            var line = LineOf(start, starts);
            var end = line + 1 < starts.Count ? starts[line + 1] : source.Length;
            return source.Substring(starts[line], end - starts[line]).Trim().Length == 0;
        }

        // A part of a fenced code block: its line breaks are the source's
        private static bool IsCodeBlock(string source, Part part, List<int> starts)
        {
            if (part.First < 0 || part.First >= starts.Count)
                return false;
            var end = part.First + 1 < starts.Count ? starts[part.First + 1] : source.Length;
            var line = source.Substring(starts[part.First], end - starts[part.First]).TrimStart();
            return line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal);
        }

        private static List<(int Block, int First, int Last)> BlocksOf(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            var result = new List<(int, int, int)>();
            foreach (var entry in text.Split(','))
            {
                var fields = entry.Split('/');
                if (fields.Length == 3 && int.TryParse(fields[0], out var block) && int.TryParse(fields[1], out var first) && int.TryParse(fields[2], out var last))
                    result.Add((block, first, last));
                else
                    return null;
            }
            return result;
        }

        // --- diffs ----------------------------------------------------------------------------------

        // For each character of a: its place in b (-1: none) -- matched by a longest common subsequence
        private static int[] Align(string a, string b)
        {
            var result = Enumerable.Repeat(-1, a.Length).ToArray();
            if (a == b)
            {
                for (var i = 0; i < a.Length; i++)
                    result[i] = i;
                return result;
            }
            foreach (var (i, j) in Matches(a, b))
                result[i] = j;
            return result;
        }

        // The changes from a to b: [a0, a1) of a replaced by text
        private static List<(int A0, int A1, string Text)> Diff(string a, string b)
        {
            var hunks = new List<(int, int, string)>();
            var i = 0;
            var j = 0;
            foreach (var (mi, mj) in Matches(a, b).Concat(new[] { (a.Length, b.Length) }))
            {
                if (mi > i || mj > j)
                    hunks.Add((i, mi, b.Substring(j, mj - j)));
                i = mi + 1;
                j = mj + 1;
            }
            return hunks;
        }

        // The matched characters of a longest common subsequence of a and b (the common start and
        // end first; a middle too large: none matched in it)
        private static List<(int I, int J)> Matches(string a, string b)
        {
            var matches = new List<(int, int)>();
            var prefix = 0;
            while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
            {
                matches.Add((prefix, prefix));
                prefix++;
            }
            var suffix = 0;
            while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix])
                suffix++;
            int n = a.Length - prefix - suffix, m = b.Length - prefix - suffix;
            if (n > 0 && m > 0 && (long)n * m <= 4_000_000)
            {
                var lengths = new int[n + 1, m + 1];
                for (var x = n - 1; x >= 0; x--)
                {
                    for (var y = m - 1; y >= 0; y--)
                        lengths[x, y] = a[prefix + x] == b[prefix + y] ? lengths[x + 1, y + 1] + 1 : Math.Max(lengths[x + 1, y], lengths[x, y + 1]);
                }
                int p = 0, q = 0;
                while (p < n && q < m)
                {
                    if (a[prefix + p] == b[prefix + q])
                    {
                        matches.Add((prefix + p, prefix + q));
                        p++;
                        q++;
                    }
                    else if (lengths[p + 1, q] >= lengths[p, q + 1])
                    {
                        p++;
                    }
                    else
                    {
                        q++;
                    }
                }
            }
            for (var k = suffix; k > 0; k--)
                matches.Add((a.Length - k, b.Length - k));
            return matches;
        }

        // --- updating in place ----------------------------------------------------------------------

        /// <summary>The Markdown text's parts made <paramref name="markdown"/> in
        /// <paramref name="frame"/>: the same objects, their geometry replaced in place (their
        /// attributes kept); new ones only for parts the text didn't have, left-over ones deleted.
        /// Their ids.</summary>
        public static List<Guid> Update(RhinoDoc doc, Text text, string markdown, Frame frame)
        {
            MarkdownStyleFiles.KeepCurrent(doc);
            var layout = Layout(doc, markdown, frame);
            var fields = FieldsOf(doc, markdown);
            var placed = layout.Placed(Transform.PlaneToPlane(Plane.WorldXY, frame.Plane)).ToList();
            // A copy's id is the original's too: the edited one gets its own, so they part ways
            var id = text.Id != Guid.Empty && !text.Shared ? text.Id : Guid.NewGuid();
            var frameText = frame.ToString();
            var old = text.Parts.Select(Part.Of).ToList();

            // Each piece to an old part of its kind: the one at the same place first, then the rest in order
            var target = new RhinoObject[placed.Count];
            var used = new HashSet<Guid>();
            for (var i = 0; i < placed.Count; i++)
            {
                var kind = RhinoTextLayout.KindOf(placed[i].Geometry);
                var same = old.FirstOrDefault(p => p.Index == i && p.Kind == kind && !used.Contains(p.Object.Id));
                if (same != null)
                {
                    target[i] = same.Object;
                    used.Add(same.Object.Id);
                }
            }
            for (var i = 0; i < placed.Count; i++)
            {
                if (target[i] != null)
                    continue;
                var kind = RhinoTextLayout.KindOf(placed[i].Geometry);
                var next = old.Where(p => p.Kind == kind && !used.Contains(p.Object.Id)).OrderBy(p => p.Index < 0 ? int.MaxValue : p.Index).FirstOrDefault();
                if (next != null)
                {
                    target[i] = next.Object;
                    used.Add(next.Object.Id);
                }
            }

            var template = (text.Picked ?? text.Parts.FirstOrDefault())?.Attributes ?? doc.CreateDefaultAttributes();
            var ids = new List<Guid>();
            for (var i = 0; i < placed.Count; i++)
            {
                var geometry = placed[i].Geometry;
                if (target[i] != null)
                {
                    var attributes = AttributesFor(target[i].Attributes, layout, placed[i], i, id, markdown, frameText, fields);
                    if (doc.Objects.Replace(target[i].Id, geometry, true))
                    {
                        doc.Objects.ModifyAttributes(target[i].Id, attributes, true);
                        ids.Add(target[i].Id);
                        continue;
                    }
                    used.Remove(target[i].Id); // couldn't: a new one, the old one deleted below
                }
                var added = doc.Objects.Add(geometry, AttributesFor(template, layout, placed[i], i, id, markdown, frameText, fields));
                if (added != Guid.Empty)
                    ids.Add(added);
            }
            foreach (var part in old.Where(p => !used.Contains(p.Object.Id)))
                DeleteOwn(doc, part.Object); // hidden and locked ones too: they are this text's
            return ids;
        }

        // --- the document's style changed -----------------------------------------------------------

        /// <summary>Every Markdown text of <paramref name="doc"/> drawn again in its style (after it
        /// changed): what was changed on their parts in Rhino taken in first, each updated in
        /// place (<paramref name="except"/>: one left out -- the one being edited). How many.</summary>
        public static int Restyle(RhinoDoc doc, Guid except = default)
        {
            var done = new HashSet<Guid>();
            var count = 0;
            foreach (var o in AllObjects(doc).Where(IsPart).ToList())
            {
                if (done.Contains(o.Id) || o.IsDeleted)
                    continue;
                var text = Find(doc, o);
                if (text == null)
                    continue;
                foreach (var part in text.Parts)
                    done.Add(part.Id);
                if (except != Guid.Empty && text.Parts.Any(p => p.Id == except))
                    continue;
                text.Picked = null;
                var (source, frame, notes, _, _) = Reconcile(doc, text);
                foreach (var note in notes)
                    RhinoApp.WriteLine("MarkdownText: " + note);
                foreach (var id in Update(doc, text, source, frame))
                    done.Add(id);
                count++;
            }
            doc.Views.Redraw();
            return count;
        }

        /// <summary>Opening a file: its copy of a default replaced by the current default, and its
        /// Markdown texts drawn with it (after the opening).</summary>
        public static void InstallStyleDefault()
        {
            RhinoDoc.EndOpenDocument += (s, e) =>
            {
                try
                {
                    var doc = e.Document;
                    var kept = MarkdownStyleFiles.Kept(doc);
                    if (e.Merge || kept == null || !MarkdownStyle.IsPristineDefault(kept))
                        return;
                    var current = MarkdownStyle.DefaultCss(MarkdownStyle.Target.Rhino);
                    if (kept == current || current.Length == 0)
                        return;
                    EventHandler later = null;
                    later = (s2, e2) =>
                    {
                        RhinoApp.Idle -= later;
                        try
                        {
                            MarkdownStyleFiles.Keep(doc, current, "default");
                            var count = Restyle(doc);
                            RhinoApp.WriteLine("miniVivisection: this file's Markdown style was a copy of miniVivisection's default -- now this version's default ({0} Markdown text{1} drawn again).", count, count == 1 ? "" : "s");
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(LogArea.General, "Markdown style: the default for {0}: {1}", doc.Name, Log.Describe(ex));
                        }
                    };
                    RhinoApp.Idle += later;
                }
                catch (Exception ex)
                {
                    Log.Warning(LogArea.General, "Markdown style: {0}", Log.Describe(ex));
                }
            };
        }

        // --- fields ({{protected}}, {{modified}}) -------------------------------------------------------

        private static (uint Doc, bool Has)? s_hasFields;

        /// <summary>Whether <paramref name="doc"/> has a Markdown text with fields (known until the
        /// document changes: ForgetFields).</summary>
        public static bool HasFields(RhinoDoc doc)
        {
            var known = s_hasFields;
            if (known != null && known.Value.Doc == doc.RuntimeSerialNumber)
                return known.Value.Has;
            var has = AllObjects(doc).Any(o => MarkdownFields.Has(o.Attributes.GetUserString(SourceKey)));
            s_hasFields = (doc.RuntimeSerialNumber, has);
            return has;
        }

        public static void ForgetFields() => s_hasFields = null;

        /// <summary>An object's attributes changed from <paramref name="before"/> to <paramref name="now"/>
        /// (null: none -- added, deleted): a text with fields added makes it known; one gone, looked for again.</summary>
        public static void FieldsChanged(ObjectAttributes before, ObjectAttributes now)
        {
            var known = s_hasFields;
            if (MarkdownFields.Has(now?.GetUserString(SourceKey)))
            {
                if (known != null)
                    s_hasFields = (known.Value.Doc, true);
            }
            else if (MarkdownFields.Has(before?.GetUserString(SourceKey)))
                s_hasFields = null;
        }

        /// <summary>The Markdown texts with fields whose parts don't show the state now laid out again
        /// (what was changed on them in Rhino taken in first); <paramref name="keepModified"/>: the
        /// document's modified flag as it was -- the fields' words aren't a change of the user's. How many.</summary>
        public static int RefreshFields(RhinoDoc doc, bool keepModified)
        {
            if (doc == null)
                return 0;
            var signature = RhinoProtect.FieldState(doc).Signature;
            var was = doc.Modified;
            var done = new HashSet<Guid>();
            var count = 0;
            foreach (var o in AllObjects(doc).Where(IsPart).ToList())
            {
                if (done.Contains(o.Id) || o.IsDeleted || !MarkdownFields.Has(o.Attributes.GetUserString(SourceKey)))
                    continue;
                var text = Find(doc, o);
                if (text == null)
                    continue;
                foreach (var part in text.Parts)
                    done.Add(part.Id);
                if (text.Parts.All(p => p.Attributes.GetUserString(FieldsKey) == signature))
                    continue;
                text.Picked = null;
                var (source, frame, _, _, _) = Reconcile(doc, text);
                foreach (var id in Update(doc, text, source, frame))
                    done.Add(id);
                count++;
            }
            if (keepModified)
                doc.Modified = was;
            return count;
        }

        // --- a task's box clicked ----------------------------------------------------------------

        /// <summary>A task box's place in the source and whether it's checked (its curves keep
        /// them); null: <paramref name="o"/> is no task box.</summary>
        public static (int Line, int Column, bool Done)? TaskOf(RhinoObject o)
        {
            var fields = (o?.Attributes.GetUserString(RhinoTextLayout.TaskKey) ?? "").Split(';');
            return fields.Length == 3 && int.TryParse(fields[0], out var line) && int.TryParse(fields[1], out var column)
                ? (line, column, fields[2] == "1") : ((int, int, bool)?)null;
        }

        /// <summary>The task of the box <paramref name="box"/> (one of its curves) checked or
        /// unchecked: its Markdown changed ("- [ ]" and "- [x]") and the text updated in place,
        /// one undo step. What was changed on the parts in Rhino is taken in first (Reconcile),
        /// the task then found again by its order. What went wrong, or null.</summary>
        public static string ToggleTask(RhinoDoc doc, RhinoObject box)
        {
            var task = TaskOf(box);
            var text = task == null ? null : Find(doc, box);
            if (text == null)
                return "That is no task box of a Markdown text.";
            var (source, frame, notes, _, _) = Reconcile(doc, text);
            var (line, column) = (task.Value.Line, task.Value.Column);
            if (source != text.Source)
            {
                // Changed in Rhino: the same task (the n-th) in the source as it is now
                var before = Parse(text.Source).Where(b => b.TaskLine >= 0).ToList();
                var after = Parse(source).Where(b => b.TaskLine >= 0).ToList();
                var n = before.FindIndex(b => b.TaskLine == line && b.TaskColumn == column);
                if (n < 0 || n >= after.Count || before.Count != after.Count)
                    return "The text was changed in Rhino: double-click it to check the task in its Markdown.";
                (line, column) = (after[n].TaskLine, after[n].TaskColumn);
            }
            var toggled = MarkdownBlocks.ToggleTask(source, line, column);
            if (toggled == null)
                return "No task box at line " + (line + 1) + " of the text's Markdown.";
            var undo = doc.BeginUndoRecord(task.Value.Done ? "Uncheck task" : "Check task");
            try
            {
                Update(doc, text, toggled, frame);
            }
            finally
            {
                doc.EndUndoRecord(undo);
            }
            foreach (var note in notes)
                RhinoApp.WriteLine("miniVivisection: " + note);
            doc.Views.Redraw();
            return null;
        }

        /// <summary>Deletes one of the text's own objects whatever its state: a locked or hidden
        /// one is unlocked and shown first when Rhino won't delete it as it is.</summary>
        internal static void DeleteOwn(RhinoDoc doc, RhinoObject o)
        {
            if (o == null || o.IsDeleted || doc.Objects.Delete(o, true, true))
                return;
            doc.Objects.Unlock(o.Id, true);
            doc.Objects.Show(o.Id, true);
            doc.Objects.Delete(o.Id, true);
        }
    }
}
