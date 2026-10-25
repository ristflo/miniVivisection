using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>Baking a value of the paused script into the document, for the context
    /// menus, the V Watch tab and the command.</summary>
    internal static class WatchActions
    {
        public const string RootLayer = "Script Editor Watch";

        public const string NotReachable =
            "the paused script runs on its own thread -- turn FreePause on (miniVivisection FreePause=On) and continue to the next pause";

        /// <summary>Bakes <paramref name="expression"/>'s current value: evaluated on the
        /// paused thread, added to the document on the UI thread.</summary>
        public static void Bake(ScriptEditorHook hook, IDebugControls controls, string expression)
        {
            var reached = hook.RunOnPausedThread(controls, () =>
            {
                if (!ScriptEditorHook.TryEvaluate(controls, expression, out var value, out var error))
                {
                    UserLog.Warning("Can't bake {0}: {1}", expression, error);
                    return;
                }
                var items = WatchGeometry.Drawable(value, "auto", true, out _, out _, out _);
                if (items.Count == 0)
                {
                    UserLog.Warning("{0} has nothing to bake ({1}).", expression, ScriptEditorHook.TypeName(value));
                    return;
                }
                var watched = new WatchedValue(expression, WatchGeometry.ColorFor(expression), items);
                RhinoApp.InvokeOnUiThread(new Action(() => Bake(RhinoDoc.ActiveDoc, new[] { watched })));
            });
            if (!reached)
                UserLog.Warning("Can't bake {0}: {1}.", expression, NotReachable);
        }

        /// <summary>Adds copies of <paramref name="values"/> to the document, on layer
        /// "Script Editor Watch::Baked": named after their watch, in its color, a list's
        /// items grouped; planes are left out. One undo step.</summary>
        public static int Bake(RhinoDoc doc, IEnumerable<WatchedValue> values)
        {
            if (doc == null)
                return 0;
            var layer = LayerIndex(doc, "Baked");
            var undo = doc.BeginUndoRecord("Bake watched values");
            var count = 0;
            try
            {
                foreach (var value in values)
                {
                    var ids = new List<Guid>();
                    for (var i = 0; i < value.Items.Count; i++)
                    {
                        var item = value.Items[i];
                        var geometry = item.ToDocumentGeometry();
                        if (geometry == null)
                            continue;
                        var attributes = Attributes(layer, value.Items.Count == 1 ? value.Name : string.Format("{0}[{1}]", value.Name, i), value.Color);
                        if (item.Arrow.HasValue)
                            attributes.ObjectDecoration = ObjectDecoration.EndArrowhead;
                        var id = doc.Objects.Add(geometry, attributes);
                        if (id != Guid.Empty)
                            ids.Add(id);
                    }
                    if (ids.Count > 1)
                        doc.Groups.Add(value.Name, ids);
                    count += ids.Count;
                }
            }
            finally
            {
                doc.EndUndoRecord(undo);
            }
            doc.Views.Redraw();
            UserLog.Success("Baked {0} object(s) to layer {1}::Baked.", count, RootLayer);
            return count;
        }

        internal static ObjectAttributes Attributes(int layer, string name, System.Drawing.Color color)
        {
            return new ObjectAttributes
            {
                LayerIndex = layer,
                Name = name,
                ColorSource = ObjectColorSource.ColorFromObject,
                ObjectColor = color,
            };
        }

        /// <summary>The layer "Script Editor Watch::<paramref name="name"/>", created when missing.</summary>
        internal static int LayerIndex(RhinoDoc doc, string name)
        {
            var index = doc.Layers.FindByFullPath(RootLayer + "::" + name, -1);
            if (index >= 0)
                return index;
            var root = doc.Layers.FindByFullPath(RootLayer, -1);
            if (root < 0)
                root = doc.Layers.Add(new Layer { Name = RootLayer });
            return doc.Layers.Add(new Layer { Name = name, ParentLayerId = doc.Layers[root].Id });
        }
    }
}
