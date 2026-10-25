#! python 3
# Batch 7 (review 2026-10-10: H6, M14): Claude's tools against a real Script Editor. Code to
# run (run_python, an evaluate that calls something) asks the user when "Claude may run
# Python" is Ask, is refused when Off; plain expressions are read without asking; an edit
# for another script than the message's is refused.
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino
from System.Text.Json import JsonDocument
from System.Threading.Tasks import Task

asked = []
answer = [False]


def ask(what, code):
    asked.append((what, code))
    return Task.FromResult[bool](answer[0])


def call(tools, name, json):
    tool = [t for t in tools if t.Name == name][0]
    return tool.Handler(JsonDocument.Parse(json).RootElement.Clone())


def test():
    yield 2
    Rhino.RhinoApp.RunScript('_ScriptEditor', False)
    yield lt.wait_for(lambda: len(lt.editors()) > 0, 60)
    yield 3
    editor = lt.editors()[0]
    asm = lt.assembly('miniVivisection')
    access = asm.GetType('MiniVivisection.ScriptEditorAccess').GetConstructors(lt.ANY)[0].Invoke([editor])
    gate = asm.GetType('MiniVivisection.EditGate').GetConstructors(lt.ANY)[0].Invoke([])
    gate.GetType().GetField('AskToRun').SetValue(gate, System.Func[System.String, System.String, Task[bool]](ask))
    tools = list(lt.static_call('ScriptEditorTools', 'For', access, lt.hook(), gate))
    old = lt.static_call('MiniVivisectionSettings', 'GetText', 'ClaudeRunCode')
    lt.check('the default is Ask', old, 'Ask')
    try:
        lt.static_call('MiniVivisectionSettings', 'Set', 'ClaudeRunCode', 'Ask')
        task = call(tools, 'evaluate', '{"expression": "__import__(\'os\').getcwd()"}')
        yield lt.wait_for(lambda: task.IsCompleted, 10)
        lt.check('Ask: a call in evaluate asks the user', len(asked), 1)
        lt.check('declined: not run', task.Result.startswith('Not run'), True)

        task = call(tools, 'evaluate', '{"expression": "pts[2].X"}')
        yield lt.wait_for(lambda: task.IsCompleted, 10)
        lt.check('a plain expression is read without asking', len(asked), 1)
        lt.check('(not paused: nothing to read)', 'paused' in task.Result, True)

        task = call(tools, 'run_python', '{"code": "1 + 1"}')
        yield lt.wait_for(lambda: task.IsCompleted, 10)
        lt.check('run_python asks too', len(asked), 2)

        answer[0] = True
        task = call(tools, 'run_python', '{"code": "1 + 1"}')
        yield lt.wait_for(lambda: task.IsCompleted, 20)
        lt.check('allowed: it runs', task.Result.startswith('Not run'), False)
        lt.note('run_python after /run: ' + task.Result[:80])

        lt.static_call('MiniVivisectionSettings', 'Set', 'ClaudeRunCode', 'Off')
        task = call(tools, 'run_python', '{"code": "1 + 1"}')
        yield lt.wait_for(lambda: task.IsCompleted, 10)
        lt.check('Off: refused without asking', (len(asked), task.Result.startswith('Not allowed')), (3, True))

        gate.GetType().GetField('Script').SetValue(gate, System.Uri('file:///C:/somewhere/other_script.py'))
        task = call(tools, 'edit_script', '{"old_text": "a", "new_text": "b"}')
        yield lt.wait_for(lambda: task.IsCompleted, 10)
        lt.check('an edit for another script is refused', task.Result.startswith('Not applied: the user switched'), True)
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'ClaudeRunCode', old)


lt.run('t_claudegate', test)
