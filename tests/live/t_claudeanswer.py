#! python 3
# V°Claude: Claude asks to run code -- Run, Always and Deny buttons appear above the chat, the
# prompt with them; a click answers (as /run, /auto on or /deny), the bar goes; typing /deny
# answers too; Always also lets Claude's code run without asking from then on.
# No real Claude: the chat's question (the gate's AskToRun, CodeQuestion) is asked directly.
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # livetest; it adds tools/
import livetest as lt
import Rhino


def test():
    yield 2
    import System
    import Eto.Forms as ef
    Rhino.RhinoApp.RunScript('_ScriptEditor', False)
    yield lt.wait_for(lambda: len(lt.editors()) > 0, 60)
    yield 3
    editor = lt.editors()[0]
    chat_type = lt.plugin_type('ClaudeChat')
    chat = System.Activator.CreateInstance(chat_type, [editor, lt.hook()])
    form = ef.Form()
    form.Content = chat
    form.Width = 520
    form.Height = 420
    form.Show()
    yield 1
    view = lt.member(chat, '_view')
    question = lt.member(chat, '_question')
    bar = lt.member(question, 'Bar')
    lt.check('no question: no bar', bar.Visible, False)

    ask_to_run = lt.member(lt.member(chat, '_gate'), 'AskToRun')

    def ask(what, code):
        return ask_to_run.Invoke(what, code)

    first = ask('evaluate', '[type(p).__name__ for p in points]')
    yield 1
    lt.check('a question: the bar is shown', bar.Visible, True)
    lt.check("it says what, and the code's first line", lt.member(question, '_question').Text, 'Claude asks to run code: evaluate: [type(p).__name__ for p in points].')
    lt.check('the prompt is there to answer', view.IsPrompting, True)
    lt.check('the question in the chat', 'Run, Always or Deny above, or type /run, /auto on or /deny' in lt.member(view, 'TextNow'), True)

    # Click Run.
    run_button = [c for c in bar.Children if isinstance(c, ef.Button) and c.Text == 'Run'][0]
    run_button.PerformClick()
    yield 1
    lt.check('Run clicked: answered yes', (first.IsCompleted, first.Result if first.IsCompleted else None), (True, True))
    lt.check('the bar is gone', bar.Visible, False)
    lt.check('the chat says so', 'Running it.' in lt.member(view, 'TextNow'), True)

    # Typed /deny while Claude "answers": the second question refused that way.
    second = ask('run_python', 'print(1)')
    yield 1
    lt.check('a second question: the bar again', bar.Visible, True)
    runner = lt.member(chat, '_runner')
    busy = runner.GetType().GetProperty('Busy', lt.ANY)
    submit = chat_type.GetMethod('Submit', lt.ANY)
    submit.Invoke(chat, ['/deny'])
    yield 1
    lt.check('/deny typed: answered no', (second.IsCompleted, second.Result if second.IsCompleted else None), (True, False))
    lt.check('the bar is gone again', bar.Visible, False)
    lt.check('Claude not running here', busy.GetValue(runner), False)

    # Click Always: yes, and "Claude may run Python" On from now on; /auto off asks again.
    old = lt.static_call('MiniVivisectionSettings', 'GetText', 'ClaudeRunCode')
    try:
        third = ask('run_python', 'print(2)')
        yield 1
        always = [c for c in bar.Children if isinstance(c, ef.Button) and c.Text == 'Always'][0]
        always.PerformClick()
        yield 1
        lt.check('Always clicked: answered yes', (third.IsCompleted, third.Result if third.IsCompleted else None), (True, True))
        lt.check('Always: Claude may run Python is On', lt.static_call('MiniVivisectionSettings', 'GetText', 'ClaudeRunCode'), 'On')
        submit.Invoke(chat, ['/auto off'])
        yield 1
        lt.check('/auto off: Ask again', lt.static_call('MiniVivisectionSettings', 'GetText', 'ClaudeRunCode'), 'Ask')
    finally:
        lt.static_call('MiniVivisectionSettings', 'Set', 'ClaudeRunCode', old)
    form.Close()


lt.run('t_claudeanswer', test)
