#! python 3
"""Captures for miniVivisection's screenshots (tests/live/t_screenshots.py runs them in Rhino,
on the test runner's hidden desktop; tools/compose_screenshots.py makes the pictures).

  window(hwnd)          a top-level window as Windows draws it (PrintWindow, full content):
                        WinForms (Grasshopper), WebView2 (the Script Editor's code), MFC
                        (Rhino's frame) -- not WPF's own drawing, nor OpenGL viewports
  wpf(visual)           a WPF element drawn by WPF (RenderTargetBitmap)
  editor(eto_window)    a Script Editor: its WPF drawn by WPF, its code (WebView2) and title
                        bar by Windows -- one picture, as on screen
  viewport(view, size)  a Rhino view at a size (its own capture, conduits included)
  save(bitmap, name)    PNG in RAW; returns the path
"""
import ctypes
import ctypes.wintypes as wt
import os
import tempfile

import clr
clr.AddReference('PresentationCore')
clr.AddReference('PresentationFramework')
clr.AddReference('WindowsBase')
clr.AddReference('System.Drawing')
import System
import System.Drawing as sd
from System.IO import MemoryStream
from System.Windows.Media import PixelFormats
from System.Windows.Media.Imaging import BitmapFrame, PngBitmapEncoder, RenderTargetBitmap

RAW = os.path.join(tempfile.gettempdir(), 'minivivisection-live', 'shots')
user32 = ctypes.windll.user32
PW_RENDERFULLCONTENT = 2


def save(bitmap, name):
    os.makedirs(RAW, exist_ok=True)
    path = os.path.join(RAW, name + '.png')
    bitmap.Save(path, sd.Imaging.ImageFormat.Png)
    return path


def rect_of(hwnd):
    r = wt.RECT()
    user32.GetWindowRect(ctypes.c_void_p(hwnd), ctypes.byref(r))
    return r.left, r.top, r.right - r.left, r.bottom - r.top


def window(hwnd):
    """The window as Windows draws it, its own size."""
    x, y, w, h = rect_of(hwnd)
    bitmap = sd.Bitmap(max(1, w), max(1, h))
    g = sd.Graphics.FromImage(bitmap)
    hdc = g.GetHdc()
    user32.PrintWindow(ctypes.c_void_p(hwnd), ctypes.c_void_p(hdc.ToInt64()), PW_RENDERFULLCONTENT)
    g.ReleaseHdc(hdc)
    g.Dispose()
    return bitmap


def wpf(visual, scale=1.0):
    """A WPF element as WPF draws it, at a scale (the desktop's: 1.25 for 125 %)."""
    w = int(round(visual.ActualWidth * scale))
    h = int(round(visual.ActualHeight * scale))
    target = RenderTargetBitmap(max(1, w), max(1, h), 96.0 * scale, 96.0 * scale, PixelFormats.Pbgra32)
    target.Render(visual)
    encoder = PngBitmapEncoder()
    encoder.Frames.Add(BitmapFrame.Create(target))
    stream = MemoryStream()
    encoder.Save(stream)
    stream.Position = 0
    return sd.Bitmap(stream)


EnumProc = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)


def children(hwnd):
    """The child windows: (handle, class, x, y, w, h) on screen, visible ones."""
    found = []

    def each(child, _):
        if user32.IsWindowVisible(child):
            name = ctypes.create_unicode_buffer(256)
            user32.GetClassNameW(child, name, 256)
            x, y, w, h = rect_of(child)
            found.append((child, name.value, x, y, w, h))
        return True
    user32.EnumChildWindows(ctypes.c_void_p(hwnd), EnumProc(each), 0)
    return found


def client_origin(hwnd):
    point = wt.POINT(0, 0)
    user32.ClientToScreen(ctypes.c_void_p(hwnd), ctypes.byref(point))
    return point.x, point.y


def editor(eto_window, hwnd):
    """A Script Editor as on screen: Windows' picture (title bar, WebView2), WPF's drawing of
    its client area put over it, the WebView2 areas put back."""
    shot = window(hwnd)
    original = shot.Clone(sd.Rectangle(0, 0, shot.Width, shot.Height), shot.PixelFormat)
    wx, wy, ww, wh = rect_of(hwnd)
    cx, cy = client_origin(hwnd)
    client = wt.RECT()
    user32.GetClientRect(ctypes.c_void_p(hwnd), ctypes.byref(client))
    wpf_window = eto_window.ControlObject
    scale = (client.right - client.left) / max(1.0, wpf_window.ActualWidth)
    content = wpf(wpf_window, scale)
    g = sd.Graphics.FromImage(shot)
    g.DrawImage(content, cx - wx, cy - wy, content.Width, content.Height)
    # The code (WebView2's windows) as Windows drew them
    for child, name, x, y, w, h in children(hwnd):
        if name.startswith('Chrome_WidgetWin') and w > 50 and h > 50:
            area = sd.Rectangle(x - wx, y - wy, w, h)
            g.DrawImage(original, area, area, sd.GraphicsUnit.Pixel)
    g.Dispose()
    return shot


def viewport(view, width, height):
    return view.CaptureToBitmap(sd.Size(width, height))


def control(c):
    """A WinForms control drawn by itself."""
    bitmap = sd.Bitmap(max(1, c.Width), max(1, c.Height))
    c.DrawToBitmap(bitmap, sd.Rectangle(0, 0, c.Width, c.Height))
    return bitmap
