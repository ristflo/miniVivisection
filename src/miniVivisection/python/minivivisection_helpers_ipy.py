# -*- coding: utf-8 -*-
# miniVivisection's helpers for IronPython 2 scripts (IronPythonBridge.cs): compiled once into
# a module of their own inside Rhino's IronPython engine. Each takes the paused frame -- an
# IronPython TraceBackFrame: f_globals, f_locals (a function's locals go through the DLR
# debugger's scope, so assignments reach the running function), f_code -- and works in its
# globals and locals. Python 2.7 syntax: this file never runs in Python 3.
import re
import sys
import traceback

_VALUE = '__minivivisection_value'


def mv_eval(frame, expression):
    return eval(expression, frame.f_globals, frame.f_locals)


def mv_assign(frame, target, value):
    # target = value: any assignable target (crv, curves[0], node.x); the value comes in
    # through a global that is removed again.
    g, l = frame.f_globals, frame.f_locals
    g[_VALUE] = value
    try:
        exec (target + ' = ' + _VALUE) in g, l
    finally:
        g.pop(_VALUE, None)


def _with_extras(frame, extras, work):
    # gh_run, gh_branch, gh_item (a Grasshopper component's run) as names a condition can
    # use where the script has no such name; gone afterwards.
    g, l = frame.f_globals, frame.f_locals
    added = []
    if extras is not None:
        for key in list(extras.keys()):
            if key not in g and key not in l:
                g[key] = extras[key]
                added.append(key)
    try:
        return work(g, l)
    finally:
        for key in added:
            g.pop(key, None)


def mv_condition(frame, expression, extras=None):
    return _with_extras(frame, extras, lambda g, l: bool(eval(expression, g, l)))


def mv_logpoint(frame, message, extras=None):
    # {expressions} filled in; {{ and }} are braces.
    def fill(g, l):
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
        return ''.join(out)
    return _with_extras(frame, extras, fill)


class _Capture(object):
    def __init__(self):
        self.parts = []

    def write(self, text):
        self.parts.append(text)

    def flush(self):
        pass


def _captured(work):
    # work(out), with the script's sys.stdout and sys.stderr going to out meanwhile.
    out = _Capture()
    saved = sys.stdout, sys.stderr
    sys.stdout = sys.stderr = out
    try:
        work(out)
    finally:
        sys.stdout, sys.stderr = saved
    return ''.join(out.parts)


def _report(out):
    # The exception being handled, without the helper's own frame.
    kind, value, tb = sys.exc_info()
    traceback.print_exception(kind, value, tb.tb_next if tb is not None else None, file=out)


def _split_last_expression(source):
    # (statements, expression): a final expression statement is printed, like a notebook
    # cell's. Found from the text: the last top-level statement, when it compiles as an
    # expression; else everything is statements.
    lines = source.replace('\r\n', '\n').split('\n')
    start = None
    for i in range(len(lines) - 1, -1, -1):
        text = lines[i]
        if text.strip() == '' or text.lstrip().startswith('#') or text[:1] in (' ', '\t', ')', ']', '}'):
            continue
        start = i
        break
    if start is None:
        return source, None
    tail = '\n'.join(lines[start:]).strip()
    try:
        compile(tail, '<input>', 'eval')
    except SyntaxError:
        return source, None
    return '\n'.join(lines[:start]), tail


def mv_exec(frame, source):
    # A cell: statements run; a final expression's value is printed. Returns the output.
    g, l = frame.f_globals, frame.f_locals
    body, last = _split_last_expression(source)

    def run(out):
        try:
            if body.strip():
                exec compile(body, '<input>', 'exec') in g, l
            if last is not None:
                value = eval(compile(last, '<input>', 'eval'), g, l)
                if value is not None:
                    out.write(repr(value) + '\n')
        except SystemExit:
            out.write('SystemExit ignored\n')
        except BaseException:
            _report(out)
    return _captured(run)


def _compile_command(source):
    # Python's own prompt rule: None while the input isn't complete.
    try:
        import codeop
    except ImportError:
        codeop = None
    if codeop is not None:
        return codeop.compile_command(source, '<terminal>', 'single')
    try:
        return compile(source + '\n', '<terminal>', 'single')
    except SyntaxError as e:
        if 'EOF' in str(e) or source.rstrip().endswith((':', '\\')):
            return None
        raise


def mv_run(frame, source):
    # One prompt input, Python prompt style ('single': expression results are printed):
    # (more, output) -- more: the block isn't finished yet.
    try:
        code = _compile_command(source)
    except (OverflowError, SyntaxError, ValueError):
        return (False, ''.join(traceback.format_exception_only(*sys.exc_info()[:2])))
    if code is None:
        return (True, '')
    g, l = frame.f_globals, frame.f_locals

    def run(out):
        try:
            exec code in g, l
        except SystemExit:
            out.write('SystemExit ignored\n')
        except BaseException:
            _report(out)
    return (False, _captured(run))


def mv_frame_key(frame):
    # Which frame the script is paused in -- file, function, first line -- so an edit
    # started here is written back only here (EditSession.OtherFrame).
    code = frame.f_code
    return '%s|%s|%s' % (code.co_filename, code.co_name, code.co_firstlineno)


# Not variables: modules, functions, classes, .NET namespaces (as RhinoCode's own list leaves them out).
_SKIPPED_TYPES = ('module', 'function', 'builtin_function_or_method', 'type', 'classobj', 'instancemethod', 'namespace#')
_NATIVE_TYPES = (int, long, float, bool, str, unicode, list, tuple, dict, set, frozenset, type(None))


def mv_inline_values(frame):
    # The frame's variables for the values at the ends of the lines and "was" (InlineValues):
    # (key, the code's first line, the paused line, [(name, text or None, value)]) -- a .NET
    # value's text is None, the plug-in formats it.
    code = frame.f_code
    variables = frame.f_locals  # module code: its globals (as IronPython's frame gives them)
    if variables is None:
        variables = frame.f_globals
    items = []
    for name, value in list(variables.items()):
        if not isinstance(name, basestring) or name.startswith('__') or name.startswith('$'):
            continue
        if type(value).__name__ in _SKIPPED_TYPES:
            continue
        items.append((name, repr(value) if isinstance(value, _NATIVE_TYPES) else None, value))
    return (mv_frame_key(frame), max(1, code.co_firstlineno), frame.f_lineno, items)


def mv_syntax_error(expression):
    try:
        compile(expression, '<condition>', 'eval')
        return None
    except SyntaxError as e:
        return (e.msg or 'invalid syntax') + (' at column %s' % e.offset if e.offset else '')
