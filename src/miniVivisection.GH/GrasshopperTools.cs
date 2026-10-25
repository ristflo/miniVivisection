using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace MiniVivisection.GH
{
    /// <summary>
    /// The Grasshopper tools V°Claude in Grasshopper gives Claude (the .rhp's GrasshopperChat
    /// serves them as MCP server "grasshopper", by reflection: <see cref="Tools"/> and
    /// <see cref="Call"/>, on Rhino's UI thread). Only what RhinoAI's g1_* tools don't do:
    /// they read the canvas graph and place, wire, delete and solve; these read the selection,
    /// a parameter's data in full (with the geometry's facts), a script's code, and set
    /// values, scripts, nicknames, enabled/preview, groups, wires off, Markdown notes, and
    /// capture the canvas. They record no undo steps of their own: the chat makes one per
    /// answer (<see cref="ClaudeUndo"/>).
    /// </summary>
    public static class GrasshopperTools
    {
        private const string NoArguments = "{\"type\":\"object\",\"properties\":{}}";

        private sealed class Tool
        {
            public string Name, Description, Schema;
            public bool Changes;
            public Func<JsonElement, string> Run;
        }

        private static readonly List<Tool> s_tools = new List<Tool>
        {
            new Tool
            {
                Name = "gh_get_selection",
                Description = "The objects selected on the Grasshopper canvas (what the user means by 'this', 'these'): id, name, nickname, enabled, preview, " +
                              "position, group, inputs and outputs (access, sources, item counts), messages; a slider's range, a Panel's text, a toggle's value. " +
                              "Nothing selected: the document's name and object count.",
                Schema = NoArguments,
                Run = _ => Selection(),
            },
            new Tool
            {
                Name = "gh_get_data",
                Description = "One parameter's data in full: its tree's paths and, per item, the type and its facts -- numbers, text, point coordinates, " +
                              "a curve's length, closed and planar, a surface's or Brep's area, volume, closed (solid), faces, the bounding box, valid. " +
                              "For a component pass its id and the parameter (index, name or nickname; outputs first, side 'input' for inputs); " +
                              "for a floating parameter (slider, Panel, Curve param) its id only. Up to max_items items (default: the setting).",
                Schema = "{\"type\":\"object\",\"properties\":{" +
                         "\"id\":{\"type\":\"string\",\"description\":\"The object's instance id (or a unique nickname).\"}," +
                         "\"param\":{\"type\":\"string\",\"description\":\"Parameter: index, name or nickname. Omit for a floating parameter.\"}," +
                         "\"side\":{\"type\":\"string\",\"enum\":[\"output\",\"input\"],\"description\":\"Default output.\"}," +
                         "\"max_items\":{\"type\":\"integer\"}},\"required\":[\"id\"]}",
                Run = Data,
            },
            new Tool
            {
                Name = "gh_get_script",
                Description = "A script component's code (with line numbers), its language, and its inputs and outputs (name, access).",
                Schema = "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}},\"required\":[\"id\"]}",
                Run = GetScript,
            },
            new Tool
            {
                Name = "gh_set_value",
                Description = "Sets a value on the canvas: a Number Slider (value, min, max, decimals), a Panel's text (and show_as Markdown/Text/Python/CSharp/Auto), " +
                              "a Boolean Toggle (value true/false), a Value List (value: an item's name or index), " +
                              "a parameter's stored values (values: numbers, text, booleans, points as \"x,y,z\") -- a floating one, or a component's input (id + param), " +
                              "or Rhino objects referenced by a geometry parameter (rhino_objects: object ids, or [\"selected\"] for what's selected in Rhino). " +
                              "The definition recomputes.",
                Schema = "{\"type\":\"object\",\"properties\":{" +
                         "\"id\":{\"type\":\"string\"}," +
                         "\"param\":{\"type\":\"string\",\"description\":\"A component's input (index, name or nickname).\"}," +
                         "\"value\":{\"description\":\"Slider value, Panel text, toggle true/false, Value List item.\"}," +
                         "\"min\":{\"type\":\"number\"},\"max\":{\"type\":\"number\"},\"decimals\":{\"type\":\"integer\"}," +
                         "\"show_as\":{\"type\":\"string\",\"enum\":[\"Auto\",\"Markdown\",\"Text\",\"Python\",\"CSharp\"]}," +
                         "\"values\":{\"type\":\"array\",\"description\":\"Stored values for a parameter.\"}," +
                         "\"rhino_objects\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}},\"required\":[\"id\"]}",
                Changes = true,
                Run = SetValue,
            },
            new Tool
            {
                Name = "gh_set_script",
                Description = "Writes a script component's code (Python 3, C#, IronPython 2: its own language) and, optionally, its inputs and outputs: " +
                              "inputs [{name, access: item|list|tree}], outputs [name]; given lists replace the component's parameters (the 'out' output stays). " +
                              "Place the component first (g1_place_component 'Python 3 Script'). The definition recomputes; the answer has the component's messages.",
                Schema = "{\"type\":\"object\",\"properties\":{" +
                         "\"id\":{\"type\":\"string\"},\"code\":{\"type\":\"string\"}," +
                         "\"inputs\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"},\"access\":{\"type\":\"string\",\"enum\":[\"item\",\"list\",\"tree\"]}},\"required\":[\"name\"]}}," +
                         "\"outputs\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}},\"required\":[\"id\",\"code\"]}",
                Changes = true,
                Run = SetScript,
            },
            new Tool
            {
                Name = "gh_edit_objects",
                Description = "Changes objects on the canvas: nickname, enabled (false: disabled, Grasshopper's Disable), preview, move_by [dx, dy] " +
                              "or move_to [x, y] (the first object; the others keep their offsets), group (a name: a new group of these objects).",
                Schema = "{\"type\":\"object\",\"properties\":{" +
                         "\"ids\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}," +
                         "\"nickname\":{\"type\":\"string\"},\"enabled\":{\"type\":\"boolean\"},\"preview\":{\"type\":\"boolean\"}," +
                         "\"move_by\":{\"type\":\"array\",\"items\":{\"type\":\"number\"}},\"move_to\":{\"type\":\"array\",\"items\":{\"type\":\"number\"}}," +
                         "\"group\":{\"type\":\"string\"}},\"required\":[\"ids\"]}",
                Changes = true,
                Run = EditObjects,
            },
            new Tool
            {
                Name = "gh_disconnect",
                Description = "Removes wires into a parameter: a component's input (id + param) or a floating parameter (id). " +
                              "sources: [{id, param}] the wires to remove; omitted: all of them.",
                Schema = "{\"type\":\"object\",\"properties\":{" +
                         "\"id\":{\"type\":\"string\"},\"param\":{\"type\":\"string\"}," +
                         "\"sources\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"},\"param\":{\"type\":\"string\"}},\"required\":[\"id\"]}}}," +
                         "\"required\":[\"id\"]}",
                Changes = true,
                Run = Disconnect,
            },
            new Tool
            {
                Name = "gh_add_note",
                Description = "Adds a Panel with a note in Markdown (shown rendered: headings, lists, bold, code, links such as [Loft](gh:Loft) to a component), " +
                              "to the right of the given objects, or at x, y. For explanations next to what you built.",
                Schema = "{\"type\":\"object\",\"properties\":{" +
                         "\"text\":{\"type\":\"string\"},\"near\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}," +
                         "\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"width\":{\"type\":\"number\"},\"height\":{\"type\":\"number\"}," +
                         "\"markdown\":{\"type\":\"boolean\",\"description\":\"Default true.\"}},\"required\":[\"text\"]}",
                Changes = true,
                Run = AddNote,
            },
            new Tool
            {
                Name = "gh_capture_canvas",
                Description = "An image of the Grasshopper canvas -- of the given objects, or of the whole definition -- saved as a PNG file; " +
                              "Read the file to see it. (The Rhino viewport with the preview: RhinoAI's get_viewport_image.)",
                Schema = "{\"type\":\"object\",\"properties\":{\"ids\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"width\":{\"type\":\"integer\",\"description\":\"Pixels, default 1400.\"}}}",
                Run = Capture,
            },
        };

        /// <summary>For the .rhp: each tool as { name, description, input schema (JSON), changes (bool) }.</summary>
        public static object[][] Tools() => s_tools.Select(t => new object[] { t.Name, t.Description, t.Schema, t.Changes }).ToArray();

        /// <summary>Runs a tool (UI thread): its answer for Claude, or what went wrong.</summary>
        public static string Call(string name, string argumentsJson)
        {
            var tool = s_tools.FirstOrDefault(t => t.Name == name);
            if (tool == null)
                return "Unknown tool " + name + ".";
            try
            {
                using (var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson))
                    return tool.Run(document.RootElement);
            }
            catch (ToolError error)
            {
                return error.Message;
            }
            catch (Exception ex)
            {
                return name + " failed: " + (ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException.Message : ex.Message);
            }
        }

        /// <summary>A message's context line: the definition, the solver, and (<paramref name="selection"/>)
        /// the selected objects; null without a definition.</summary>
        public static string Context(bool selection)
        {
            var doc = Instances.ActiveCanvas?.Document;
            if (doc == null)
                return null;
            var text = string.Format("[Grasshopper: {0}, {1} objects, solver {2}", DocName(doc), doc.ObjectCount, GH_Document.EnableSolutions && doc.Enabled ? "on" : "off");
            if (selection)
            {
                var selected = doc.SelectedObjects();
                text += selected.Count == 0
                    ? "; nothing selected"
                    : "; selected: " + string.Join(", ", selected.Take(12).Select(o => Label(o) + " [" + o.InstanceGuid + "]")) + (selected.Count > 12 ? ", and " + (selected.Count - 12) + " more" : "");
            }
            return text + "]";
        }

        /// <summary>The open definition's file; null when unsaved or none.</summary>
        public static string DocumentPath() => Instances.ActiveCanvas?.Document?.FilePath is string path && path.Length > 0 ? path : null;

        /// <summary>Which definition a conversation is about: its file, or -- unsaved -- the document's
        /// own id (a new unsaved definition doesn't continue another one's conversation); null without one.</summary>
        public static string DocumentKey()
        {
            var doc = Instances.ActiveCanvas?.Document;
            if (doc == null)
                return null;
            return DocumentPath() ?? "unsaved " + doc.DocumentID;
        }

        /// <summary>The setting "Items Claude reads per parameter" (the .rhp's, by reflection).</summary>
        public static int MaxItems = 50;

        /// <summary>Where gh_capture_canvas saves its images (the .rhp sets it: a folder Claude may Read).</summary>
        public static string CaptureFolder = Path.Combine(Path.GetTempPath(), "rhinovivisection", "minivivisection", "captures");

        private sealed class ToolError : Exception
        {
            public ToolError(string message) : base(message)
            {
            }
        }

        // --- finding things --------------------------------------------------------------------

        internal static GH_Document Document()
        {
            var doc = Instances.ActiveCanvas?.Document;
            if (doc == null)
                throw new ToolError("No Grasshopper definition is open (Grasshopper's window shows none).");
            return doc;
        }

        private static string Text(JsonElement args, string name)
        {
            if (!args.TryGetProperty(name, out var value))
                return null;
            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Null ? null : value.GetRawText();
        }

        private static IGH_DocumentObject Find(GH_Document doc, string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ToolError("No id given.");
            id = id.Trim().Trim('{', '}');
            if (Guid.TryParse(id, out var guid))
            {
                var found = doc.FindObject(guid, false);
                if (found != null)
                    return found;
                throw new ToolError("No object " + id + " in the definition (gh_get_selection or g1_get_canvas_graph list the ids).");
            }
            var named = doc.Objects.Where(o => string.Equals(o.NickName, id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (named.Count == 0)
                named = doc.Objects.Where(o => string.Equals(o.Name, id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (named.Count == 1)
                return named[0];
            throw new ToolError(named.Count == 0
                ? "No object named " + id + "."
                : string.Format("{0} objects are named {1}: use an id ({2}).", named.Count, id, string.Join(", ", named.Take(5).Select(o => o.InstanceGuid))));
        }

        private static IGH_Param FindParam(IGH_DocumentObject obj, string param, string side)
        {
            if (obj is IGH_Param floating && string.IsNullOrEmpty(param))
                return floating;
            if (!(obj is IGH_Component component))
            {
                if (obj is IGH_Param alone)
                    return alone;
                throw new ToolError(Label(obj) + " has no parameters.");
            }
            if (string.IsNullOrEmpty(param))
                throw new ToolError(Label(obj) + " is a component: name the parameter (param: index, name or nickname).");
            var lists = side == "input"
                ? new[] { component.Params.Input }
                : side == "output" ? new[] { component.Params.Output } : new[] { component.Params.Output, component.Params.Input };
            foreach (var list in lists)
            {
                if (int.TryParse(param, out var index) && index >= 0 && index < list.Count)
                    return list[index];
                var match = list.FirstOrDefault(p => string.Equals(p.NickName, param, StringComparison.OrdinalIgnoreCase))
                            ?? list.FirstOrDefault(p => string.Equals(p.Name, param, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return match;
            }
            throw new ToolError(string.Format("{0} has no {1} {2}. Inputs: {3}; outputs: {4}.", Label(obj), side ?? "parameter", param,
                string.Join(", ", component.Params.Input.Select(p => p.NickName)), string.Join(", ", component.Params.Output.Select(p => p.NickName))));
        }

        private static string Label(IGH_DocumentObject obj) =>
            obj == null ? "(none)" : obj.NickName != obj.Name && !string.IsNullOrEmpty(obj.NickName) ? obj.Name + " '" + obj.NickName + "'" : obj.Name;

        private static IGH_DocumentObject Owner(IGH_Param param) => param.Attributes?.GetTopLevel?.DocObject ?? param;

        // --- reading -----------------------------------------------------------------------------

        private static string Selection()
        {
            var doc = Document();
            var selected = doc.SelectedObjects();
            var text = new StringBuilder();
            text.AppendFormat("Definition: {0}; {1} objects; solver {2}.\n", DocName(doc), doc.ObjectCount, GH_Document.EnableSolutions && doc.Enabled ? "on" : "off");
            if (selected.Count == 0)
                return text.Append("Nothing is selected on the canvas.").ToString();
            text.AppendFormat("Selected: {0}\n", selected.Count);
            foreach (var obj in selected.Take(40))
                Describe(doc, obj, text);
            if (selected.Count > 40)
                text.AppendFormat("... and {0} more.\n", selected.Count - 40);
            return text.ToString();
        }

        internal static string DocName(GH_Document doc) => string.IsNullOrEmpty(doc.FilePath) ? doc.DisplayName ?? "unsaved" : Path.GetFileName(doc.FilePath);

        /// <summary>One object for Claude: what it is and how it's wired.</summary>
        internal static void Describe(GH_Document doc, IGH_DocumentObject obj, StringBuilder text)
        {
            var kind = obj is IGH_Component ? "component" : obj is IGH_Param ? "parameter" : obj is GH_Group ? "group" : "object";
            text.AppendFormat("- {0} [{1}] {2}", Label(obj), obj.InstanceGuid, kind);
            if (obj is IGH_ActiveObject active && active.Locked)
                text.Append(", disabled");
            if (obj is IGH_PreviewObject preview && preview.IsPreviewCapable)
                text.Append(preview.Hidden ? ", preview off" : ", preview on");
            var pivot = obj.Attributes?.Pivot ?? PointF.Empty;
            text.AppendFormat(CultureInfo.InvariantCulture, ", at ({0:0}, {1:0})", pivot.X, pivot.Y);
            var groups = doc.Objects.OfType<GH_Group>().Where(g => g.ObjectIDs.Contains(obj.InstanceGuid)).Select(g => "'" + g.NickName + "'").ToList();
            if (groups.Count > 0)
                text.Append(", in group " + string.Join(", ", groups));
            text.Append('\n');
            switch (obj)
            {
                case GH_NumberSlider slider:
                    text.AppendFormat(CultureInfo.InvariantCulture, "  slider: {0} (min {1}, max {2}, {3} decimals)\n", slider.CurrentValue, slider.Slider.Minimum, slider.Slider.Maximum, slider.Slider.DecimalPlaces);
                    break;
                case GH_Panel panel:
                    var panelText = panel.UserText ?? "";
                    text.AppendFormat("  panel ({0}, show as {1}): {2}\n", panel.SourceCount > 0 ? "fed by a wire" : "own text", ScriptPanels.ShowAsOf(panel),
                        Short(panelText.Replace("\r", ""), 500));
                    break;
                case GH_BooleanToggle toggle:
                    text.AppendFormat("  toggle: {0}\n", toggle.Value);
                    break;
                case GH_ValueList list:
                    text.AppendFormat("  value list: {0} (items: {1})\n", string.Join(", ", list.SelectedItems.Select(i => i.Name)), string.Join(", ", list.ListItems.Take(20).Select(i => i.Name)));
                    break;
            }
            if (ScriptLanguage(obj) is string language)
                text.AppendFormat("  script: {0}, {1} lines (gh_get_script)\n", language, (SourceOf(obj) ?? "").Split('\n').Length);
            if (obj is IGH_Component component)
            {
                foreach (var param in component.Params.Input)
                    text.Append("  in  ").Append(Param(param, true)).Append('\n');
                foreach (var param in component.Params.Output)
                    text.Append("  out ").Append(Param(param, false)).Append('\n');
            }
            else if (obj is IGH_Param floating)
                text.Append("  ").Append(Param(floating, true)).Append('\n');
            if (obj is IGH_ActiveObject withMessages)
            {
                foreach (var level in new[] { GH_RuntimeMessageLevel.Error, GH_RuntimeMessageLevel.Warning, GH_RuntimeMessageLevel.Remark })
                {
                    foreach (var message in withMessages.RuntimeMessages(level).Take(5))
                        text.AppendFormat("  {0}: {1}\n", level.ToString().ToLowerInvariant(), Short(message, 300));
                }
            }
        }

        private static string Param(IGH_Param param, bool sources)
        {
            var text = new StringBuilder();
            var index = param.Attributes?.Parent?.DocObject is IGH_Component owner
                ? (param.Kind == GH_ParamKind.input ? owner.Params.Input.IndexOf(param) : owner.Params.Output.IndexOf(param))
                : -1;
            text.AppendFormat("{0}{1} ({2}, {3}, {4})", index >= 0 ? index + " " : "", param.NickName, param.Name, param.Access.ToString().ToLowerInvariant(), param.TypeName);
            if (sources && param.SourceCount > 0)
                text.Append(" <- ").Append(string.Join(", ", param.Sources.Select(s => Label(Owner(s)) + " [" + Owner(s).InstanceGuid + "]" + (Owner(s) == s ? "" : "." + s.NickName))));
            var data = param.VolatileData;
            text.AppendFormat(": {0} item{1}{2}", data.DataCount, data.DataCount == 1 ? "" : "s", data.PathCount > 1 ? " in " + data.PathCount + " branches" : "");
            var first = data.AllData(true).FirstOrDefault();
            if (first != null)
                text.Append(", e.g. ").Append(Short(Fact(first, brief: true), 120));
            return text.ToString();
        }

        private static string Data(JsonElement args)
        {
            var doc = Document();
            var obj = Find(doc, Text(args, "id"));
            var param = FindParam(obj, Text(args, "param"), Text(args, "side"));
            var max = args.TryGetProperty("max_items", out var m) && m.TryGetInt32(out var n) && n > 0 ? n : MaxItems;
            var data = param.VolatileData;
            var text = new StringBuilder();
            text.AppendFormat("{0}, {1} {2}: {3} item(s) in {4} branch(es)\n", Label(Owner(param)), param.Kind == GH_ParamKind.input ? "input" : param.Kind == GH_ParamKind.output ? "output" : "parameter",
                param.NickName, data.DataCount, data.PathCount);
            var shown = 0;
            for (var b = 0; b < data.PathCount && shown < max; b++)
            {
                var branch = data.get_Branch(b);
                text.AppendFormat("{0} ({1})\n", data.get_Path(b), branch.Count);
                for (var i = 0; i < branch.Count && shown < max; i++, shown++)
                    text.AppendFormat("  [{0}] {1}\n", i, branch[i] is IGH_Goo goo ? Fact(goo, brief: false) : "null");
            }
            if (shown < data.DataCount)
                text.AppendFormat("... {0} more item(s) (max_items).\n", data.DataCount - shown);
            return text.ToString();
        }

        /// <summary>An item's type and facts: what Claude needs to judge it without seeing it.</summary>
        internal static string Fact(IGH_Goo goo, bool brief)
        {
            if (goo == null)
                return "null";
            object value;
            try
            {
                value = goo.ScriptVariable();
            }
            catch (Exception)
            {
                value = null;
            }
            string P(Point3d p) => string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###})", p.X, p.Y, p.Z);
            string V(Vector3d v) => string.Format(CultureInfo.InvariantCulture, "<{0:0.###}, {1:0.###}, {2:0.###}>", v.X, v.Y, v.Z);
            string Box(BoundingBox box) => box.IsValid ? "box " + P(box.Min) + " - " + P(box.Max) : "no box";
            string N(double d) => d.ToString("0.####", CultureInfo.InvariantCulture);
            switch (value)
            {
                case null:
                    return goo.TypeName + ": " + Short(goo.ToString(), 200);
                case double d:
                    return "number " + N(d);
                case int i:
                    return "integer " + i;
                case bool b:
                    return "boolean " + b;
                case string s:
                    return "text \"" + Short(s, brief ? 80 : 400) + "\"";
                case Point3d p:
                    return "point " + P(p);
                case Vector3d v:
                    return "vector " + V(v) + ", length " + N(v.Length);
                case Plane plane:
                    return "plane at " + P(plane.Origin) + ", normal " + V(plane.ZAxis);
                case Line line:
                    return "line " + P(line.From) + " - " + P(line.To) + ", length " + N(line.Length);
                case Circle circle:
                    return "circle at " + P(circle.Center) + ", radius " + N(circle.Radius);
                case Interval interval:
                    return "domain " + N(interval.T0) + " to " + N(interval.T1);
                case Curve curve:
                    return string.Format("curve ({0}{1}{2}), length {3}, {4} - {5}{6}", curve.GetType().Name.Replace("Curve", "").ToLowerInvariant(),
                        curve.IsClosed ? ", closed" : ", open", curve.IsPlanar() ? ", planar" : "", N(curve.GetLength()), P(curve.PointAtStart), P(curve.PointAtEnd),
                        brief ? "" : ", " + Box(curve.GetBoundingBox(true)) + (curve.IsValid ? "" : ", INVALID"));
                case Brep brep:
                {
                    var area = brief ? "" : ", area " + N(AreaMassProperties.Compute(brep)?.Area ?? 0);
                    var volume = !brief && brep.IsSolid ? ", volume " + N(Math.Abs(brep.GetVolume())) : "";
                    return string.Format("brep: {0} face(s), {1}{2}{3}{4}{5}", brep.Faces.Count, brep.IsSolid ? "closed (solid)" : "open", area, volume,
                        brief ? "" : ", " + Box(brep.GetBoundingBox(true)), brep.IsValid ? "" : ", INVALID");
                }
                case Extrusion extrusion:
                    return string.Format("extrusion, {0}{1}", extrusion.IsSolid ? "closed" : "open", brief ? "" : ", " + Box(extrusion.GetBoundingBox(true)));
                case Surface surface:
                    return string.Format("surface{0}{1}", brief ? "" : ", area " + N(AreaMassProperties.Compute(surface)?.Area ?? 0),
                        brief ? "" : ", " + Box(surface.GetBoundingBox(true)));
                case Mesh mesh:
                    return string.Format("mesh: {0} vertices, {1} faces, {2}{3}{4}", mesh.Vertices.Count, mesh.Faces.Count, mesh.IsClosed ? "closed" : "open",
                        !brief && mesh.IsClosed ? ", volume " + N(mesh.Volume()) : "", brief ? "" : ", " + Box(mesh.GetBoundingBox(true)));
                case Box box:
                    return "box " + N(box.X.Length) + " x " + N(box.Y.Length) + " x " + N(box.Z.Length) + " at " + P(box.Center);
                case Rectangle3d rectangle:
                    return "rectangle " + N(rectangle.Width) + " x " + N(rectangle.Height) + " at " + P(rectangle.Center);
                case GeometryBase geometry:
                    return geometry.ObjectType.ToString().ToLowerInvariant() + (brief ? "" : ", " + Box(geometry.GetBoundingBox(true)));
                default:
                    return goo.TypeName + ": " + Short(goo.ToString(), brief ? 80 : 300);
            }
        }

        private static string Short(string text, int max)
        {
            text = text ?? "";
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }

        // --- script components ------------------------------------------------------------------

        private static MethodInfo ScriptMethod(object obj, string name, params Type[] arguments) =>
            obj?.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, arguments, null);

        internal static string SourceOf(object obj)
        {
            var get = ScriptMethod(obj, "TryGetSource", typeof(string).MakeByRefType());
            if (get == null)
                return null;
            var args = new object[] { null };
            return (bool)get.Invoke(obj, args) ? (string)args[0] : null;
        }

        /// <summary>"Python 3", "C#", "IronPython 2" -- null for a component that isn't a script.</summary>
        internal static string ScriptLanguage(object obj)
        {
            if (ScriptMethod(obj, "SetSource", typeof(string)) == null)
                return null;
            var type = obj.GetType().Name;
            return type.StartsWith("Python3", StringComparison.Ordinal) ? "Python 3"
                : type.StartsWith("CSharp", StringComparison.Ordinal) ? "C#"
                : type.StartsWith("IronPython", StringComparison.Ordinal) ? "IronPython 2"
                : type;
        }

        private static string GetScript(JsonElement args)
        {
            var doc = Document();
            var obj = Find(doc, Text(args, "id"));
            var language = ScriptLanguage(obj) ?? throw new ToolError(Label(obj) + " isn't a script component.");
            var code = SourceOf(obj) ?? "";
            var text = new StringBuilder().AppendFormat("{0} [{1}], {2}\n", Label(obj), obj.InstanceGuid, language);
            if (obj is IGH_Component component)
            {
                text.Append("inputs: ").Append(string.Join(", ", component.Params.Input.Select(p => p.NickName + " (" + p.Access.ToString().ToLowerInvariant() + ")"))).Append('\n');
                text.Append("outputs: ").Append(string.Join(", ", component.Params.Output.Select(p => p.NickName))).Append('\n');
            }
            var lines = code.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
                text.Append((i + 1).ToString().PadLeft(4)).Append('\t').Append(lines[i]).Append('\n');
            return text.ToString();
        }

        private static string SetScript(JsonElement args)
        {
            var doc = Document();
            var obj = Find(doc, Text(args, "id"));
            var set = ScriptMethod(obj, "SetSource", typeof(string)) ?? throw new ToolError(Label(obj) + " isn't a script component.");
            var component = (IGH_Component)obj;
            var code = Text(args, "code") ?? "";
            set.Invoke(obj, new object[] { code });
            var changed = false;
            if (args.TryGetProperty("inputs", out var inputs) && inputs.ValueKind == JsonValueKind.Array)
            {
                var wanted = inputs.EnumerateArray().Select(i => (Name: i.TryGetProperty("name", out var n) ? n.GetString() : null,
                    Access: i.TryGetProperty("access", out var a) ? a.GetString() : "item")).Where(i => !string.IsNullOrWhiteSpace(i.Name)).ToList();
                changed |= SyncParams(component, GH_ParameterSide.Input, wanted);
            }
            if (args.TryGetProperty("outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Array)
            {
                var wanted = outputs.EnumerateArray().Select(o => (Name: o.ValueKind == JsonValueKind.String ? o.GetString() : null, Access: "item"))
                    .Where(o => !string.IsNullOrWhiteSpace(o.Name)).ToList();
                changed |= SyncParams(component, GH_ParameterSide.Output, wanted);
            }
            if (changed)
            {
                (component as IGH_VariableParameterComponent)?.VariableParameterMaintenance();
                component.Params.OnParametersChanged();
                ScriptMethod(obj, "SetParametersToScript")?.Invoke(obj, null);
            }
            component.ExpireSolution(true);
            Instances.InvalidateCanvas();
            return "Script written to " + Label(obj) + ". " + AfterSolve(component);
        }

        /// <summary>A script component's inputs or outputs as wanted: renamed in place, added, removed
        /// (the outputs' "out", the printed text, stays). True when anything changed.</summary>
        private static bool SyncParams(IGH_Component component, GH_ParameterSide side, List<(string Name, string Access)> wanted)
        {
            var variable = component as IGH_VariableParameterComponent ?? throw new ToolError(Label(component) + " can't change its parameters.");
            var list = side == GH_ParameterSide.Input ? component.Params.Input : component.Params.Output;
            var keep = side == GH_ParameterSide.Output ? list.Where(p => p.NickName == "out").ToList() : new List<IGH_Param>();
            var changed = false;
            var mine = list.Except(keep).ToList();
            for (var i = 0; i < wanted.Count; i++)
            {
                IGH_Param param;
                if (i < mine.Count)
                    param = mine[i];
                else
                {
                    var at = keep.Count > 0 && side == GH_ParameterSide.Output ? list.Count : list.Count;
                    if (!variable.CanInsertParameter(side, at))
                        throw new ToolError(Label(component) + " can't take another " + (side == GH_ParameterSide.Input ? "input" : "output") + ".");
                    param = variable.CreateParameter(side, at);
                    if (side == GH_ParameterSide.Input)
                        component.Params.RegisterInputParam(param, at);
                    else
                        component.Params.RegisterOutputParam(param, at);
                    changed = true;
                }
                if (param.NickName != wanted[i].Name || param.Name != wanted[i].Name)
                {
                    param.NickName = wanted[i].Name;
                    param.Name = wanted[i].Name;
                    changed = true;
                }
                var access = wanted[i].Access == "list" ? GH_ParamAccess.list : wanted[i].Access == "tree" ? GH_ParamAccess.tree : GH_ParamAccess.item;
                if (side == GH_ParameterSide.Input && param.Access != access)
                {
                    param.Access = access;
                    changed = true;
                }
            }
            foreach (var extra in mine.Skip(wanted.Count).ToList())
            {
                if (side == GH_ParameterSide.Input)
                    component.Params.UnregisterInputParameter(extra, true);
                else
                    component.Params.UnregisterOutputParameter(extra, true);
                changed = true;
            }
            return changed;
        }

        /// <summary>After a change that recomputes: the component's messages, or that it ran clean.</summary>
        private static string AfterSolve(IGH_DocumentObject obj)
        {
            if (!(obj is IGH_ActiveObject active))
                return "";
            var messages = new[] { GH_RuntimeMessageLevel.Error, GH_RuntimeMessageLevel.Warning }
                .SelectMany(level => active.RuntimeMessages(level).Select(m => level.ToString().ToLowerInvariant() + ": " + m)).Take(8).ToList();
            if (GrasshopperHeld())
                return "The solution waits (a script is paused, or the solver is off): it runs later.";
            return messages.Count == 0 ? "It ran without errors or warnings." : "Its messages: " + string.Join(" | ", messages.Select(m => Short(m, 300)));
        }

        private static bool GrasshopperHeld()
        {
            var doc = Instances.ActiveCanvas?.Document;
            return doc == null || !doc.Enabled || !GH_Document.EnableSolutions;
        }

        // --- setting values ----------------------------------------------------------------------

        private static string SetValue(JsonElement args)
        {
            var doc = Document();
            var obj = Find(doc, Text(args, "id"));
            var param = Text(args, "param");
            args.TryGetProperty("value", out var value);
            var hasValue = value.ValueKind != JsonValueKind.Undefined && value.ValueKind != JsonValueKind.Null;
            string ValueText() => value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
            switch (obj)
            {
                case GH_NumberSlider slider when param == null:
                {
                    if (args.TryGetProperty("decimals", out var decimals) && decimals.TryGetInt32(out var places))
                        slider.Slider.DecimalPlaces = Math.Max(0, Math.Min(12, places));
                    if (args.TryGetProperty("min", out var min) && min.TryGetDecimal(out var low))
                        slider.Slider.Minimum = low;
                    if (args.TryGetProperty("max", out var max) && max.TryGetDecimal(out var high))
                        slider.Slider.Maximum = high;
                    if (slider.Slider.Minimum > slider.Slider.Maximum)
                        throw new ToolError("The slider's min is above its max.");
                    if (hasValue)
                    {
                        if (!decimal.TryParse(ValueText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                            throw new ToolError("A slider's value is a number.");
                        slider.SetSliderValue(Math.Max(slider.Slider.Minimum, Math.Min(slider.Slider.Maximum, number)));
                    }
                    slider.ExpireSolution(true);
                    return string.Format(CultureInfo.InvariantCulture, "{0}: {1} (min {2}, max {3}).", Label(slider), slider.CurrentValue, slider.Slider.Minimum, slider.Slider.Maximum);
                }
                case GH_Panel panel when param == null:
                {
                    if (hasValue)
                    {
                        if (panel.SourceCount > 0)
                            throw new ToolError(Label(panel) + " shows what its wire brings: disconnect it first (gh_disconnect) to give it its own text.");
                        panel.UserText = ValueText();
                    }
                    if (Text(args, "show_as") is string showAs && Enum.TryParse(showAs, true, out ScriptPanels.ShowAs mode))
                        ScriptPanels.ChangeShowAs(panel, mode, recordUndo: false);
                    panel.Attributes?.ExpireLayout();
                    panel.ExpireSolution(true);
                    return Label(panel) + ": text set (" + (panel.UserText ?? "").Length + " characters), show as " + ScriptPanels.ShowAsOf(panel) + ".";
                }
                case GH_BooleanToggle toggle when param == null:
                {
                    if (!hasValue || !bool.TryParse(ValueText(), out var flag))
                        throw new ToolError("A toggle's value is true or false.");
                    toggle.Value = flag;
                    toggle.ExpireSolution(true);
                    return Label(toggle) + ": " + flag + ".";
                }
                case GH_ValueList list when param == null:
                {
                    var wanted = hasValue ? ValueText() : throw new ToolError("Give the item: its name or index.");
                    var index = list.ListItems.FindIndex(i => string.Equals(i.Name, wanted, StringComparison.OrdinalIgnoreCase) || string.Equals(i.Expression, wanted, StringComparison.OrdinalIgnoreCase));
                    if (index < 0 && int.TryParse(wanted, out var number) && number >= 0 && number < list.ListItems.Count)
                        index = number;
                    if (index < 0)
                        throw new ToolError(Label(list) + " has no item " + wanted + ". Items: " + string.Join(", ", list.ListItems.Select(i => i.Name)));
                    list.SelectItem(index);
                    list.ExpireSolution(true);
                    return Label(list) + ": " + list.ListItems[index].Name + ".";
                }
            }
            var target = FindParam(obj, param, "input");
            if (args.TryGetProperty("rhino_objects", out var rhino) && rhino.ValueKind == JsonValueKind.Array)
                return SetReferences(target, rhino);
            if (!args.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
            {
                if (!hasValue)
                    throw new ToolError("Nothing to set: give value, values or rhino_objects.");
                values = JsonDocument.Parse("[" + (value.ValueKind == JsonValueKind.String ? JsonSerializer.Serialize(value.GetString()) : value.GetRawText()) + "]").RootElement;
            }
            var items = values.EnumerateArray().Select(Plain).ToArray();
            SetPersistent(target, items);
            return Label(Owner(target)) + "." + target.NickName + ": " + items.Length + " stored value(s)" +
                   (target.SourceCount > 0 ? " -- it has wires, which win: the stored values are used only without them (gh_disconnect)." : ".") + " " + AfterSolve(Owner(target));
        }

        private static object Plain(JsonElement item)
        {
            switch (item.ValueKind)
            {
                case JsonValueKind.Number:
                    return item.TryGetInt32(out var i) && !item.GetRawText().Contains(".") ? (object)i : item.GetDouble();
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return item.GetBoolean();
                case JsonValueKind.Array:
                    var numbers = item.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number).Select(e => e.GetDouble()).ToArray();
                    return numbers.Length == 3 ? new Point3d(numbers[0], numbers[1], numbers[2]) : (object)item.GetRawText();
                case JsonValueKind.String:
                    var text = item.GetString() ?? "";
                    var parts = text.Trim('{', '}', '(', ')', ' ').Split(',');
                    if (parts.Length == 3 && parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                    {
                        var xyz = parts.Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
                        return new Point3d(xyz[0], xyz[1], xyz[2]);
                    }
                    return text;
                default:
                    return null;
            }
        }

        /// <summary>A parameter's stored ("internalised") values, cast to its type, without an undo step.</summary>
        private static void SetPersistent(IGH_Param param, object[] items)
        {
            var type = param.GetType();
            while (type != null && !(type.IsGenericType && type.GetGenericTypeDefinition() == typeof(GH_PersistentParam<>)))
                type = type.BaseType;
            if (type == null)
                throw new ToolError(Label(Owner(param)) + "." + param.NickName + " can't store values.");
            var cast = type.GetMethod("Cast_Object", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            var persistent = (IGH_Structure)type.GetProperty("PersistentData").GetValue(param);
            var clear = persistent.GetType().GetMethod("Clear", Type.EmptyTypes);
            var append = persistent.GetType().GetMethods().First(m => m.Name == "Append" && m.GetParameters().Length == 1);
            var failed = new List<string>();
            var goos = new List<object>();
            foreach (var item in items)
            {
                var goo = item == null ? null : cast?.Invoke(param, new[] { item });
                if (item != null && goo == null)
                    failed.Add(Convert.ToString(item, CultureInfo.InvariantCulture));
                goos.Add(goo);
            }
            if (failed.Count > 0)
                throw new ToolError(string.Format("{0} ({1}) can't take {2}.", param.NickName, param.TypeName, string.Join(", ", failed.Take(5))));
            clear.Invoke(persistent, null);
            foreach (var goo in goos)
                append.Invoke(persistent, new[] { goo });
            param.OnObjectChanged(GH_ObjectEventType.PersistentData);
            param.ExpireSolution(true);
        }

        private static string SetReferences(IGH_Param param, JsonElement ids)
        {
            var rhinoDoc = RhinoDoc.ActiveDoc ?? throw new ToolError("No Rhino document.");
            var wanted = ids.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            var objects = wanted.Count == 1 && wanted[0] == "selected"
                ? rhinoDoc.Objects.GetSelectedObjects(false, false).ToList()
                : wanted.Select(s => Guid.TryParse(s, out var g) ? rhinoDoc.Objects.FindId(g) : null).ToList();
            if (objects.Count == 0 || objects.Any(o => o == null))
                throw new ToolError(objects.Count == 0 ? "Nothing is selected in Rhino." : "Not every id is a Rhino object (RhinoAI's list_objects or get_selection give ids).");
            var goos = objects.Select(o => (object)GH_Convert.ObjRefToGeometry(new ObjRef(o))).ToArray();
            SetPersistent(param, goos);
            return string.Format("{0}.{1}: {2} Rhino object(s) referenced. {3}", Label(Owner(param)), param.NickName, goos.Length, AfterSolve(Owner(param)));
        }

        // --- editing objects ---------------------------------------------------------------------

        private static List<IGH_DocumentObject> FindAll(GH_Document doc, JsonElement args, string name)
        {
            if (!args.TryGetProperty(name, out var ids) || ids.ValueKind != JsonValueKind.Array)
                return new List<IGH_DocumentObject>();
            return ids.EnumerateArray().Select(e => Find(doc, e.GetString())).Distinct().ToList();
        }

        private static string EditObjects(JsonElement args)
        {
            var doc = Document();
            var objects = FindAll(doc, args, "ids");
            if (objects.Count == 0)
                throw new ToolError("No ids given.");
            var done = new List<string>();
            var recompute = false;
            if (Text(args, "nickname") is string nickname)
            {
                foreach (var obj in objects)
                {
                    obj.NickName = nickname;
                    obj.Attributes?.ExpireLayout();
                }
                done.Add("nickname " + nickname);
            }
            if (args.TryGetProperty("enabled", out var enabled) && (enabled.ValueKind == JsonValueKind.True || enabled.ValueKind == JsonValueKind.False))
            {
                foreach (var active in objects.OfType<IGH_ActiveObject>())
                    active.Locked = !enabled.GetBoolean();
                done.Add(enabled.GetBoolean() ? "enabled" : "disabled");
                recompute = true;
            }
            if (args.TryGetProperty("preview", out var preview) && (preview.ValueKind == JsonValueKind.True || preview.ValueKind == JsonValueKind.False))
            {
                foreach (var shown in objects.OfType<IGH_PreviewObject>())
                    shown.Hidden = !preview.GetBoolean();
                done.Add(preview.GetBoolean() ? "preview on" : "preview off");
                Instances.RedrawCanvas();
                RhinoDoc.ActiveDoc?.Views.Redraw();
            }
            var by = Numbers(args, "move_by");
            var to = Numbers(args, "move_to");
            if (to != null && to.Length >= 2 && objects[0].Attributes != null)
                by = new[] { to[0] - objects[0].Attributes.Pivot.X, to[1] - objects[0].Attributes.Pivot.Y };
            if (by != null && by.Length >= 2)
            {
                foreach (var obj in objects.Where(o => o.Attributes != null))
                {
                    obj.Attributes.Pivot = new PointF(obj.Attributes.Pivot.X + (float)by[0], obj.Attributes.Pivot.Y + (float)by[1]);
                    obj.Attributes.ExpireLayout();
                }
                done.Add(string.Format(CultureInfo.InvariantCulture, "moved by ({0:0}, {1:0})", by[0], by[1]));
            }
            if (Text(args, "group") is string group)
            {
                var made = new GH_Group { NickName = group, Colour = Color.FromArgb(120, 170, 200, 230) };
                made.CreateAttributes();
                foreach (var obj in objects)
                    made.AddObject(obj.InstanceGuid);
                doc.AddObject(made, false);
                done.Add("grouped as '" + group + "' [" + made.InstanceGuid + "]");
            }
            if (recompute)
                doc.NewSolution(false);
            Instances.InvalidateCanvas();
            return done.Count == 0 ? "Nothing to change (nickname, enabled, preview, move_by, move_to, group)." : string.Format("{0} object(s): {1}.", objects.Count, string.Join(", ", done));
        }

        private static double[] Numbers(JsonElement args, string name) =>
            args.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
                ? array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number).Select(e => e.GetDouble()).ToArray()
                : null;

        private static string Disconnect(JsonElement args)
        {
            var doc = Document();
            var obj = Find(doc, Text(args, "id"));
            var param = FindParam(obj, Text(args, "param"), "input");
            if (param.SourceCount == 0)
                return param.NickName + " has no wires.";
            var removed = 0;
            if (args.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
            {
                foreach (var source in sources.EnumerateArray())
                {
                    var from = Find(doc, source.TryGetProperty("id", out var id) ? id.GetString() : null);
                    var fromParam = source.TryGetProperty("param", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                    foreach (var wire in param.Sources.Where(s => ReferenceEquals(Owner(s), from) &&
                                 (fromParam == null || string.Equals(s.NickName, fromParam, StringComparison.OrdinalIgnoreCase) || string.Equals(s.Name, fromParam, StringComparison.OrdinalIgnoreCase)
                                  || (int.TryParse(fromParam, out var index) && from is IGH_Component c && index < c.Params.Output.Count && ReferenceEquals(c.Params.Output[index], s)))).ToList())
                    {
                        param.RemoveSource(wire);
                        removed++;
                    }
                }
            }
            else
            {
                removed = param.SourceCount;
                param.RemoveAllSources();
            }
            param.ExpireSolution(true);
            Instances.InvalidateCanvas();
            return string.Format("{0}.{1}: {2} wire(s) removed, {3} left.", Label(Owner(param)), param.NickName, removed, param.SourceCount);
        }

        private static string AddNote(JsonElement args)
        {
            var doc = Document();
            var text = Text(args, "text") ?? "";
            var near = FindAll(doc, args, "near");
            var width = args.TryGetProperty("width", out var w) && w.TryGetDouble(out var wd) ? (float)wd : 320f;
            var height = args.TryGetProperty("height", out var h) && h.TryGetDouble(out var hd) ? (float)hd : Math.Min(1200, NoteHeight(text, width));
            PointF at;
            if (args.TryGetProperty("x", out var x) && x.TryGetDouble(out var xd) && args.TryGetProperty("y", out var y) && y.TryGetDouble(out var yd))
                at = new PointF((float)xd, (float)yd);
            else if (near.Count > 0)
            {
                var bounds = near.Where(o => o.Attributes != null).Select(o => o.Attributes.Bounds).Aggregate(RectangleF.Union);
                at = new PointF(bounds.Right + 40, bounds.Top);
            }
            else
            {
                var canvas = Instances.ActiveCanvas;
                var visible = canvas.Viewport.VisibleRegion;
                at = new PointF(visible.Left + 40, visible.Top + 40);
            }
            var panel = new GH_Panel();
            panel.CreateAttributes();
            panel.NickName = "Note";
            panel.UserText = text;
            panel.Properties.Multiline = true;
            panel.Attributes.Pivot = at;
            panel.Attributes.Bounds = new RectangleF(at.X, at.Y, Math.Max(120, width), Math.Max(40, height));
            doc.AddObject(panel, false);
            var markdown = !args.TryGetProperty("markdown", out var md) || md.ValueKind != JsonValueKind.False;
            if (markdown)
                ScriptPanels.ChangeShowAs(panel, ScriptPanels.ShowAs.Markdown, recordUndo: false);
            panel.Attributes.ExpireLayout();
            Instances.InvalidateCanvas();
            return string.Format(CultureInfo.InvariantCulture, "Note added [{0}] at ({1:0}, {2:0}){3}.", panel.InstanceGuid, at.X, at.Y, markdown ? ", shown as Markdown" : "");
        }

        /// <summary>A note's height for its Markdown in <paramref name="width"/>: each line wrapped (about 6.5 px
        /// a character), headings taller, blank lines half, generously -- a note cut off at the bottom is worse.</summary>
        internal static float NoteHeight(string text, float width)
        {
            var perLine = Math.Max(10, (int)((width - 16) / 6.5f));
            var height = 24f;
            foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0)
                {
                    height += 9;
                    continue;
                }
                var heading = line.StartsWith("#");
                var rows = Math.Max(1, (int)Math.Ceiling(line.Length * (heading ? 1.4 : 1.0) / perLine));
                height += rows * (heading ? 26 : 18);
            }
            return height * 1.1f;
        }

        // --- the canvas as an image ----------------------------------------------------------------

        private static string Capture(JsonElement args)
        {
            var doc = Document();
            var canvas = Instances.ActiveCanvas ?? throw new ToolError("Grasshopper's canvas isn't open.");
            var objects = FindAll(doc, args, "ids");
            if (objects.Count == 0)
                objects = doc.Objects.Where(o => !(o is GH_Group)).ToList();
            var withBounds = objects.Where(o => o.Attributes != null).ToList();
            if (withBounds.Count == 0)
                throw new ToolError("The definition is empty.");
            var region = withBounds.Select(o => o.Attributes.Bounds).Aggregate(RectangleF.Union);
            region.Inflate(30, 30);
            var width = args.TryGetProperty("width", out var w) && w.TryGetInt32(out var wi) ? Math.Max(300, Math.Min(4000, wi)) : 1400;
            var zoom = Math.Min(1.5f, Math.Min(width / region.Width, 2400f / region.Height));
            var viewport = new GH_Viewport
            {
                Width = Math.Max(100, (int)Math.Ceiling(region.Width * zoom)),
                Height = Math.Max(100, (int)Math.Ceiling(region.Height * zoom)),
            };
            viewport.Zoom = zoom;
            viewport.Tx = (int)Math.Round(-region.Left * zoom);
            viewport.Ty = (int)Math.Round(-region.Top * zoom);
            using (var image = canvas.GenerateHiResImageTile(viewport, Color.FromArgb(212, 208, 200)))
            {
                if (image == null)
                    throw new ToolError("Grasshopper couldn't draw the canvas.");
                Directory.CreateDirectory(CaptureFolder);
                var path = Path.Combine(CaptureFolder, "canvas-" + DateTime.Now.ToString("HHmmss-fff") + ".png");
                image.Save(path, ImageFormat.Png);
                return string.Format("Saved the canvas ({0} object(s), {1} x {2} px) to {3} -- Read it to see.", withBounds.Count, image.Width, image.Height, path);
            }
        }
    }
}
