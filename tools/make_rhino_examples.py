#! python 3
"""Makes miniVivisection's Rhino example, examples/00_overview.3dm: the overview of all examples
(tools/examples_overview.py) as Markdown text, linking every lesson, and right of it the Markdown
text example -- Markdown made into Rhino objects by the MarkdownText command (MarkdownRhinoText),
with links to named views (Overview, MarkdownText) and named objects saved with
it -- now the V° logo's outlines (Logo). The file is protected (ProtectModel: RhinoProtect, its original in the file itself): the
overview's {{protected}} and {{modified}} say so, its protect:revert link reverts.

Runs inside Rhino 8 with the plug-in loaded (the live test t_markdowntext runs make;
tests/live/make_examples.py too): it makes the texts in the active document (empty) and saves
and protects it.

    make(doc, path, protect=True)   # returns the ids of the Markdown example's objects
"""
import os
import sys

import System
import Rhino

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(HERE, 'tools'))
import examples_overview  # noqa: E402 (next to this file)

PATH = OVERVIEW_PATH = os.path.join(HERE, 'examples', examples_overview.RHINO_FILE)

MARKDOWN = """# Markdown text in Rhino

Made by the **MarkdownText** command from Markdown -- double-click any part of it (or run `MarkdownText` on it): its Markdown opens in a dialog, the caret at that part, and the same objects are updated where they are.

## What it draws

- Headings, **bold**, *italic*, `code` and [links](https://www.rhino3d.com) (blue, a line under them)
- How all of it looks -- sizes, fonts, colors, spacing, bullets, task boxes, the quote's bar, the links' color -- is the file's Markdown style: *MarkdownText* > *Edit style...*
- Lists, also nested:
  - A nested item
  - And another
1. Numbered lists
2. Their numbers counted for you
- [x] Task lists: a task done
- [ ] An open task -- click its box to check it

> Quotes, with a bar beside them.

```
code blocks = "in a frame, in Consolas"
```

| Markdown | Rhino object |
|----------|--------------|
| text | Text, rich text |
| rules | Line |
| task boxes | Polyline |
| quote bars | Text: a `|` on each line |
| code blocks | Text in a frame |

## Links

Click a link to follow it -- a double-click edits (V°Settings > *Markdown* > *Markdown links: follow with*: Click, Ctrl+click or Off):

- [Overview](rhinoview:Overview) -- a named view, restored; [Perspective](rhinoview:Perspective) -- a viewport by its title
- [The V° logo](rhino:Logo) -- objects by their name: zoomed to, marked in red
- [The Ring component in GH 11](gh:grasshopper/gh11_markdown_notes.gh#Ring) -- Grasshopper opens that lesson (when it isn't open) and goes to *Ring*; its links lead back here
- [GH 11's overview](ghview:grasshopper/gh11_markdown_notes.gh#Overview) -- a named view of its canvas
- [Lesson 01](01_watch_geometry.py) -- a script: the Script Editor opens it
- [All examples](rhinoview:Overview) -- the overview, left of this text, every lesson linked
- [Grasshopper](https://www.grasshopper3d.com) -- the browser

---

This text is one group, and every part knows the text it belongs to -- ungrouped too. Edit a part with Rhino's own tools (its words, or move the whole text): the next MarkdownText takes it into the Markdown.
The dialog sets the width (mm), the base size (pt) and the base font, and the file's Markdown style -- *Edit style...*, *Load...*, *Save as...*, *Default* --, for all of its Markdown texts.
"""


def _builder():
    for assembly in System.AppDomain.CurrentDomain.GetAssemblies():
        if assembly.GetName().Name == 'miniVivisection':
            return assembly.GetType('MiniVivisection.MarkdownRhinoText')
    raise RuntimeError('miniVivisection is not loaded')


def frame(height=1.0, width=70.0, plane=None):
    """A MarkdownRhinoText.Frame: the plane (its origin: the top left), the height, the width."""
    kind = _builder().GetNestedType('Frame', System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
    result = System.Activator.CreateInstance(kind)
    kind.GetField('Height').SetValue(result, float(height))
    kind.GetField('Width').SetValue(result, float(width))
    if plane is not None:
        kind.GetField('Plane').SetValue(result, plane)
    return result


def create(doc, markdown, where):
    """MarkdownRhinoText.Create: the ids of the objects made (grouped)."""
    flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
    args = System.Array[System.Object]([doc, markdown, where, 0])
    ids = _builder().GetMethod('Create', flags).Invoke(None, args)
    return list(ids)


def _layer(doc, name, color):
    """The layer's index, made when missing."""
    index = doc.Layers.FindByFullPath(name, -1)
    if index < 0:
        index = doc.Layers.Add(name, color)
    return index


def _on_layer(doc, ids):
    """The text on a layer of its own; its bounding box."""
    text_layer = _layer(doc, 'Markdown text', System.Drawing.Color.FromArgb(40, 40, 40))
    for i in ids:
        o = doc.Objects.FindId(i)
        attributes = o.Attributes.Duplicate()
        attributes.LayerIndex = text_layer
        doc.Objects.ModifyAttributes(o, attributes, True)
    doc.Views.Redraw()
    box = Rhino.Geometry.BoundingBox.Empty
    for i in ids:
        box.Union(doc.Objects.FindId(i).Geometry.GetBoundingBox(True))
    return box


def _named_view(doc, box, name):
    """All of box in the Top view (maximized), with a margin, kept as the named view name."""
    box = Rhino.Geometry.BoundingBox(box.Min, box.Max)
    box.Inflate(box.Diagonal.Length * 0.06)
    top = doc.Views.Find('Top', False)
    if top is not None:
        doc.Views.ActiveView = top
        top.Maximized = True
    doc.Views.Redraw()
    for view in doc.Views:
        view.ActiveViewport.ZoomBoundingBox(box)
    doc.Views.Redraw()
    if top is not None:
        index = doc.NamedViews.FindByName(name)
        if index >= 0:
            doc.NamedViews.Delete(index)
        doc.NamedViews.Add(name, top.ActiveViewportID)


def _save(doc, path):
    options = Rhino.FileIO.FileWriteOptions()
    options.UpdateDocumentPath = True  # ProtectModel keeps the file at its path
    if not doc.WriteFile(path, options):
        raise RuntimeError('could not save ' + path)


def _protect(doc):
    """ProtectModel in the file itself (RhinoProtect): saved and kept as the original."""
    flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
    protect = None
    for assembly in System.AppDomain.CurrentDomain.GetAssemblies():
        if assembly.GetName().Name == 'miniVivisection':
            protect = assembly.GetType('MiniVivisection.RhinoProtect')
    settings = protect.GetMethod('Read', flags).Invoke(None, [doc])
    settings.InItself = True
    settings.Protected = False
    doc.Strings.SetString(protect.GetField('SettingsKey', flags).GetValue(None), settings.ToString())
    problem = protect.GetMethod('Protect', flags).Invoke(None, [doc])
    if problem:
        raise RuntimeError(problem)


def _logo(doc, corner, side):
    """The V° logo's outlines, top left at corner, side long -- as the package's icon
    (tools/make_yak.py: a bold Segoe UI V° on a rounded square, 32 units, corners 7, the text
    19 units, its baseline 23 units down, its middle 15 units in): the square's outline and the
    letters' outlines, curves named Logo (the link rhino:Logo names them), on the layer Link
    targets. Their bounding box."""
    unit = side / 32.0
    attributes = doc.CreateDefaultAttributes()
    attributes.Name = 'Logo'
    attributes.LayerIndex = _layer(doc, 'Link targets', System.Drawing.Color.FromArgb(193, 39, 45))
    tolerance = doc.ModelAbsoluteTolerance
    square = Rhino.Geometry.Rectangle3d(Rhino.Geometry.Plane(corner, Rhino.Geometry.Vector3d.XAxis, Rhino.Geometry.Vector3d.YAxis),
                                        Rhino.Geometry.Interval(0, side), Rhino.Geometry.Interval(-side, 0)).ToNurbsCurve()
    rounded = Rhino.Geometry.Curve.CreateFilletCornersCurve(square, 7 * unit, tolerance, doc.ModelAngleToleranceRadians) or square
    curves = [rounded]
    # The letters: a bold Segoe UI text, its capitals' height about 0.7 of the font size
    plane = Rhino.Geometry.Plane(Rhino.Geometry.Point3d(corner.X + 15 * unit, corner.Y - 23 * unit, corner.Z), Rhino.Geometry.Vector3d.XAxis, Rhino.Geometry.Vector3d.YAxis)
    style = doc.DimStyles.Current
    text = Rhino.Geometry.TextEntity.Create('V\u00b0', plane, style, False, 0, 0)
    text.Font = Rhino.DocObjects.Font('Segoe UI', Rhino.DocObjects.Font.FontWeight.Bold, Rhino.DocObjects.Font.FontStyle.Upright, False, False)
    text.DimensionScale = 1
    text.TextHeight = 19 * unit * 0.7
    text.TextHorizontalAlignment = Rhino.DocObjects.TextHorizontalAlignment.Center
    text.TextVerticalAlignment = Rhino.DocObjects.TextVerticalAlignment.Bottom
    curves += list(text.CreateCurves(style, False, 1.0, 0.0) or [])
    box = Rhino.Geometry.BoundingBox.Empty
    for curve in curves:
        doc.Objects.AddCurve(curve, attributes)
        box.Union(curve.GetBoundingBox(True))
    return box


def make(doc, path=PATH, protect=True):
    """The overview (70 wide) and, at x = 80, the Markdown text example; at x = 170 the V° logo's
    outlines its links name; the named views Overview (all of it) and MarkdownText (the example);
    saved to path and protected. The example's ids."""
    overview = create(doc, examples_overview.markdown('rhino'), frame(width=70.0))
    overview_box = _on_layer(doc, overview)
    # The Markdown example right of the overview, their tops level
    plane = Rhino.Geometry.Plane(Rhino.Geometry.Point3d(80, 0, 0), Rhino.Geometry.Vector3d.XAxis, Rhino.Geometry.Vector3d.YAxis)
    ids = create(doc, MARKDOWN, frame(plane=plane))
    box = _on_layer(doc, ids)
    # The V° logo the links name, right of the example, its top left at (170, 0, 0)
    box.Union(_logo(doc, Rhino.Geometry.Point3d(170, 0, 0), 20.0))
    _named_view(doc, box, 'MarkdownText')
    everything = Rhino.Geometry.BoundingBox(overview_box.Min, overview_box.Max)
    everything.Union(box)
    _named_view(doc, everything, 'Overview')  # the view the file opens with: all of it
    _save(doc, path)
    if protect:
        _protect(doc)
    return ids
