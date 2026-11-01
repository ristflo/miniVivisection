#! python 3
# Review 2026-10-27 (H7): V°Claude in Grasshopper asks before a script it writes runs. gh_set_script,
# as the MCP server hands it to Claude (GrasshopperBridge.Tools), is asked like run_python: Ask
# shows the code and waits (declined: the component keeps its code), Off refuses without asking,
# On writes without asking. The question itself (CodeQuestion): Run answers yes, Always answers
# yes and switches "Claude may run Python" to On, /auto off back to Ask.
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import System
import Rhino
from System.Text.Json import JsonDocument
from System.Threading.Tasks import Task

OLD = '#! python 3\na = x * 2\n'
NEW = '#! python 3\na = x * 3  # written by Claude\n'

asked = []
answer = [False]


def ask(what, code):
    asked.append((what, code))
    return Task.FromResult[bool](answer[0])


def grasshopper_ready():
    asm = lt.assembly('Grasshopper')
    return (asm is not None and asm.GetType('Grasshopper.Instances').GetProperty('DocumentEditor').GetValue(None) is not None
            and lt.assembly('RhinoCodePluginGH') is not None and lt.assembly('miniVivisection.GH') is not None)


def code_of(component):
    text = lt.assembly('miniVivisection.GH').GetType('MiniVivisection.GH.GrasshopperTools').GetMethod('Call').Invoke(
        None, ['gh_get_script', '{"id": "%s"}' % component.InstanceGuid])
    return text


def test():
    yield 2
    Rhino.RhinoApp.RunScript('_Grasshopper', False)
    yield lt.wait_for(grasshopper_ready, 120)
    yield 2
    import Grasshopper
    from Grasshopper.Kernel import GH_Document, GH_ParamAccess
    import make_gh_examples as mk

    doc = GH_Document()
    double = mk._place(mk.script('Double', 'Double', OLD, [('x', GH_ParamAccess.item, 'double')], ['a']), 240, 220)
    doc.AddObject(double, False)
    Grasshopper.Instances.DocumentServer.AddDocument(doc)
    Grasshopper.Instances.ActiveCanvas.Document = doc
    # x gets a value before the first solution, so the scripts run without failing
    lt.assembly('miniVivisection.GH').GetType('MiniVivisection.GH.GrasshopperTools').GetMethod('Call').Invoke(
        None, ['gh_set_value', '{"id": "%s", "param": "x", "values": [2]}' % double.InstanceGuid])
    doc.Enabled = True
    doc.NewSolution(False)
    yield 2

    tools = list(lt.static_call('GrasshopperBridge', 'Tools', False, False, System.Func[System.String, System.String, Task[bool]](ask)))
    set_script = [t for t in tools if t.Name == 'gh_set_script'][0]
    args = '{"id": "%s", "code": %s}' % (double.InstanceGuid, System.Text.Json.JsonSerializer.Serialize[System.String](NEW))

    class call:
        """The tool called on a worker thread, as the MCP server calls it: the script is written and
        computed on Rhino's UI thread, outside this test's Python (a Python 3 component computed
        inside a Python 3 test's call can take Rhino down, now and then)."""
        def __init__(self):
            self.task = None
            System.Threading.Thread(System.Threading.ThreadStart(self.start)).Start()

        def start(self):
            self.task = set_script.Handler(JsonDocument.Parse(args).RootElement.Clone())

        @property
        def IsCompleted(self):
            return self.task is not None and self.task.IsCompleted

        @property
        def Result(self):
            return self.task.Result

    old = lt.static_call('MiniVivisectionSettings', 'GetText', 'ClaudeRunCode')
    try:
        lt.static_call('MiniVivisectionSettings', 'Set', 'ClaudeRunCode', 'Ask')
        task = call()
        yield lt.wait_for(lambda: task.IsCompleted, 10)
        lt.check('Ask: the user is asked, with the code', (len(asked), asked[0][1] if asked else None), (1, NEW))
        lt.check('declined: not done', task.Result.startswith('Not done: the user declined'), True)
        lt.check('declined: the component keeps its code', 'x * 2' in code_of(double) and 'x * 3' not in code_of(double), True)

        answer[0] = True
        task = call()
        yield lt.wait_for(lambda: task.IsCompleted, 20)
        lt.check('allowed: asked again, then written', (len(asked), task.Result.startswith('Script written')), (2, True))
        lt.check('allowed: the new code is in the component', 'x * 3' in code_of(double), True)

        lt.static_call('MiniVivisectionSettings', 'Set', 'ClaudeRunCode', 'Off')
        task = call()
        yield lt.wait_for(lambda: task.IsCompleted, 10)
        lt.check('Off: refused without asking', (len(asked), task.Result.startswith('Not done: running code is switched off')), (2, True))

        lt.static_call('MiniVivisectionSettings', 'Set', 'ClaudeRunCode', 'On')
        task = call()
        yield lt.wait_for(lambda: task.IsCompleted, 20)
        lt.check('On: written without asking', (len(asked), task.Result.startswith('Script written')), (2, True))

        # The question: Run, Always, /auto off
        lt.static_call('MiniVivisectionSettings', 'Set', 'ClaudeRunCode', 'Ask')
        written = []
        question_type = lt.plugin_type('CodeQuestion')
        question = System.Activator.CreateInstance(question_type, [
            System.Action[System.String](written.append), System.Action(lambda: None), System.Action[System.Action](lambda a: a())])
        asking = question.Ask('a test', 'print(1)', 'python')
        lt.check('the question is open', (question.Waiting, 'print(1)' in ''.join(written)), (True, True))
        question.Answer(True)
        yield lt.wait_for(lambda: asking.IsCompleted, 5)
        lt.check('Run: yes, and the question closes', (asking.Result, question.Waiting), (True, False))
        asking = question.Ask('a test', 'print(2)', 'python')
        question.Auto(True)
        yield lt.wait_for(lambda: asking.IsCompleted, 5)
        lt.check('Always: yes, and On from now on', (asking.Result, lt.static_call('MiniVivisectionSettings', 'GetText', 'ClaudeRunCode')), (True, 'On'))
        question.Auto(False)
        lt.check('/auto off: Ask again', lt.static_call('MiniVivisectionSettings', 'GetText', 'ClaudeRunCode'), 'Ask')
        asking = question.Ask('a test', 'print(3)', 'python')
        question.Answer(False)
        yield lt.wait_for(lambda: asking.IsCompleted, 5)
        lt.check('Deny: no', asking.Result, False)
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'ClaudeRunCode', old)
        doc.Enabled = False


lt.run('t_ghclaudegate', test)
