namespace MiniVivisection
{
    /// <summary>JavaScript shared by the scripts the plug-in runs in the code view (Monaco
    /// in WebView2): HoverPreview, InlineValues, BreakpointMarkers.</summary>
    internal static class CodeViewScripts
    {
        /// <summary>Part of a function body: sets <c>editor</c> to the Script Editor's own
        /// Monaco editor -- Ed.Core's app keeps it in <c>globalThis.monaco_editor</c> (its
        /// getEditor()); the first of <c>monaco.editor.getEditors()</c> needn't be it -- or
        /// returns 'no Monaco editor found'.</summary>
        public const string FindEditor = @"
var editor = globalThis.monaco_editor || null;
try { if (!editor && window.monaco && monaco.editor && monaco.editor.getEditors) editor = monaco.editor.getEditors()[0]; } catch (e) {}
if (!editor && window.ed && ed.editor && (ed.editor.deltaDecorations || ed.editor.onMouseMove)) editor = ed.editor;
if (!editor) return 'no Monaco editor found';
";
    }
}
