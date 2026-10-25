// Checks for the prompts window in Grasshopper (Plan_Grasshopper_Prompts.md) and the settings'
// tabs: General (what the Script Editor and Grasshopper share, and Markdown), Rhino, Grasshopper
// (the prompts window's settings among them); the defaults are the documented ones.
using System.Linq;

namespace MiniVivisection
{
    internal static partial class Checks
    {
        private static void GrasshopperPromptsChecks()
        {
            var visible = MiniVivisectionSettings.All.Where(s => !s.Hidden).ToList();
            Check("three tabs: General, Rhino, Grasshopper", string.Join(",", visible.Select(s => s.Tab).Distinct()), "General,Rhino,Grasshopper");
            Check("every Gh setting on the Grasshopper tab", MiniVivisectionSettings.All.Where(s => s.Key.StartsWith("Gh")).All(s => s.Tab == MiniVivisectionSettings.GrasshopperTab), true);
            Check("the Grasshopper group on the Grasshopper tab", MiniVivisectionSettings.All.Where(s => s.Group == MiniVivisectionSettings.GrasshopperGroup).All(s => s.Tab == MiniVivisectionSettings.GrasshopperTab), true);
            Check("every Markdown and link setting in the Markdown group",
                  MiniVivisectionSettings.All.Where(s => s.Key.Contains("Markdown") || s.Key.StartsWith("Link")).All(s => s.Group == MiniVivisectionSettings.MarkdownGroup), true);
            Check("the Markdown group on General", MiniVivisectionSettings.All.Where(s => s.Group == MiniVivisectionSettings.MarkdownGroup).All(s => s.Tab == MiniVivisectionSettings.GeneralTab), true);
            Check("Rhino's: drawing, selection, RunPythonScript, FreePause",
                  new[] { "Draw", "NameDots", "RhinoWatchesAutoEdit", "PickDrawn", "RunCommand", "FreePause" }.All(k => MiniVivisectionSettings.Find(k).Tab == MiniVivisectionSettings.RhinoTab), true);
            Check("a group on one tab only", visible.GroupBy(s => s.Group).All(g => g.Select(s => s.Tab).Distinct().Count() == 1), true);
            Check("read and change by default", MiniVivisectionSettings.GhClaudeReadOnly, false);
            Check("one undo step per answer by default", MiniVivisectionSettings.GhClaudeUndo, true);
            Check("50 items per parameter", MiniVivisectionSettings.GhClaudeItems, 50);
            Check("the window's tabs on", MiniVivisectionSettings.GhPromptsAny, true);
            Check("the group names have V names", VNames.Text(MiniVivisectionSettings.GhClaudeGroup).StartsWith("V"), true);
        }
    }
}
