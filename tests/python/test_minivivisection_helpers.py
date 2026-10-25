"""Tests for minivivisection_helpers.py (the plug-in's embedded Python helpers), outside Rhino.

The helpers run in the paused script's Python: they find the paused frame through
RhinoCode's trace function, whose code is named __pynet_tracefunc__ and whose local
`frame` is the paused frame. The tests pause a small script the same way. .NET values
(pythonnet) aren't available here; those paths are covered by the checks project.

    PYTEST_DISABLE_PLUGIN_AUTOLOAD=1 python -m pytest python/test_minivivisection_helpers.py
"""
import os
import sys

import pytest

HELPERS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "src", "miniVivisection", "python", "minivivisection_helpers.py")


@pytest.fixture(scope="module")
def h():
    namespace = {"__name__": "minivivisection_helpers"}
    exec(compile(open(HELPERS, encoding="utf-8").read(), HELPERS, "exec"), namespace)
    return namespace


SCRIPT = """
import sys

class Node:
    def __init__(self):
        self.x = 3
        self.label = 'n1'

crv = 'global curve'          # global line
count = 5

def helper():
    crv = 'helper local'      # helper line
    return crv

def target():
    crv = 7                   # assign line
    total = 0
    for i in range(3):
        total += i            # loop line
    later = 1                 # pause line
    return total

def __pynet_tracefunc__(frame, event, arg):
    if frame.f_code is target.__code__ and event == 'line' and frame.f_lineno == PAUSE:
        callback()
    return __pynet_tracefunc__

sys.settrace(__pynet_tracefunc__)
try:
    target()
finally:
    sys.settrace(None)
"""


def line_of(marker):
    return next(i for i, text in enumerate(SCRIPT.split("\n"), start=1) if "# " + marker in text)


def paused(callback, filename="script.py"):
    """Runs SCRIPT, pausing once at the pause line: callback() runs inside the trace function."""
    namespace = {"callback": callback, "PAUSE": line_of("pause line")}
    exec(compile(SCRIPT, filename, "exec"), namespace)


def test_short_formats_values_compactly(h):
    short = h["__minivivisection_short"]
    assert short(5) == "5"
    assert short("abc") == "'abc'"
    assert short([1, 2, 3]) == "[1, 2, 3]"
    assert short(list(range(250))) == "[0, 1, 2, … +247]"
    assert short((1,)) == "(1,)"
    assert short({"a": 1}) == "{'a': 1}"
    assert short([[[[1]]]]) == "[[[…]]]"

    class Node:
        def __init__(self):
            self.x = 3
            self.label = "n1"
            self._private = 1

    assert short(Node()) == "Node(x=3, label='n1')"


def test_value_text_cuts_long_values(h):
    text, value = h["__minivivisection_value_text"]("x" * 300)
    assert value is None
    assert len(text) <= 120 and "…" in text


def test_scope_follows_python_at_the_hovered_line(h):
    results = {}

    def at_pause():
        eval_at = h["__minivivisection_eval_at"]
        results["global at module level"] = eval_at("crv", line_of("global line"))
        results["local in the paused function"] = eval_at("crv", line_of("assign line"))
        results["global read in the paused function"] = eval_at("count", line_of("assign line"))
        try:
            eval_at("crv", line_of("helper line"))
            results["local of a function not running"] = "no error"
        except NameError:
            results["local of a function not running"] = "not in scope"

    paused(at_pause)
    assert results == {
        "global at module level": "global curve",
        "local in the paused function": 7,
        "global read in the paused function": 5,
        "local of a function not running": "not in scope",
    }


def test_condition_and_logpoint_in_the_paused_frame(h, capsys):
    results = {}

    def at_pause():
        results["condition"] = h["__minivivisection_condition"]("total == 3 and crv == 7")
        results["logpoint"] = h["__minivivisection_logpoint"]("total={total} {{braces}} bad={nope}")

    paused(at_pause)
    assert results["condition"] is True
    assert results["logpoint"] == "total=3 {braces} bad=<NameError: name 'nope' is not defined>"
    assert "total=3" in capsys.readouterr().out


def test_inline_values_list_the_paused_frames_variables(h):
    results = {}

    def at_pause():
        key, first, current, items = h["__minivivisection_inline_values"]()
        results.update(first=first, current=current, names={name: text for name, text, _ in items})

    paused(at_pause)
    assert results["current"] == line_of("pause line")
    assert results["first"] == line_of("assign line") - 1  # the def line
    assert results["names"] == {"crv": "7", "total": "3", "i": "2"}


def test_hover_info_names_functions_and_collections(h):
    results = {}

    def at_pause():
        info = h["__minivivisection_hover_info"]
        results["total"] = info("total", line_of("loop line"))
        results["target"] = info("target", line_of("loop line"))

    paused(at_pause)
    assert results["total"] == ["3", None, None]
    assert results["target"][0] == "def target()"


def test_syntax_error(h):
    assert h["__minivivisection_syntax_error"]("i > 1 and x.y") is None
    assert h["__minivivisection_syntax_error"]("i >") is not None


def test_no_paused_frame_outside_the_trace_function(h):
    with pytest.raises(RuntimeError):
        h["__minivivisection_paused_frame"]()


THREADED = """
import sys, threading

ready = threading.Event()
go = threading.Event()
lock = threading.Lock()

def waiter():
    n = 5
    ready.set()
    go.wait()                 # waiter line
    return n

def locker():
    n = 7
    with lock:                # locker line
        return n

def target():
    worker = threading.Thread(target=waiter, name='Worker-1')
    worker.start()
    ready.wait()
    lock.acquire()
    other = threading.Thread(target=locker, name='Worker-2')
    other.start()
    while not any(f.f_code.co_name == 'locker' for f in sys._current_frames().values()):
        pass
    n = 1
    later = 1                 # pause line
    lock.release()
    go.set()
    worker.join()
    other.join()

def __pynet_tracefunc__(frame, event, arg):
    if frame.f_code is target.__code__ and event == 'line' and frame.f_lineno == PAUSE:
        callback()
    return __pynet_tracefunc__

sys.settrace(__pynet_tracefunc__)
try:
    target()
finally:
    sys.settrace(None)
"""


def paused_threaded(callback, tmp_path):
    """THREADED from a file (the waiting lines are read back), paused once in its main thread."""
    path = tmp_path / "threaded.py"
    path.write_text(THREADED, encoding="utf-8")
    pause = next(i for i, text in enumerate(THREADED.split("\n"), start=1) if "# pause line" in text)
    exec(compile(THREADED, str(path), "exec"), {"callback": callback, "PAUSE": pause})


def test_thread_names_in_conditions_and_logpoints(h, tmp_path, capsys):
    results = {}

    def at_pause():
        results["condition"] = h["__minivivisection_condition"]("is_main_thread and thread_name == 'main' and n == 1")
        results["tagged"] = h["__minivivisection_logpoint"]("n={n}", True)
        results["untagged"] = h["__minivivisection_logpoint"]("n={n}")
        results["info"] = h["__minivivisection_thread_info"]()

    paused_threaded(at_pause, tmp_path)
    assert results["condition"] is True
    assert results["tagged"] == "[main] n=1"  # the script runs threads of its own
    assert results["untagged"] == "n=1"
    assert results["info"][0] == "main" and results["info"][2] >= 3


def test_threads_snapshot_shows_where_each_thread_is(h, tmp_path):
    results = {}

    def at_pause():
        rows = h["__minivivisection_threads"](True)
        for _, label, _, _, state, frames in rows:
            own = next(f for f in frames if f[3])  # the innermost frame of the script
            results[label] = (state, own[2], frames[0][3])

    paused_threaded(at_pause, tmp_path)
    assert results["main"] == ("paused here", "target", True)
    assert results["Worker-1"] == ("waiting in wait()", "waiter", False)  # inside threading's wait
    assert results["Worker-2"] == ("waiting in with", "locker", True)


def test_expression_in_every_thread(h, tmp_path):
    results = {}

    def at_pause():
        results.update({label: (here, ok, value) for label, _, here, ok, value in h["__minivivisection_eval_in_threads"]("n")})

    paused_threaded(at_pause, tmp_path)
    assert results["main"] == (True, True, 1)
    assert results["Worker-1"] == (False, True, 5)
    assert results["Worker-2"] == (False, True, 7)


def test_inline_values_key_is_per_thread(h):
    import threading

    keys = []

    def at_pause():
        keys.append(h["__minivivisection_inline_values"]()[0])

    paused(at_pause)
    assert keys[0].endswith("|" + str(threading.get_ident()))


def test_stop_script_finds_the_line_and_stops_the_thread(h, tmp_path):
    # RunPythonScript without the debugger: Ctrl+Alt+B finds where the script is and ends it.
    import threading
    import time
    path = tmp_path / "long_run.py"
    path.write_text(
        "def work(out):\n"
        "    total = 0\n"
        "    i = 0\n"
        "    while True:\n"
        "        i += 1\n"
        "        total += i\n"
        "        out['i'] = i\n",
        encoding="utf-8")
    namespace = {}
    exec(compile(path.read_text(encoding="utf-8"), str(path), "exec"), namespace)
    out, ended = {}, []

    def run():
        try:
            namespace["work"](out)
        except KeyboardInterrupt:
            ended.append("KeyboardInterrupt")

    thread = threading.Thread(target=run, daemon=True)
    thread.start()
    while "i" not in out:
        time.sleep(0.01)
    row = h["__minivivisection_stop_script"](str(path), True)
    thread.join(5)
    assert row is not None
    ident, filename, line, function, stack, values = row
    assert ident == thread.ident and os.path.samefile(filename, path)
    assert 4 <= line <= 7 and function == "work"
    assert stack[0][2] == "work" and any(name == "total" for name, _ in values)
    assert ended == ["KeyboardInterrupt"] and not thread.is_alive()
    assert h["__minivivisection_stop_script"](str(tmp_path / "other.py"), False) is None


def test_stop_script_any_script(h, tmp_path):
    # A Grasshopper component's code has no file to name: '*' finds the running script.
    import threading
    import time
    path = tmp_path / "component_code.py"
    path.write_text("def work(out):\n    n = 0\n    while True:\n        n += 1\n        out['n'] = n\n", encoding="utf-8")
    namespace = {}
    exec(compile(path.read_text(encoding="utf-8"), str(path), "exec"), namespace)
    out, ended = {}, []

    def run():
        try:
            namespace["work"](out)
        except KeyboardInterrupt:
            ended.append(True)

    thread = threading.Thread(target=run, daemon=True)
    thread.start()
    while "n" not in out:
        time.sleep(0.01)
    assert h["__minivivisection_stop_script"]("*", False) is None  # no thread named: none taken
    row = h["__minivivisection_stop_script"]("*", True, thread.ident)
    thread.join(5)
    assert row is not None and row[3] == "work" and ended == [True]


def test_stop_script_only_the_named_thread(h, tmp_path):
    # With the run's thread known, another thread running the same file is left alone.
    import threading
    import time
    path = tmp_path / "shared.py"
    path.write_text("def work(out, key):\n    while not out.get('stop'):\n        out[key] = out.get(key, 0) + 1\n", encoding="utf-8")
    namespace = {}
    exec(compile(path.read_text(encoding="utf-8"), str(path), "exec"), namespace)
    out, ended = {}, []

    def run(key):
        try:
            namespace["work"](out, key)
        except KeyboardInterrupt:
            ended.append(key)

    first = threading.Thread(target=run, args=("a",), daemon=True)
    second = threading.Thread(target=run, args=("b",), daemon=True)
    first.start()
    second.start()
    while "a" not in out or "b" not in out:
        time.sleep(0.01)
    row = h["__minivivisection_stop_script"](str(path), True, second.ident)
    second.join(5)
    out["stop"] = True
    first.join(5)
    assert row is not None and row[0] == second.ident
    assert ended == ["b"]


def test_interrupt_unknown_thread(h):
    assert h["__minivivisection_interrupt"](1) is False


def test_grasshopper_run_names_in_conditions_and_logpoints(h, tmp_path):
    # gh_run, gh_branch, gh_item: the Grasshopper run being evaluated, set by the plug-in.
    results = {}

    def at_pause():
        h["__minivivisection_set_gh"](4, "{0;3}", "{0;3}[2]")
        results["by data"] = h["__minivivisection_condition"]('gh_item == "{0;3}[2]"')
        results["other data"] = h["__minivivisection_condition"]('gh_branch == "{0;5}"')
        results["by number"] = h["__minivivisection_condition"]("gh_run == 4 and n == 1")
        results["logpoint"] = h["__minivivisection_logpoint"]("run {gh_run} {gh_item}: n={n}")
        h["__minivivisection_set_gh"](-1, None, None)  # outside Grasshopper: not there
        try:
            h["__minivivisection_condition"]("gh_run == 4")
            results["outside"] = "defined"
        except NameError:
            results["outside"] = "undefined"

    paused_threaded(at_pause, tmp_path)
    assert results["by data"] is True and results["other data"] is False and results["by number"] is True
    assert results["logpoint"] == "run 4 {0;3}[2]: n=1"
    assert results["outside"] == "undefined"


def test_value_text_survives_a_broken_repr(h):
    # One broken __repr__ in the frame must not take the other line-end values along.
    class Broken:
        __slots__ = ()

        def __repr__(self):
            raise RuntimeError("no repr")

    text, value = h["__minivivisection_value_text"](Broken())
    assert text.startswith("<repr failed") and value is None


def test_commands_leave_builtins_after_overlapping_inputs(h):
    # Two inputs that overlap (A starts, B starts, A ends, B ends): the commands must not
    # stay in builtins -- B used to save A's commands as "what builtins had".
    import builtins
    assert not hasattr(builtins, "show")
    a = h["__minivivisection_commands"]("a = 1")
    b = h["__minivivisection_commands"]("b = 2")
    a.__enter__()
    b.__enter__()
    assert builtins.show is h["show"]
    a.__exit__(None, None, None)
    assert builtins.show is h["show"]  # B still runs
    b.__exit__(None, None, None)
    assert not hasattr(builtins, "show")


def test_frame_key_names_the_paused_frame(h):
    # Edit in Rhino writes back only into the frame the edit came from.
    import threading
    keys = []
    paused(lambda: keys.append(h["__minivivisection_frame_key"]()))
    filename, function, first_line, thread = keys[0].split("|")
    assert filename == "script.py" and function == "target" and thread == str(threading.get_ident())
