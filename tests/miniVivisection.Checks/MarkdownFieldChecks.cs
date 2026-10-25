// Markdown fields (MarkdownFields): {{protected}} and {{modified}} put in as the protection's
// state says -- by both parsers, in a paragraph, a list item, bold, a heading's end --, their words
// without a place in the source, the text around them where it was; the protect: link described.
using System.Collections.Generic;
using System.Linq;

namespace MiniVivisection
{
    internal static partial class Checks
    {
        private static void MarkdownFieldChecks()
        {
            var original = new MarkdownFields.State { IsProtected = true, Disk = "original" };
            var modified = new MarkdownFields.State { IsProtected = true, Disk = "modified" };
            var unknown = new MarkdownFields.State { IsProtected = true, Disk = "unknown" };
            Check("fields: the words", (MarkdownFields.State.None.ProtectedText, MarkdownFields.State.None.ModifiedText, original.ProtectedText, original.ModifiedText, modified.ModifiedText, unknown.ModifiedText),
                ("not protected", "not tracked", "protected", "not modified", "modified", "not tracked"));
            Check("fields: found (spaces, case)", (MarkdownFields.Has("a {{ Protected }} b"), MarkdownFields.Has("{{modified}}"), MarkdownFields.Has("{{other}}"), MarkdownFields.Has("no field")), (true, true, false, false));

            const string markdown = "# State: {{protected}}\n\nThis file is {{protected}} and {{modified}}.\n\n- **{{modified}}** now\n\n[Revert](protect:revert)";
            string Text(List<MarkdownBlocks.Block> blocks) => string.Join(" | ", blocks.Select(b => string.Concat(b.Runs.Select(r => r.Text))));
            foreach (var (name, parse) in new (string, System.Func<string, List<MarkdownBlocks.Block>>)[] { ("Markdown", MarkdownBlocks.Parse), ("Markdig", MarkdownMarkdig.Parse) })
            {
                var blocks = MarkdownFields.Expand(parse(markdown), modified);
                Check(name + " fields: put in", Text(blocks), "State: protected | This file is protected and modified. | modified now | Revert");
                var paragraph = blocks[1].Runs;
                Check(name + " fields: their words have no place in the source", paragraph.Where(r => r.Text == "protected" || r.Text == "modified").All(r => r.Line == -1), true);
                Check(name + " fields: the text after one where it was", paragraph.Where(r => r.Text.StartsWith(" and")).Select(r => (r.Line, r.Column)).FirstOrDefault(), (2, 26));
                Check(name + " fields: bold kept", blocks[2].Runs.First(r => r.Text == "modified").Style.HasFlag(MarkdownBlocks.Style.Bold), true);
                Check(name + " fields: the protect link", blocks[3].Runs.Select(r => r.Url).FirstOrDefault(), "protect:revert");
            }
            Check("fields: in `code` as written", Text(MarkdownFields.Expand(MarkdownBlocks.Parse("`{{protected}}` is {{protected}}"), original)), "{{protected}} is protected");
            Check("fields: not protected",Text(MarkdownFields.Expand(MarkdownBlocks.Parse("{{protected}}, {{modified}}"), null)), "not protected, not tracked");
            Check("protect link: described", MarkdownLinks.Describe("protect:revert"), "Revert to the protected original");
        }
    }
}
