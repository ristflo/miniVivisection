using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GH_IO.Serialization;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Undo;
using Grasshopper.Kernel.Undo.Actions;

namespace MiniVivisection.GH
{
    /// <summary>
    /// One undo step per answer of V°Claude in Grasshopper (the .rhp's GrasshopperChat, by
    /// reflection): <see cref="Begin"/> before a message records every object as it is,
    /// <see cref="Commit"/> after the answer compares and pushes one record with what changed --
    /// objects added, removed, changed (their wires too) -- so Ctrl+Z takes back the whole answer,
    /// whichever tools made it (RhinoAI's g1_* record no undo; ours neither). Records made during
    /// the answer (a tool's, or the user's own) are folded into it. Only the objects that changed
    /// are put back; the others stay as they are.
    /// </summary>
    public static class ClaudeUndo
    {
        private static readonly FieldInfo s_data = typeof(GH_ArchivedUndoAction).GetField("m_data", BindingFlags.NonPublic | BindingFlags.Instance);

        private sealed class Before
        {
            public GH_Document Document;
            public Dictionary<Guid, (GH_GenericObjectAction Change, GH_RemoveObjectAction Remove)> Objects = new Dictionary<Guid, (GH_GenericObjectAction, GH_RemoveObjectAction)>();
            public HashSet<Guid> Records;
            public Dictionary<Guid, List<Guid>> Wires;
        }

        /// <summary>The answer as one action. Undone: the objects Claude deleted come back, the
        /// changed ones take their old state, those it added go; redone the other way round. Then
        /// every wire of the definition is as it was before (after) the answer: Grasshopper's own
        /// actions keep each object's wires, but in this mix (an object rewired to one that is
        /// added again a moment later) they lose some.</summary>
        private sealed class AnswerAction : GH_UndoAction
        {
            public readonly List<GH_AddObjectAction> Added = new List<GH_AddObjectAction>();
            public readonly List<GH_GenericObjectAction> Changed = new List<GH_GenericObjectAction>();
            public readonly List<GH_RemoveObjectAction> Removed = new List<GH_RemoveObjectAction>();
            public Dictionary<Guid, List<Guid>> WiresBefore, WiresAfter;

            public override bool ExpiresSolution => true;

            public override bool ExpiresDisplay => true;

            protected override void Internal_Undo(GH_Document doc)
            {
                foreach (var action in Removed)
                    Safely(() => action.Undo(doc));
                foreach (var action in Changed)
                    Safely(() => action.Undo(doc));
                foreach (var action in Enumerable.Reverse(Added))
                    Safely(() => action.Undo(doc));
                SetWires(doc, WiresBefore);
            }

            protected override void Internal_Redo(GH_Document doc)
            {
                foreach (var action in Added)
                    Safely(() => action.Redo(doc));
                foreach (var action in Changed)
                    Safely(() => action.Redo(doc));
                foreach (var action in Removed)
                    Safely(() => action.Redo(doc));
                SetWires(doc, WiresAfter);
            }

            private static void Safely(Action action)
            {
                try
                {
                    action();
                }
                catch (Exception)
                {
                    // one object that can't be put back doesn't stop the rest
                }
            }
        }

        /// <summary>Every wire of the definition: each parameter with sources, its sources in order.</summary>
        private static Dictionary<Guid, List<Guid>> Wires(GH_Document doc)
        {
            var wires = new Dictionary<Guid, List<Guid>>();
            foreach (var param in Targets(doc))
            {
                if (param.SourceCount > 0)
                    wires[param.InstanceGuid] = param.Sources.Select(s => s.InstanceGuid).ToList();
            }
            return wires;
        }

        /// <summary>Every parameter that can have sources: components' inputs and floating parameters.</summary>
        private static IEnumerable<IGH_Param> Targets(GH_Document doc)
        {
            foreach (var obj in doc.Objects)
            {
                if (obj is IGH_Component component)
                {
                    foreach (var input in component.Params.Input)
                        yield return input;
                }
                else if (obj is IGH_Param param)
                    yield return param;
            }
        }

        /// <summary>The wires as <paramref name="wires"/> says, where they differ.</summary>
        private static void SetWires(GH_Document doc, Dictionary<Guid, List<Guid>> wires)
        {
            if (wires == null)
                return;
            foreach (var param in Targets(doc).ToList())
            {
                var want = wires.TryGetValue(param.InstanceGuid, out var sources) ? sources : new List<Guid>();
                if (param.Sources.Select(s => s.InstanceGuid).SequenceEqual(want))
                    continue;
                param.RemoveAllSources();
                foreach (var id in want)
                {
                    if (doc.FindObject(id, false) is IGH_Param source)
                        param.AddSource(source);
                }
                param.ExpireSolution(false);
            }
        }

        /// <summary>The active definition as it is now; null without one.</summary>
        public static object Begin()
        {
            var doc = Instances.ActiveCanvas?.Document;
            if (doc == null)
                return null;
            var before = new Before { Document = doc, Records = new HashSet<Guid>(doc.UndoServer.UndoGuids), Wires = Wires(doc) };
            foreach (var obj in doc.Objects)
            {
                try
                {
                    before.Objects[obj.InstanceGuid] = (new GH_GenericObjectAction(obj), new GH_RemoveObjectAction(obj));
                }
                catch (Exception)
                {
                    // an object that can't serialize itself: not put back
                }
            }
            return before;
        }

        /// <summary>After the answer: one undo record "<paramref name="name"/>" when the definition
        /// changed; what changed ("2 changed, 1 added"), or null for nothing.</summary>
        public static string Commit(object snapshot, string name)
        {
            if (!(snapshot is Before before) || before.Document == null)
                return null;
            var doc = before.Document;
            var action = new AnswerAction { WiresBefore = before.Wires, WiresAfter = Wires(doc) };
            var now = doc.Objects.ToDictionary(o => o.InstanceGuid);
            foreach (var pair in now)
            {
                if (!before.Objects.TryGetValue(pair.Key, out var was))
                    action.Added.Add(new GH_AddObjectAction(pair.Value));
                else if (!Same((byte[])s_data?.GetValue(was.Change), Bytes(pair.Value)))
                    action.Changed.Add(was.Change);
            }
            foreach (var pair in before.Objects)
            {
                if (!now.ContainsKey(pair.Key))
                    action.Removed.Add(pair.Value.Remove);
            }
            // Records made during the answer go into this one (its state covers theirs)
            foreach (var id in doc.UndoServer.UndoGuids.Where(id => !before.Records.Contains(id)).ToList())
                doc.UndoServer.RemoveRecord(id);
            if (action.Added.Count + action.Changed.Count + action.Removed.Count == 0)
                return null;
            doc.UndoServer.PushUndoRecord(new GH_UndoRecord(name, action));
            var parts = new List<string>();
            if (action.Changed.Count > 0)
                parts.Add(action.Changed.Count + " changed");
            if (action.Added.Count > 0)
                parts.Add(action.Added.Count + " added");
            if (action.Removed.Count > 0)
                parts.Add(action.Removed.Count + " removed");
            return string.Join(", ", parts);
        }

        /// <summary>/undo: the newest undo record, if it's V°Claude's (its name starts with <paramref name="prefix"/>).
        /// What happened, for the chat.</summary>
        public static string UndoLast(string prefix)
        {
            var doc = Instances.ActiveCanvas?.Document;
            if (doc == null)
                return "No Grasshopper definition is open.";
            var names = doc.UndoServer.UndoNames;
            if (names.Count == 0)
                return "Nothing to undo.";
            if (!names[0].StartsWith(prefix, StringComparison.Ordinal))
                return "The last change in Grasshopper's undo list is yours (" + names[0] + "), not Claude's -- Ctrl+Z on the canvas undoes it.";
            doc.UndoServer.PerformUndo();
            Instances.InvalidateCanvas();
            return "Undone: " + names[0] + ".";
        }

        private static byte[] Bytes(IGH_DocumentObject obj)
        {
            try
            {
                var chunk = new GH_LooseChunk("data");
                return obj.Write(chunk) ? chunk.Serialize_Binary() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool Same(byte[] a, byte[] b) => a != null && b != null && a.AsSpan().SequenceEqual(b);
    }
}
