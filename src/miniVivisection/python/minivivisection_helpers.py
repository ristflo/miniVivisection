# miniVivisection's Python helpers, run in the paused script's Python (PythonBridge).
#
# Embedded in the plug-in (EmbeddedResource "minivivisection_helpers.py") and exec'd once into a
# namespace of its own; PythonBridge calls these functions through it. While paused, the
# innermost Python frame is RhinoCode's trace function (trace_begin.py:
# __pynet_tracefunc__(frame, event, arg)); its `frame` is the paused frame.
# Tested with pytest: python/test_minivivisection_helpers.py.

def __minivivisection_paused_frame():
    import sys
    f = sys._getframe(1)
    while f is not None and f.f_code.co_name != '__pynet_tracefunc__':
        f = f.f_back
    if f is None:
        raise RuntimeError("the paused Python frame isn't reachable from here")
    return f.f_locals['frame']

# The file these helpers run as (their frames are not the script's).
__minivivisection_helpers_file = __minivivisection_paused_frame.__code__.co_filename

def __minivivisection_eval(expression):
    frame = __minivivisection_paused_frame()
    return eval(expression, frame.f_globals, frame.f_locals)

def __minivivisection_scope_at(expression, line, filename=None):
    # Where a name written at `line` of the script lives, as Python resolves it there:
    # (globals, locals) of the frame running that code; (globals, None) for a global;
    # None when it is a local of code that isn't running now (or not assigned yet).
    # `filename`: the hovered file when it isn't the paused frame's.
    import os, re, dis, types
    paused = __minivivisection_paused_frame()
    def norm(path):
        try:
            return os.path.normcase(os.path.abspath(path))
        except Exception:
            return path
    want = norm(filename) if filename else norm(paused.f_code.co_filename)
    stack = []
    f = paused
    while f is not None:
        if norm(f.f_code.co_filename) == want:
            stack.append(f)  # innermost first
        f = f.f_back
    if not stack:
        return None
    match = re.match(r'[A-Za-z_]\w*', expression.strip())
    name = match.group(0) if match else None
    module = next((s for s in reversed(stack) if s.f_code.co_name == '<module>'), None)
    g = stack[0].f_globals
    if module is None:
        # No module-level frame of this file: only frames on the stack are known.
        for s in stack:
            if s.f_code.co_firstlineno <= line <= max([l for _, l in dis.findlinestarts(s.f_code) if l is not None] or [0]):
                return (s.f_globals, s.f_locals)
        return (g, None)
    ends = {}
    def last_line(code):
        if code not in ends:
            end = max([l for _, l in dis.findlinestarts(code) if l is not None] or [code.co_firstlineno])
            for c in code.co_consts:
                if isinstance(c, types.CodeType):
                    end = max(end, last_line(c))
            ends[code] = end
        return ends[code]
    path = [module.f_code]
    while True:
        inner = None
        for c in path[-1].co_consts:
            if isinstance(c, types.CodeType) and c.co_firstlineno <= line <= last_line(c):
                inner = c
        if inner is None:
            break
        path.append(inner)
    running = {}
    for s in stack:
        running.setdefault(s.f_code, s)
    for code in reversed(path[1:]):
        frame = running.get(code)
        own = name is not None and (name in code.co_varnames or name in code.co_cellvars)
        free = name is not None and name in code.co_freevars
        if frame is not None:
            if name is None:
                return (frame.f_globals, frame.f_locals)
            if own or free:
                return (frame.f_globals, frame.f_locals) if name in frame.f_locals else None
            return (g, None)
        if own:
            return None
        if not free:
            return (g, None)
    return (g, None)

def __minivivisection_eval_at(expression, line, filename=None):
    scope = __minivivisection_scope_at(expression, line, filename)
    if scope is None:
        raise NameError('not in scope at line ' + str(line) + ' now')
    g, l = scope
    return eval(expression, g, l) if l is not None else eval(expression, g)

def __minivivisection_is_class_at(expression, line, filename=None):
    # Whether expression (a dotted name, as written at line) is a class: what calling it
    # makes is a new value (a constructor), not an action.
    return isinstance(__minivivisection_eval_at(expression, line, filename), type)

def __minivivisection_is_clr(v):
    try:
        import clr, System
        return isinstance(v, System.Object) and not isinstance(v, (bool, int, float, str))
    except Exception:
        return False

def __minivivisection_short(v, depth=0):
    # A value as a short line of code: compact collections with ... +N, objects of the
    # script's classes as Node(x=3, ...). One formatting for tooltips, inline values and
    # their was-texts (compared as text).
    import inspect
    def cut(text, limit):
        return text if len(text) <= limit else text[:limit - 1] + '\u2026'
    if v is None or isinstance(v, (bool, int, float, complex, bytes)):
        return repr(v)
    if isinstance(v, str):
        return repr(cut(v, 60))
    if isinstance(v, (list, tuple, set, frozenset, dict)):
        if depth > 2:
            return '\u2026'
        if isinstance(v, dict):
            items = [__minivivisection_short(k, depth + 1) + ': ' + __minivivisection_short(x, depth + 1) for k, x in list(v.items())[:6]]
            open_, close = '{', '}'
        else:
            items = [__minivivisection_short(x, depth + 1) for x in list(v)[:6]]
            open_, close = ('[', ']') if isinstance(v, list) else ('(', ')') if isinstance(v, tuple) else ('{', '}')
        n = len(v)
        if n > 6:
            items = items[:3] + ['\u2026 +' + str(n - 3)]
        text = ', '.join(items)
        if isinstance(v, tuple) and n == 1:
            text += ','
        return open_ + text + close
    if __minivivisection_is_clr(v):
        text = str(v)
        if type(v).__name__ in ('Point3d', 'Point3f', 'Vector3d', 'Vector3f', 'Point2d'):
            return '(' + text + ')'
        return cut(text, 40)
    if inspect.isfunction(v) or inspect.ismethod(v) or inspect.isbuiltin(v) or inspect.isclass(v) or inspect.ismodule(v):
        return getattr(v, '__name__', '?')
    attributes = getattr(v, '__dict__', None)
    if isinstance(attributes, dict) and depth <= 2:
        parts = [k + '=' + __minivivisection_short(x, depth + 1) for k, x in list(attributes.items())[:4] if not k.startswith('_')]
        if len(attributes) > 4:
            parts.append('\u2026')
        return type(v).__name__ + '(' + ', '.join(parts) + ')'
    return cut(repr(v), 60)

def __minivivisection_value_text(v):
    # [text or None, value]: None for a .NET value, which the plug-in formats (HoverText).
    if __minivivisection_is_clr(v):
        return [None, v]
    try:
        text = __minivivisection_short(v)
    except Exception as e:
        text = '<repr failed: ' + type(e).__name__ + '>'  # one broken __repr__ doesn't take the others along
    return [text if len(text) <= 120 else text[:119] + '\u2026', None]

def __minivivisection_hover_info(expression, line, filename=None):
    # The code's tooltip while paused: [value as a short line of code, key facts, value].
    # No type: the editor's own tooltip (shown below ours) has it. A .NET value comes
    # back as [None, None, value]: the plug-in describes it (HoverText).
    import inspect
    value = __minivivisection_eval_at(expression, line, filename) if line > 0 else __minivivisection_eval(expression)
    if __minivivisection_is_clr(value):
        return [None, None, value]
    if inspect.isfunction(value) or inspect.ismethod(value) or inspect.isbuiltin(value):
        try:
            signature = str(inspect.signature(value))
        except Exception:
            signature = '(...)'
        return ['def ' + getattr(value, '__name__', expression) + signature, (inspect.getdoc(value) or '').split('\n')[0] or None, None]
    if inspect.isclass(value):
        return ['class ' + value.__qualname__, None, None]
    if inspect.ismodule(value):
        return ['import ' + value.__name__, getattr(value, '__file__', None), None]
    facts = None
    if isinstance(value, dict):
        facts = str(len(value)) + (' entry' if len(value) == 1 else ' entries')
    elif isinstance(value, (list, tuple, set, frozenset, range)):
        facts = str(len(value)) + (' item' if len(value) == 1 else ' items')
    elif isinstance(value, str) and len(value) > 20:
        facts = str(len(value)) + ' characters'
    return [__minivivisection_value_text(value)[0], facts, None]

def __minivivisection_inline_values():
    # The paused frame's variables for inline values and was-texts (ValueHistory):
    # [frame key, its first line, the paused line, [[name, text or None, value], ...]].
    # Module level: the script's own globals (no modules, functions, classes, dunders).
    import inspect
    f = __minivivisection_paused_frame()
    code = f.f_code
    module = code.co_name == '<module>'
    names = f.f_globals if module else f.f_locals
    items = []
    for name, value in list(names.items()):
        if name.startswith('__') or inspect.ismodule(value) or inspect.isfunction(value) or inspect.isclass(value) or inspect.isbuiltin(value):
            continue
        items.append([name] + __minivivisection_value_text(value))
        if len(items) >= 300:
            break
    import threading
    key = code.co_filename + '|' + code.co_name + '|' + str(code.co_firstlineno) + '|' + str(threading.get_ident())
    return [key, 1 if module else code.co_firstlineno, f.f_lineno, items]

def __minivivisection_trace_frame():
    import sys
    f = sys._getframe(1)
    while f is not None and f.f_code.co_name != '__pynet_tracefunc__':
        f = f.f_back
    return f

def __minivivisection_tracer_parts():
    # RhinoCode's trace script (trace_begin.py) runs in the script's globals: the
    # functions that make its debugger variables, or None outside a paused script.
    tf = __minivivisection_trace_frame()
    if tf is None:
        return None
    g = tf.f_globals
    parts = (g.get('__pynet_tracevar__'), g.get('__pynet_traceexecvar__'), g.get('__pynet_tracer__'))
    return (tf, parts) if all(p is not None for p in parts) else None

def __minivivisection_variable(parts, name, value):
    tracevar, make, tracer = parts
    found = []
    try:
        if tracevar(tracer, found, name, value, False) and found:
            return found[0]
    except Exception:
        pass
    # what RhinoCode leaves out of its variables: functions, classes, modules
    return make(tracer, False, None, value, name, type(value).__name__, repr(value), False, False)

def __minivivisection_watch_var(expression):
    # A Watch tab row: the variable RhinoCode makes for the expression's value in the
    # paused frame, or for its error. None outside a paused Python script.
    found = __minivivisection_tracer_parts()
    if found is None:
        return None
    tf, parts = found
    frame = tf.f_locals['frame']
    try:
        value = eval(expression, frame.f_globals, frame.f_locals)
    except Exception as e:
        kind = type(e).__name__
        return parts[1](parts[2], False, None, None, expression, kind, kind + ': ' + str(e), False, False)
    return __minivivisection_variable(parts, expression, value)

def __minivivisection_var_for(name, value):
    # The variable RhinoCode makes for a value already evaluated (the V°Watch tab's details).
    found = __minivivisection_tracer_parts()
    return None if found is None else __minivivisection_variable(found[1], name, value)

__minivivisection_python_dir = None  # the plug-in's python folder, set from .NET

def __minivivisection_pipeline():
    import sys
    if __minivivisection_python_dir and __minivivisection_python_dir not in sys.path:
        sys.path.insert(0, __minivivisection_python_dir)
    import minivivisection_visualize
    return minivivisection_visualize

def __minivivisection_visualize(value, interpretation, limit):
    import Rhino.Geometry as rg
    wv = __minivivisection_pipeline()
    how, items = wv.visualize(value, interpretation, rg, limit)
    return [how, wv.describe(how), [k for k, _ in items], [v for _, v in items]]

def __minivivisection_candidates(value):
    wv = __minivivisection_pipeline()
    return [c + '|' + wv.LABELS.get(c, c) for c in wv.candidates(value)]

def __minivivisection_edit_mode(value, interpretation):
    return __minivivisection_pipeline().edit_mode(value, interpretation)

def __minivivisection_write_back(expression, interpretation, geometries):
    frame = __minivivisection_paused_frame()
    value = eval(expression, frame.f_globals, frame.f_locals)
    how, replacement = __minivivisection_pipeline().write_back(value, interpretation, list(geometries))
    if replacement is not None:
        __minivivisection_assign(expression, replacement)
    return how

def __minivivisection_load_visualizers(paths):
    import traceback
    frame = __minivivisection_paused_frame()
    seed = dict(frame.f_globals)
    seed.update(frame.f_locals)
    wv = __minivivisection_pipeline()
    report = []
    for path in paths:
        try:
            report.append('ok|' + path + '|' + ', '.join(wv.load_visualizer_script(path, seed)))
        except Exception as e:
            frames = traceback.extract_tb(e.__traceback__)
            where = next(('%s:%d' % (f.filename, f.lineno) for f in reversed(frames) if f.filename == path), '')
            report.append('error|' + path + '|' + type(e).__name__ + ': ' + str(e) + (' (line ' + where.rsplit(':', 1)[1] + ')' if where else ''))
    return report

def __minivivisection_in_trace():
    return __minivivisection_trace_frame() is not None

__minivivisection_thread_names = ('thread_name', 'thread_id', 'is_main_thread')

def __minivivisection_thread_label(t):
    # A thread as the plug-in shows it: 'main' (Python's main thread: Rhino's UI thread),
    # 'script' (a .NET thread running Python, e.g. an async:true script), else its name.
    import threading
    if t is threading.main_thread():
        return 'main'
    if isinstance(t, threading._DummyThread):
        return 'script'
    return t.name

def __minivivisection_thread_vars():
    import threading
    t = threading.current_thread()
    return {'thread_name': __minivivisection_thread_label(t), 'thread_id': threading.get_ident(), 'is_main_thread': t is threading.main_thread()}

# The Grasshopper run being evaluated (a script component's): gh_run (its RunCount),
# gh_branch ("{0;3}"), gh_item ("{0;3}[2]"); set by the plug-in before each condition and
# logpoint (__minivivisection_set_gh), empty outside Grasshopper.
__minivivisection_gh_names = ('gh_run', 'gh_branch', 'gh_item')
__minivivisection_gh = {}

def __minivivisection_set_gh(run, branch, item):
    global __minivivisection_gh
    __minivivisection_gh = {} if run is None or run < 0 else {'gh_run': run, 'gh_branch': branch, 'gh_item': item}

def __minivivisection_scope_of(frame, text):
    # The frame's names, plus thread_name, thread_id and is_main_thread, and in Grasshopper
    # gh_run, gh_branch and gh_item, when the text uses them (a variable of the script with
    # the same name wins).
    g, l = frame.f_globals, frame.f_locals
    if any(n in text for n in __minivivisection_thread_names):
        l = dict(l)
        for k, v in __minivivisection_thread_vars().items():
            l.setdefault(k, v)
    if __minivivisection_gh and any(n in text for n in __minivivisection_gh_names):
        l = dict(l)
        for k, v in __minivivisection_gh.items():
            l.setdefault(k, v)
    return g, l

def __minivivisection_has_workers():
    # Whether the script started threads of its own (threading.Thread, still running).
    import threading
    return any(t is not threading.main_thread() and not isinstance(t, threading._DummyThread) for t in threading.enumerate())

def __minivivisection_condition(expression):
    # A breakpoint's condition, in the traced frame (inside RhinoCode's trace callback).
    frame = __minivivisection_paused_frame()
    g, l = __minivivisection_scope_of(frame, expression)
    return bool(eval(expression, g, l))

def __minivivisection_logpoint(message, thread_tag=False):
    # A logpoint: {expressions} filled in from the traced frame, printed to the
    # script's own stdout (the editor's Terminal); {{ and }} are braces. thread_tag:
    # [thread] in front while the script runs threads of its own.
    import re, threading
    frame = __minivivisection_paused_frame()
    g, l = __minivivisection_scope_of(frame, message)
    out = []
    for part in re.findall(r'\{\{|\}\}|\{[^{}]+\}|[^{}]+|[{}]', message):
        if part == '{{':
            out.append('{')
        elif part == '}}':
            out.append('}')
        elif len(part) > 2 and part[0] == '{' and part[-1] == '}':
            try:
                out.append(str(eval(part[1:-1], g, l)))
            except Exception as e:
                out.append('<' + type(e).__name__ + ': ' + str(e) + '>')
        else:
            out.append(part)
    text = ''.join(out)
    if thread_tag and __minivivisection_has_workers():
        text = '[' + __minivivisection_thread_label(threading.current_thread()) + '] ' + text
    print(text)
    return text

def __minivivisection_print(text):
    # A line in the script's Terminal (its stdout).
    print(text)

# --- threads -------------------------------------------------------------------------

def __minivivisection_thread_info():
    # The paused thread: [label, ident, number of Python threads running code].
    import sys, threading
    return [__minivivisection_thread_label(threading.current_thread()), threading.get_ident(), len(sys._current_frames())]

def __minivivisection_is_ours(code):
    # Frames of RhinoCode's tracer and of these helpers: not the script's.
    name = code.co_name
    return name.startswith('__pynet_') or name.startswith('__minivivisection_') or code.co_filename == __minivivisection_helpers_file

def __minivivisection_library_dirs():
    import os, threading, sysconfig
    dirs = {os.path.normcase(os.path.dirname(os.path.abspath(threading.__file__)))}
    for key in ('purelib', 'platlib'):
        try:
            dirs.add(os.path.normcase(os.path.abspath(sysconfig.get_paths()[key])))
        except Exception:
            pass
    return dirs

def __minivivisection_is_library(filename, dirs):
    import os
    path = os.path.normcase(os.path.abspath(filename)) if filename and not filename.startswith('<') else ''
    return not path or any(path.startswith(d) for d in dirs)

def __minivivisection_wait_call(filename, line):
    # A thread waiting in C code has no frame there: the line it waits at says what it
    # waits for (lock.acquire(), t.join(), event.wait(), time.sleep(), with lock:).
    import linecache, re
    text = linecache.getline(filename, line).strip()
    match = re.search(r'\.(acquire|join|wait|get|sleep|result)\s*\(', text)
    if match:
        return match.group(1) + '()'
    if re.match(r'with\s', text):
        return 'with'
    return None

def __minivivisection_threads(paused_here):
    # Every Python thread that runs code: [[ident, label, is main, is daemon, state,
    # [[file, line, function, is the script's], ...] innermost first], ...]. States:
    # 'paused here', 'stopped by the debugger' (at its next line, while another thread is
    # paused), 'waiting in <call>', 'in library code (<function>)', 'running'.
    # paused_here: called on the paused thread (its frames start at the paused frame);
    # else the calling thread is left out.
    import sys, threading
    me = threading.get_ident()
    by_ident = {t.ident: t for t in threading.enumerate()}
    dirs = __minivivisection_library_dirs()
    rows = []
    for ident, top in sys._current_frames().items():
        if ident == me and paused_here:
            top = __minivivisection_paused_frame()
        elif ident == me:
            continue
        stopped = False
        frames = []
        f = top
        while f is not None and len(frames) < 40:
            if __minivivisection_is_ours(f.f_code):
                stopped = stopped or f.f_code.co_name == '__pynet_tracefunc__'
            else:
                frames.append([f.f_code.co_filename, f.f_lineno, f.f_code.co_name, not __minivivisection_is_library(f.f_code.co_filename, dirs)])
            f = f.f_back
        if not frames:
            continue
        # What it does, from its innermost frame of the script (library frames above it:
        # the call it is in, e.g. threading's wait).
        own = next((fr for fr in frames if fr[3]), None)
        waits = __minivivisection_wait_call(own[0], own[1]) if own else None
        if ident == me:
            state = 'paused here'
        elif stopped:
            state = 'stopped by the debugger'
        elif waits:
            state = 'waiting in ' + waits
        elif not frames[0][3]:
            state = 'in library code (' + frames[0][2] + ')'
        else:
            state = 'running'
        t = by_ident.get(ident)
        label = __minivivisection_thread_label(t) if t is not None else 'thread ' + str(ident)
        rows.append([ident, label, t is threading.main_thread(), bool(t is not None and t.daemon), state, frames])
    rows.sort(key=lambda r: (not r[2], r[1]))
    return rows

def __minivivisection_stop_script(path, stop, ident=0):
    # A script running without the debugger (RunPythonScript, a Grasshopper component): where
    # it is now -- [ident, file, line, function, [[file, line, function], ...] innermost first,
    # [[name, value text], ...] of that frame] -- and, with stop, a KeyboardInterrupt raised
    # in that thread (at its next bytecode; a long call into native code finishes first).
    # ident: only that thread (the run's own, when known: the UI thread's); 0: the thread with
    # the script file on its stack. path '*': the innermost frame of code that isn't a
    # library's (a Grasshopper component's code has no file of its own) -- with an ident only,
    # so another script's thread is never taken. None: no such thread runs it.
    import sys, os, threading
    me = threading.get_ident()
    def norm(p):
        try:
            return os.path.normcase(os.path.abspath(p))
        except Exception:
            return p
    def stack_of(top):
        stack = []
        f = top
        while f is not None and len(stack) < 60:
            if not __minivivisection_is_ours(f.f_code):
                stack.append(f)
            f = f.f_back
        return stack
    frames = sys._current_frames()
    candidates = [(i, t) for i, t in frames.items() if i != me and (not ident or i == ident)]
    best = None
    if path == '*':
        if ident:
            dirs = __minivivisection_library_dirs()
            for i, top in candidates:
                stack = stack_of(top)
                own = [fr for fr in stack if not __minivivisection_is_library(fr.f_code.co_filename, dirs)]
                if own:
                    best = (i, own[0], stack)
    else:
        want = norm(path)
        base = os.path.basename(want)
        loose_hits = []
        for i, top in candidates:
            stack = stack_of(top)
            exact = [fr for fr in stack if norm(fr.f_code.co_filename) == want]
            if exact:
                best = (i, exact[0], stack)
                break
            loose = [fr for fr in stack if os.path.basename(norm(fr.f_code.co_filename)) == base]
            if loose:
                loose_hits.append((i, loose[0], stack))
        if best is None and len(loose_hits) == 1:
            best = loose_hits[0]  # by file name only when one thread has it
    if best is None:
        return None
    found, frame, stack = best
    values = []
    for name, value in list(frame.f_locals.items()):
        if name.startswith('__') or callable(value) or type(value).__name__ == 'module':
            continue
        try:
            text = __minivivisection_short(value)
        except Exception:
            text = '?'
        values.append([name, text])
        if len(values) >= 25:
            break
    row = [found, frame.f_code.co_filename, frame.f_lineno, frame.f_code.co_name,
           [[fr.f_code.co_filename, fr.f_lineno, fr.f_code.co_name] for fr in stack], values]
    if stop and not __minivivisection_interrupt(found):
        return None
    return row

def __minivivisection_interrupt(ident):
    # KeyboardInterrupt raised in thread ident at its next bytecode; False: no such thread.
    import ctypes
    hit = ctypes.pythonapi.PyThreadState_SetAsyncExc(ctypes.c_ulong(ident), ctypes.py_object(KeyboardInterrupt))
    if hit > 1:
        ctypes.pythonapi.PyThreadState_SetAsyncExc(ctypes.c_ulong(ident), None)  # never more than one
        return False
    return hit == 1

def __minivivisection_eval_in_threads(expression):
    # The expression in each thread's innermost frame of the script, read only:
    # [[label, ident, is the paused thread, ok, value or error text], ...]. On the paused thread.
    import sys, threading
    me = threading.get_ident()
    by_ident = {t.ident: t for t in threading.enumerate()}
    dirs = __minivivisection_library_dirs()
    out = []
    for ident, top in sys._current_frames().items():
        f = __minivivisection_paused_frame() if ident == me else top
        while f is not None and (__minivivisection_is_ours(f.f_code) or __minivivisection_is_library(f.f_code.co_filename, dirs)):
            f = f.f_back
        if f is None:
            continue
        t = by_ident.get(ident)
        label = __minivivisection_thread_label(t) if t is not None else 'thread ' + str(ident)
        try:
            out.append([label, ident, ident == me, True, eval(expression, f.f_globals, f.f_locals)])
        except Exception as e:
            out.append([label, ident, ident == me, False, type(e).__name__ + ': ' + str(e)])
    out.sort(key=lambda r: (not r[2], r[0]))
    return out

def __minivivisection_eval_session(expression):
    return eval(expression, __minivivisection_session)

def __minivivisection_items(value, limit):
    if isinstance(value, (list, tuple, set, frozenset)):
        return list(value)[:limit]
    return None

__minivivisection_session = {'__name__': '__terminal__'}
__minivivisection_host = None  # TerminalHost, set from .NET
__minivivisection_labels = {}
__minivivisection_command_names = ('show', 'hide', 'clear', 'bake', 'watch', 'edit', 'visualizers', 'reload_visualizers', 'breakpoints')

def __minivivisection_prepare_labels(source):
    # show(crv, curves[0]) labels its values with what was typed: the source of each
    # argument, call by call in source order.
    import ast
    __minivivisection_labels.clear()
    try:
        tree = ast.parse(source)
    except SyntaxError:
        return
    calls = [n for n in ast.walk(tree) if isinstance(n, ast.Call) and isinstance(n.func, ast.Name) and n.func.id in ('show', 'bake')]
    calls.sort(key=lambda n: (n.lineno, n.col_offset))
    for call in calls:
        entry = __minivivisection_labels.setdefault(call.func.id, {'calls': [], 'n': 0})
        entry['calls'].append([ast.get_source_segment(source, a) or '?' for a in call.args])

def __minivivisection_next_labels(kind, count, name):
    # The n-th call gets the n-th call's argument texts; calls beyond the last one (a
    # loop) reuse the last ones, numbered: c, c #2, c #3.
    entry = __minivivisection_labels.setdefault(kind, {'calls': [], 'n': 0})
    n = entry['n']
    entry['n'] = n + 1
    if name:
        labels = [name] if count == 1 else ['%s[%d]' % (name, i) for i in range(count)]
    elif entry['calls']:
        labels = entry['calls'][min(n, len(entry['calls']) - 1)]
    else:
        labels = []
    if len(labels) != count:
        labels = ['%s %d' % (kind, i + 1) for i in range(count)]
    if n >= max(1, len(entry['calls'])):
        labels = ['%s #%d' % (label, n - len(entry['calls']) + 2) for label in labels]
    return labels

def show(*values, name=None, as_=None):
    'show(value, ...): draws the values in the Rhino viewports until hide(name) or clear(); curves, points, vectors, planes, breps, meshes and lists of them; name= sets the label; as_= an interpretation of raw data (points, polyline, polyline_closed, curve, lines, anchored_vectors, triangles, vectors).'
    for label, value in zip(__minivivisection_next_labels('show', len(values), name), values):
        print(__minivivisection_host.Show(label, value, as_ or 'auto'))

def hide(name):
    'hide(name): removes a value drawn with show(), by its label.'
    print(__minivivisection_host.Hide(str(name)))

def clear():
    'clear(): removes everything drawn with show().'
    print(__minivivisection_host.Clear())

def bake(*values, name=None):
    'bake(value, ...): adds copies of the values to the Rhino document, on layer Script Editor Watch::Baked.'
    for label, value in zip(__minivivisection_next_labels('bake', len(values), name), values):
        print(__minivivisection_host.Bake(label, value))

def watch(expression):
    'watch(expression): adds the expression (a string) to the V°Watch tab of the Script Editor; drawn at every pause.'
    print(__minivivisection_host.Watch(str(expression)))

def visualizers():
    'visualizers(): the registered @rhinoviv.visualize / @rhinoviv.inverse functions.'
    for line in __minivivisection_pipeline().registered():
        print(line)

def breakpoints():
    'breakpoints(): the conditions, hit counts and log messages of the script\'s breakpoints, with this run\'s hits.'
    print(__minivivisection_host.Breakpoints())

def __minivivisection_syntax_error(expression):
    try:
        compile(expression, '<condition>', 'eval')
        return None
    except SyntaxError as e:
        return (e.msg or 'invalid syntax') + (' at column ' + str(e.offset) if e.offset else '')

def reload_visualizers():
    'reload_visualizers(): runs the visualizer scripts of the Options page again (Tools > Options > MiniVivisection).'
    print(__minivivisection_host.ReloadVisualizers())

def edit(expression):
    'edit(expression): turns the value of the expression (a string) into Rhino objects; changes to them go back into the script.'
    print(__minivivisection_host.Edit(str(expression)))

import contextlib as __minivivisection_contextlib

import threading as __minivivisection_threading
__minivivisection_commands_lock = __minivivisection_threading.Lock()
__minivivisection_commands_state = {'depth': 0, 'saved': {}}

@__minivivisection_contextlib.contextmanager
def __minivivisection_commands(source):
    # The commands, in builtins while an input runs: not in the script's namespace,
    # and a variable of the script with the same name wins. (A function, not a class:
    # a class body would mangle the __minivivisection_ names.) Inputs may overlap (two paused
    # editors, Claude's run_python and the Python tab): counted under a lock -- the
    # first one in saves what builtins had, the last one out puts it back, so the
    # commands never stay in builtins.
    import builtins
    __minivivisection_prepare_labels(source)
    with __minivivisection_commands_lock:
        state = __minivivisection_commands_state
        if state['depth'] == 0:
            state['saved'] = {n: getattr(builtins, n) for n in __minivivisection_command_names if hasattr(builtins, n)}
            for n in __minivivisection_command_names:
                setattr(builtins, n, globals()[n])
        state['depth'] += 1
    try:
        yield
    finally:
        with __minivivisection_commands_lock:
            state['depth'] -= 1
            if state['depth'] == 0:
                for n in __minivivisection_command_names:
                    if n in state['saved']:
                        setattr(builtins, n, state['saved'][n])
                    elif hasattr(builtins, n):
                        delattr(builtins, n)
                state['saved'] = {}

def __minivivisection_exec(source, in_frame):
    import sys, io, ast, traceback
    out = io.StringIO()
    try:
        tree = ast.parse(source, '<input>', 'exec')
    except SyntaxError:
        return ''.join(traceback.format_exception_only(*sys.exc_info()[:2]))
    last = None
    if tree.body and isinstance(tree.body[-1], ast.Expr):
        last = ast.Expression(tree.body.pop().value)
    frame = __minivivisection_paused_frame() if in_frame else None
    if frame is not None:
        g, l = frame.f_globals, frame.f_locals
    else:
        g = l = __minivivisection_session
    saved = sys.stdout, sys.stderr
    sys.stdout = sys.stderr = out
    try:
        with __minivivisection_commands(source):
            exec(compile(tree, '<input>', 'exec'), g, l)
            if last is not None:
                value = eval(compile(last, '<input>', 'eval'), g, l)
                if value is not None:
                    print(repr(value))
    except BaseException:
        kind, value, tb = sys.exc_info()
        traceback.print_exception(kind, value, tb.tb_next, file=out)
    finally:
        sys.stdout, sys.stderr = saved
        if frame is not None and l is not g:
            import ctypes
            ctypes.pythonapi.PyFrame_LocalsToFast(ctypes.py_object(frame), ctypes.c_int(0))
    return out.getvalue()

def __minivivisection_frame_key():
    # Which frame the script is paused in -- file, function, first line, thread -- so an
    # edit started here is written back only here (not as a new name in another frame).
    import threading
    code = __minivivisection_paused_frame().f_code
    return code.co_filename + '|' + code.co_name + '|' + str(code.co_firstlineno) + '|' + str(threading.get_ident())

def __minivivisection_assign(target, value):
    frame = __minivivisection_paused_frame()
    g, l = frame.f_globals, frame.f_locals
    l['__minivivisection_value'] = value
    try:
        exec(target + ' = __minivivisection_value', g, l)
    finally:
        l.pop('__minivivisection_value', None)
        if l is not g:
            import ctypes
            ctypes.pythonapi.PyFrame_LocalsToFast(ctypes.py_object(frame), ctypes.c_int(0))

def __minivivisection_run(source, in_frame):
    import sys, io, codeop, traceback
    try:
        code = codeop.compile_command(source, '<terminal>', 'single')
    except (OverflowError, SyntaxError, ValueError):
        return (False, ''.join(traceback.format_exception_only(*sys.exc_info()[:2])))
    if code is None:
        return (True, '')
    frame = __minivivisection_paused_frame() if in_frame else None
    if frame is not None:
        g, l = frame.f_globals, frame.f_locals
    else:
        g = l = __minivivisection_session
    out = io.StringIO()
    saved = sys.stdout, sys.stderr
    sys.stdout = sys.stderr = out
    try:
        with __minivivisection_commands(source):
            exec(code, g, l)
    except SystemExit:
        out.write('SystemExit ignored\n')
    except BaseException:
        kind, value, tb = sys.exc_info()
        traceback.print_exception(kind, value, tb.tb_next, file=out)
    finally:
        sys.stdout, sys.stderr = saved
        if frame is not None and l is not g:
            import ctypes
            ctypes.pythonapi.PyFrame_LocalsToFast(ctypes.py_object(frame), ctypes.c_int(0))
    return (False, out.getvalue())
