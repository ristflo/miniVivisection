#! python 3
# Helpers for miniVivisection's live tests: Python 3 scripts that run inside a fresh
# Rhino (started by run_live.py with /runscript), drive the plug-in through .NET
# reflection, and write what they found to a JSON file the runner reads.
#
# A test is a generator: it runs on Rhino's UI thread from a timer, one step at a time,
# so Rhino's message loop keeps going in between. It yields
#   - a number: wait that many seconds;
#   - a function: wait until it returns true (up to `timeout` seconds, see wait_for).
# check(name, actual, expected) records a check; the run ends when the generator returns
# (or raises), and the result is written then.
import json
import os
import sys
import time
import traceback

import System
import Rhino
import Eto.Forms

ANY = (System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
       System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance)

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))  # the repository
if os.path.join(ROOT, 'tools') not in sys.path:
    sys.path.insert(0, os.path.join(ROOT, 'tools'))  # the generators the tests run (make_gh_examples, ...)

RESULT_DIR = os.path.join(os.environ.get('TEMP', '.'), 'minivivisection-live')
_checks = []


_progress = [None]


def progress(text):
    """One line in <test>.progress at once: the runner shows how far a test got (a freeze)."""
    if _progress[0] is None:
        return
    with open(_progress[0], 'a', encoding='utf-8') as f:
        f.write(time.strftime('%H:%M:%S ') + text + '\n')


def check(name, actual, expected):
    ok = actual == expected
    _checks.append({'name': name, 'ok': ok, 'actual': repr(actual), 'expected': repr(expected)})
    progress('%s %s (got %r, want %r)' % ('ok  ' if ok else 'FAIL', name, actual, expected))
    return ok


def note(text):
    _checks.append({'name': 'note', 'ok': True, 'actual': text, 'expected': ''})
    progress(text)


# --- the plug-in, by reflection ----------------------------------------------------------

def assembly(name):
    for a in System.AppDomain.CurrentDomain.GetAssemblies():
        if a.GetName().Name == name:
            return a
    return None


def plugin_type(name):
    asm = assembly('miniVivisection')
    if asm is None:
        raise RuntimeError('miniVivisection.rhp is not loaded')
    t = asm.GetType('MiniVivisection.' + name)
    if t is None:
        raise RuntimeError('no type ' + name)
    return t


def static_call(type_name, method, *args):
    t = plugin_type(type_name)
    candidates = [m for m in t.GetMethods(ANY) if m.Name == method and len(m.GetParameters()) == len(args)]
    if not candidates:
        raise RuntimeError('no method %s.%s/%d' % (type_name, method, len(args)))
    return candidates[0].Invoke(None, list(args))


def static_get(type_name, member):
    t = plugin_type(type_name)
    p = t.GetProperty(member, ANY)
    if p is not None:
        return p.GetValue(None)
    f = t.GetField(member, ANY)
    if f is None:
        raise RuntimeError('no member %s.%s' % (type_name, member))
    return f.GetValue(None)


def member(obj, name):
    t = obj.GetType()
    while t is not None:
        p = t.GetProperty(name, ANY)
        if p is not None:
            return p.GetValue(obj)
        f = t.GetField(name, ANY)
        if f is not None:
            return f.GetValue(obj)
        t = t.BaseType
    raise RuntimeError('no member %s on %s' % (name, obj.GetType().FullName))


def hook():
    """The plug-in's ScriptEditorHook."""
    return member(static_get('MiniVivisectionPlugin', 'Instance'), 'Hook')


def editors():
    """The open Script Editor windows (RhinoCodeEditor.Editor.RCE)."""
    return [w for w in Eto.Forms.Application.Instance.Windows if w.GetType().FullName == 'RhinoCodeEditor.Editor.RCE']


def window_handle(window):
    """The Win32 handle of an Eto/WPF window."""
    wpf = window.ControlObject
    helper = System.Type.GetType('System.Windows.Interop.WindowInteropHelper, PresentationFramework')
    return int(System.Activator.CreateInstance(helper, wpf).Handle.ToInt64())


def is_cloaked(window):
    """Whether DWM keeps the window invisible (DWMWA_CLOAKED)."""
    import ctypes
    value = ctypes.c_int(0)
    ctypes.windll.dwmapi.DwmGetWindowAttribute(ctypes.c_void_p(window_handle(window)), 14, ctypes.byref(value), 4)
    return value.value != 0


def write_file(name, text):
    folder = os.path.join(RESULT_DIR, 'scripts')
    os.makedirs(folder, exist_ok=True)
    path = os.path.join(folder, name)
    with open(path, 'w', encoding='utf-8') as f:
        f.write(text)
    return path


# --- the run --------------------------------------------------------------------------------

class _Runner(object):
    def __init__(self, name, gen):
        self.name = name
        self.gen = gen
        self.wait_until = 0.0
        self.cond = None
        self.cond_deadline = 0.0
        self.busy = False
        self.done = False
        self.timer = Eto.Forms.UITimer()
        self.timer.Interval = 0.05
        self.timer.Elapsed += self.tick
        self.timer.Start()

    def tick(self, sender, e):
        if self.busy or self.done:
            return  # a step that pumps messages (a nested tick) doesn't re-enter
        now = time.time()
        if now < self.wait_until:
            return
        if self.cond is not None:
            try:
                ready = self.cond()
            except Exception:
                ready = False
            if not ready and now < self.cond_deadline:
                return
            if not ready:
                check('waited for: ' + getattr(self.cond, '__name__', 'condition'), 'timeout', 'ready')
            self.cond = None
        self.busy = True
        try:
            step = next(self.gen)
            if callable(step):
                self.cond = step
                self.cond_deadline = time.time() + getattr(step, 'timeout', 30)
            else:
                self.wait_until = time.time() + float(step or 0)
        except StopIteration:
            self.finish(None)
        except Exception:
            self.finish(traceback.format_exc())
        finally:
            self.busy = False

    def finish(self, error):
        self.done = True
        self.timer.Stop()
        if error:
            _checks.append({'name': 'test raised', 'ok': False, 'actual': error, 'expected': ''})
            print(error)
        os.makedirs(RESULT_DIR, exist_ok=True)
        result = {'test': self.name, 'ok': all(c['ok'] for c in _checks) and len(_checks) > 0, 'checks': _checks}
        with open(os.path.join(RESULT_DIR, self.name + '.json'), 'w', encoding='utf-8') as f:
            json.dump(result, f, indent=2)
        print('[live] %s: %s' % (self.name, 'PASSED' if result['ok'] else 'FAILED'))


_runners = []


def wait_for(condition, timeout=30):
    condition.timeout = timeout
    return condition


def run(name, test):
    """Starts test() (a generator) on Rhino's UI thread; returns at once."""
    os.makedirs(RESULT_DIR, exist_ok=True)
    _progress[0] = os.path.join(RESULT_DIR, name + '.progress')
    progress('started')
    _runners.append(_Runner(name, test()))
