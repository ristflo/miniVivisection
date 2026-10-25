"""miniVivisection's Rhino toolbar (toolbar/miniVivisection.rui) and its icons, drawn here: the
manual, the examples' overview in Rhino and in Grasshopper, the lessons folder, the settings,
MarkdownText and ProtectModel. Each icon is
drawn large and scaled down to 16, 24 and 32 pixels (the RUI's three strips, PNG, base64); the
toolbar's ids are fixed (uuid5 of their names), so Rhino keeps a docked toolbar where it was.
Also toolbar/icons/<name>_24.png -- the Grasshopper components' icons (V°Examples, V°Manual, V°Settings) -- and
toolbar/icons/preview.png, all of them side by side, to look at.

The plug-in copies the RUI to %APPDATA%\\miniVivisection and opens it there (RhinoToolbar);
V°Settings > Rhino > "Help, examples and settings toolbar" shows or hides it. Needs Pillow. Run after
changing an icon or a button:

    python tools/make_toolbar.py
"""
import base64
import io
import os
import uuid

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(HERE, 'src', 'miniVivisection', 'toolbar')
NAMESPACE = uuid.UUID('6f9a3c1e-2b74-4d8e-9c51-0a7d3e8b2f64')
S = 256  # drawn at this size, then scaled down

BLUE = (40, 120, 200, 255)
DARK_BLUE = (20, 60, 110, 255)
ORANGE = (245, 140, 0, 255)
DARK_ORANGE = (150, 80, 0, 255)
DARK = (45, 45, 45, 255)
GRAY = (150, 150, 150, 255)
WHITE = (255, 255, 255, 255)
PAPER = (250, 250, 248, 255)
FOLDER = (236, 182, 60, 255)
GREEN = (70, 150, 60, 255)
DARK_GREEN = (35, 85, 30, 255)
FOLDER_DARK = (170, 120, 20, 255)


def u(v):
    """A coordinate on a 24-unit grid, in drawing pixels."""
    return v * S / 24.0


def box(x1, y1, x2, y2):
    return [u(x1), u(y1), u(x2), u(y2)]


def line(d, points, fill, width):
    d.line([(u(x), u(y)) for x, y in points], fill=fill, width=int(u(width)), joint='curve')


def manual(d):
    """A book, its cover blue with a white question mark: the manual."""
    d.rounded_rectangle(box(5, 2.5, 20.5, 21.5), radius=u(1.2), fill=PAPER, outline=GRAY, width=int(u(0.6)))   # the pages
    d.rounded_rectangle(box(3.5, 1.5, 18.5, 20.5), radius=u(1.4), fill=BLUE, outline=DARK_BLUE, width=int(u(0.8)))  # the cover
    d.rectangle(box(3.5, 1.5, 6, 20.5), fill=DARK_BLUE)  # the spine
    # The question mark
    d.arc(box(8.2, 4.5, 15.8, 11.5), start=180, end=40, fill=WHITE, width=int(u(2)))
    line(d, [(14.6, 9.6), (12, 11.6), (12, 13.6)], WHITE, 2)
    d.ellipse(box(10.9, 15.4, 13.1, 17.6), fill=WHITE)


def overview(d):
    """A page listing the examples -- a heading, bullets and lines -- and an orange play badge."""
    d.rounded_rectangle(box(2.5, 1.5, 18.5, 22.5), radius=u(1.2), fill=PAPER, outline=GRAY, width=int(u(0.7)))
    line(d, [(5, 5), (14, 5)], DARK, 2.2)
    for y in (9.5, 13.5, 17.5):
        d.ellipse(box(4.6, y - 1, 6.6, y + 1), fill=BLUE)
        line(d, [(8.2, y), (15.8, y)], BLUE, 1.4)
    # The badge: try it
    d.ellipse(box(13, 13, 23, 23), fill=ORANGE, outline=DARK_ORANGE, width=int(u(0.7)))
    d.polygon([(u(16.6), u(15.6)), (u(16.6), u(20.4)), (u(20.6), u(18))], fill=WHITE)


def gh_overview(d):
    """The examples' page with a green hexagon -- Grasshopper's examples -- and a white play mark."""
    d.rounded_rectangle(box(2.5, 1.5, 18.5, 22.5), radius=u(1.2), fill=PAPER, outline=GRAY, width=int(u(0.7)))
    line(d, [(5, 5), (14, 5)], DARK, 2.2)
    for y in (9.5, 13.5, 17.5):
        d.ellipse(box(4.6, y - 1, 6.6, y + 1), fill=BLUE)
        line(d, [(8.2, y), (15.8, y)], BLUE, 1.4)
    import math
    hexagon = [(u(18 + 5.4 * math.cos(math.radians(60 * k + 30))), u(18 + 5.4 * math.sin(math.radians(60 * k + 30)))) for k in range(6)]
    d.polygon(hexagon, fill=GREEN, outline=DARK_GREEN, width=int(u(0.7)))
    d.polygon([(u(16.4), u(15.6)), (u(16.4), u(20.4)), (u(20.4), u(18))], fill=WHITE)


def lessons(d):
    """A folder with a page in it: the lessons folder (your copies)."""
    d.rounded_rectangle(box(1.5, 4, 10, 8), radius=u(1), fill=FOLDER_DARK)  # the tab
    d.rounded_rectangle(box(1.5, 6, 22.5, 20.5), radius=u(1.2), fill=FOLDER_DARK)  # the back
    d.rectangle(box(5, 3, 19, 15), fill=PAPER, outline=GRAY, width=int(u(0.6)))  # a page in it
    for y in (6, 8.5, 11):
        line(d, [(7, y), (17, y)], BLUE, 1.2)
    d.polygon([(u(1.5), u(10)), (u(22.5), u(10)), (u(21.5), u(20.5)), (u(2.5), u(20.5))], fill=FOLDER, outline=FOLDER_DARK)  # the front


def settings(d):
    """A gear: the settings (V°Settings, Rhino's Options at the miniVivisection page)."""
    import math
    outline = []
    teeth = 8
    for k in range(teeth * 4):
        angle = math.radians(k * 360.0 / (teeth * 4) + 360.0 / (teeth * 8))
        radius = 10.6 if k % 4 in (0, 1) else 7.9
        outline.append((u(12 + radius * math.cos(angle)), u(12 + radius * math.sin(angle))))
    d.polygon(outline, fill=(85, 95, 110, 255), outline=(45, 50, 60, 255), width=int(u(0.6)))
    d.ellipse(box(8.2, 8.2, 15.8, 15.8), fill=BLUE, outline=DARK_BLUE, width=int(u(0.6)))
    d.ellipse(box(10.3, 10.3, 13.7, 13.7), fill=(0, 0, 0, 0))


def markdown_text(d):
    """A heading over lines of text, its anchor an orange dot: Markdown as Rhino text (as V°Text's icon)."""
    line(d, [(6, 5), (18, 5)], DARK, 3)
    line(d, [(4, 8.6), (21, 8.6)], GRAY, 1)
    for y, x2 in ((12.5, 21), (16.5, 19), (20.5, 14)):
        line(d, [(4, y), (x2, y)], BLUE, 2)
    # A task box, ticked: what MarkdownText draws
    d.rectangle(box(16.2, 18.2, 21.8, 23.8), fill=PAPER, outline=DARK, width=int(u(0.8)))
    line(d, [(17.2, 21.1), (18.6, 22.6), (21, 19.3)], DARK, 1.1)
    d.ellipse(box(0.5, 0.5, 6.5, 6.5), fill=ORANGE, outline=DARK_ORANGE, width=int(u(0.7)))


def protect(d):
    """A shield with a check mark (as V°Protect's icon)."""
    points = [(12, 2), (21, 5)]
    # The shield's sides: two curves down to its point
    for t in range(1, 21):
        s = t / 20.0
        points.append((21 * (1 - s) ** 3 + 3 * 21 * (1 - s) ** 2 * s + 3 * 18 * (1 - s) * s ** 2 + 12 * s ** 3,
                       5 * (1 - s) ** 3 + 3 * 14 * (1 - s) ** 2 * s + 3 * 19 * (1 - s) * s ** 2 + 22 * s ** 3))
    for t in range(1, 21):
        s = t / 20.0
        points.append((12 * (1 - s) ** 3 + 3 * 6 * (1 - s) ** 2 * s + 3 * 3 * (1 - s) * s ** 2 + 3 * s ** 3,
                       22 * (1 - s) ** 3 + 3 * 19 * (1 - s) ** 2 * s + 3 * 14 * (1 - s) * s ** 2 + 5 * s ** 3))
    d.polygon([(u(x), u(y)) for x, y in points], fill=BLUE, outline=DARK_BLUE, width=int(u(0.8)))
    line(d, [(7.5, 12), (11, 15.5), (16.5, 8.5)], WHITE, 2.4)


# name, draw, button text, tooltip (left; right), left macro, right macro
BUTTONS = [
    ('manual', manual, 'Manual', 'The miniVivisection manual (PDF)', 'The manual as Markdown, in the Script Editor (V°Help)',
     '! _miniVivisectionManual', '! _-miniVivisectionManual _Markdown'),
    ('examples', overview, 'Examples', 'All examples: the overview, in Rhino (your copy of 00_overview.3dm)', None,
     '! _miniVivisectionExamples', None),
    ('ghexamples', gh_overview, 'Examples in Grasshopper', 'All examples in Grasshopper: started, the overview opened (your copy of gh0_overview.gh)', None,
     '! _miniVivisectionGrasshopperExamples', None),
    ('lessons', lessons, 'Lessons folder', 'Your copies of the lessons, in Explorer', None,
     '! _miniVivisectionLessons', None),
    ('settings', settings, 'Settings', 'The miniVivisection settings: Rhino Options, at its page', None,
     '! _miniVivisectionSettings', None),
    ('markdowntext', markdown_text, 'MarkdownText', 'Markdown as Rhino text: new, or edit the one selected', None,
     '! _MarkdownText', None),
    ('protectmodel', protect, 'ProtectModel', 'Is this file still the original? Protect it, revert it', None,
     '! _ProtectModel', None),
]


def gid(name):
    return str(uuid.uuid5(NAMESPACE, name))


def icon(draw, size):
    image = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    draw(ImageDraw.Draw(image))
    return image.resize((size, size), Image.LANCZOS)


def strip(size):
    image = Image.new('RGBA', (size * len(BUTTONS), size), (0, 0, 0, 0))
    for i, button in enumerate(BUTTONS):
        image.paste(icon(button[1], size), (i * size, 0))
    data = io.BytesIO()
    image.save(data, 'PNG')
    return base64.b64encode(data.getvalue()).decode('ascii')


def text(tag, value, indent):
    pad = ' ' * indent
    return '%s<%s>\n%s  <locale_1033>%s</locale_1033>\n%s</%s>' % (pad, tag, pad, value, pad, tag)


def rui():
    group, bar = gid('group'), gid('toolbar')
    lines = ['<?xml version="1.0" encoding="utf-8"?>',
             '<RhinoUI major_ver="2" minor_ver="0" guid="%s" localize="False" default_language_id="1033">' % gid('file'),
             '  <extend_rhino_menus />', '  <menus />', '  <tool_bar_groups>',
             '    <tool_bar_group guid="%s" dock_bar_guid32="00000000-0000-0000-0000-000000000000" dock_bar_guid64="%s" '
             'active_tool_bar_group="%s" single_file="False" hide_single_tab="True" point_floating="400,200">' % (group, gid('dockbar'), gid('group item')),
             text('text', 'miniVivisection', 6),
             '      <tool_bar_group_item guid="%s" major_version="1" minor_version="1">' % gid('group item'),
             text('text', 'miniVivisection', 8),
             '        <tool_bar_id>%s</tool_bar_id>' % bar,
             '      </tool_bar_group_item>',
             '      <dock_bar_info dock_bar="False" docking="True" horz="True" visible="True" floating="True" mru_float_style="8192" '
             'bar_id="59600" mru_width="32767" point_pos="-2,-2" float_point="400,200" rect_mru_dock_pos="0,0,0,0" '
             'dock_location_u="59419" dock_location="top" float_size="190,40" />',
             '    </tool_bar_group>', '  </tool_bar_groups>', '  <tool_bars>',
             '    <tool_bar guid="%s" bitmap_id="%s" item_display_style="control_only">' % (bar, gid('icon manual')),
             text('text', 'miniVivisection', 6)]
    for name, _, label, left, right, left_macro, right_macro in BUTTONS:
        lines.append('      <tool_bar_item guid="%s" button_display_mode="control_only" button_style="normal">' % gid('item ' + name))
        lines.append(text('text', label, 8))
        lines.append('        <left_macro_id>%s</left_macro_id>' % gid('macro ' + name))
        if right_macro:
            lines.append('        <right_macro_id>%s</right_macro_id>' % gid('macro right ' + name))
        lines.append('      </tool_bar_item>')
    lines += ['    </tool_bar>', '  </tool_bars>', '  <macros>']
    for name, _, label, left, right, left_macro, right_macro in BUTTONS:
        for which, tip, script in (('macro ', left, left_macro), ('macro right ', right, right_macro)):
            if not script:
                continue
            lines.append('    <macro_item guid="%s" bitmap_id="%s">' % (gid(which + name), gid('icon ' + name)))
            lines.append(text('text', label, 6))
            lines.append(text('tooltip', tip, 6))
            lines.append(text('help_text', tip, 6))
            lines.append(text('button_text', label, 6))
            lines.append(text('menu_text', label, 6))
            lines.append('      <script>%s</script>' % script)
            lines.append('    </macro_item>')
    lines += ['  </macros>', '  <bitmaps>']
    for tag, size in (('small_bitmap', 16), ('normal_bitmap', 24), ('large_bitmap', 32)):
        lines.append('    <%s item_width="%d" item_height="%d">' % (tag, size, size))
        for i, button in enumerate(BUTTONS):
            lines.append('      <bitmap_item guid="%s" index="%d" />' % (gid('icon ' + button[0]), i))
        lines.append('      <bitmap>%s</bitmap>' % strip(size))
        lines.append('    </%s>' % tag)
    lines += ['  </bitmaps>', '  <scripts />', '</RhinoUI>']
    return '\n'.join(lines) + '\n'


def main():
    os.makedirs(os.path.join(OUT, 'icons'), exist_ok=True)
    with open(os.path.join(OUT, 'miniVivisection.rui'), 'w', encoding='utf-8', newline='\r\n') as f:
        f.write(rui())
    for name, draw, *_ in BUTTONS:
        icon(draw, 24).save(os.path.join(OUT, 'icons', name + '_24.png'))
    # All of them, at each size and large, side by side: to look at
    sizes = (16, 24, 32, 96)
    preview = Image.new('RGBA', (sum(sizes) + 40, 96 * len(BUTTONS) + 10 * len(BUTTONS)), (205, 205, 205, 255))
    for row, (name, draw, *_) in enumerate(BUTTONS):
        x = 0
        for size in sizes:
            preview.paste(icon(draw, size), (x, row * 106), icon(draw, size))
            x += size + 10
    preview.save(os.path.join(OUT, 'icons', 'preview.png'))
    print('wrote', os.path.join(OUT, 'miniVivisection.rui'))


if __name__ == '__main__':
    main()
