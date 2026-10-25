"""miniVivisection's screenshots: the captures of tests/live/shots_rhino.py and shots_gh.py
(in %TEMP%\\minivivisection-live\\shots, taken on the test runner's hidden desktop at 125 %)
made pictures of one size, 960 x 600, in docs/screenshots.

Each picture is one window as captured -- scaled to 100 % -- or a Rhino view with a smaller
window over it (the Script Editor, Grasshopper, a dialog), as on screen: a soft shadow and a
thin border, as Windows draws them; a context menu or a tooltip where it was. What each shows:
docs/screenshots/Screenshots.md.

    python tools/compose_screenshots.py
"""
import os
import tempfile

from PIL import Image, ImageDraw, ImageFilter

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RAW = os.path.join(tempfile.gettempdir(), 'minivivisection-live', 'shots')
OUT = os.path.join(HERE, 'docs', 'screenshots')
W, H = 960, 600
S = 0.8  # the hidden desktop's 125 % to 100 %


def raw(name):
    return Image.open(os.path.join(RAW, name + '.png')).convert('RGB')


def scaled(image, factor=S):
    return image.resize((round(image.width * factor), round(image.height * factor)), Image.LANCZOS)


def fit(image, width=W, height=H, focus=(0.5, 0.5)):
    """Scaled to cover width x height, then cut around focus (fractions of the picture)."""
    factor = max(width / image.width, height / image.height)
    image = image.resize((max(width, round(image.width * factor)), max(height, round(image.height * factor))), Image.LANCZOS)
    left = min(max(0, round(image.width * focus[0] - width / 2)), image.width - width)
    top = min(max(0, round(image.height * focus[1] - height / 2)), image.height - height)
    return image.crop((left, top, left + width, top + height))


def window_over(base, window, x, y, border=(112, 112, 112)):
    """window on base at (x, y): a soft shadow under it, a one-pixel border round it."""
    base = base.copy()
    shadow = Image.new('L', (window.width + 60, window.height + 60), 0)
    ImageDraw.Draw(shadow).rectangle((30, 34, 30 + window.width, 34 + window.height), fill=110)
    shadow = shadow.filter(ImageFilter.GaussianBlur(12))
    black = Image.new('RGB', shadow.size, (0, 0, 0))
    base.paste(black, (x - 30, y - 30), shadow)
    framed = window.copy()
    ImageDraw.Draw(framed).rectangle((0, 0, framed.width - 1, framed.height - 1), outline=border)
    base.paste(framed, (x, y))
    return base


def places(name):
    """Where the Grasshopper captures' objects were: {key: (x, y, w, h)} in window pixels."""
    found = {}
    path = os.path.join(RAW, name)
    if os.path.exists(path):
        for line in open(path, encoding='utf-8', errors='replace'):
            parts = line.split()
            if len(parts) == 5:
                found[parts[0]] = tuple(float(v) for v in parts[1:])
    return found


def save(image, name):
    os.makedirs(OUT, exist_ok=True)
    assert image.size == (W, H), (name, image.size)
    path = os.path.join(OUT, name + '.png')
    image.save(path, optimize=True)
    print('wrote', path)


def main():
    gh = places('places_gh.txt')

    # 01 V Watch: the paused script's values drawn in Rhino, the watch rows in the editor
    save(window_over(scaled(raw('watch_viewport')), scaled(raw('watch_editor')), 396, 30), '01_watch_values_in_rhino')
    # 02 the values in the code, the paused line
    save(scaled(raw('values_editor')), '02_values_in_the_code')
    # 03 V Python in the paused frame: the fix drawn in Rhino
    save(window_over(scaled(raw('python_viewport')), scaled(raw('python_editor')), 396, 30), '03_python_in_the_paused_frame')
    # 04 Stop and record: Ctrl+Alt+B on a RunPythonScript run
    save(scaled(raw('stoprecord_editor')), '04_stop_and_record')

    # 05 Which branch failed: the component's menu by it
    base = scaled(raw('failed_gh'))
    menu = scaled(raw('failed_menu'))
    divide = gh.get('failed_gh:Divide', (700, 400, 60, 80))
    mx = min(W - menu.width - 8, round((divide[0] + divide[2]) * S) + 6)
    my = max(8, min(H - menu.height - 8, round(divide[1] * S) - 40))
    save(window_over(base, menu, mx, my, border=(150, 150, 150)), '05_which_branch_failed')
    # 06 The paused run's badge: the component's editor over the canvas
    save(window_over(scaled(raw('paused_gh')), scaled(raw('paused_editor')), 330, 150), '06_paused_run_badge')
    # 07 A script Panel shown as code
    save(scaled(raw('panel_code_gh')), '07_script_panel_as_code')
    # 08 The script Panel edited in place, at the line Ctrl+Alt+B stopped
    save(scaled(raw('panel_stop_gh')), '08_panel_edited_at_the_stop')
    # 09 Markdown Panels: links, a link's tooltip
    base = scaled(raw('markdown_gh'))
    tip_place = gh.get('markdown_tip')
    window = gh.get('markdown_gh_window', (30, 30, 1200, 750))
    if tip_place and os.path.exists(os.path.join(RAW, 'markdown_tip.png')):
        tip = scaled(raw('markdown_tip'))
        base.paste(tip, (round((tip_place[0] - window[0]) * S), round((tip_place[1] - window[1]) * S)))
    save(base, '09_markdown_panel_links')
    # 10 A link followed in Grasshopper: the canvas at Ring, a red circle round it
    save(scaled(raw('link_mark_gh')), '10_link_followed_in_grasshopper')

    # 11 V Edit: its object in Rhino, the component in Grasshopper
    save(window_over(scaled(raw('vedit_viewport')), scaled(raw('vedit_gh')), 14, 170), '11_v_edit')
    # 12 V Text: Panels made Rhino text
    save(window_over(scaled(raw('vtext_viewport')), scaled(raw('vtext_gh')), 386, 170), '12_v_text')

    # 13 Markdown text in Rhino: the hint over its link into Grasshopper
    hint = raw('markdown_hint_viewport')
    vw, vh, px, py = (float(v) for v in open(os.path.join(RAW, 'markdown_hint.txt')).read().split())
    region_w = min(hint.width, 1200)
    region_h = round(region_w / 1.6)
    left = int(min(max(0, px - region_w * 0.35), hint.width - region_w))
    top = int(min(max(0, py - region_h * 0.55), hint.height - region_h))
    save(hint.crop((left, top, left + region_w, top + region_h)).resize((W, H), Image.LANCZOS), '13_markdown_text_link_hint')
    # 14 MarkdownText's dialog for one part: its section in black, the rest gray
    save(window_over(scaled(raw('markdown_viewport')), scaled(raw('markdown_dialog')), 300, 130), '14_markdown_text_dialog')
    # 15 A link to a Rhino object followed: zoomed to it, a red circle round it
    save(scaled(raw('markdown_mark_viewport')), '15_link_followed_in_rhino')

    # 16 The prompts window in Grasshopper: V Claude's answer over the definition it built
    if os.path.exists(os.path.join(RAW, 'prompts_gh.png')) and os.path.exists(os.path.join(RAW, 'prompts_window.png')):
        prompts = scaled(raw('prompts_window'))
        if prompts.height > H - 30:
            prompts = scaled(prompts, (H - 30) / prompts.height)
        save(window_over(scaled(raw('prompts_gh')), prompts, W - prompts.width - 14, H - prompts.height - 14), '16_grasshopper_prompts')


if __name__ == '__main__':
    main()
