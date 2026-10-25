#! python 3
# V°Claude: Claude asks to run code -- Run and Deny buttons appear above the chat, the prompt
# with them; a click answers (as /run or /deny), the bar goes; typing /deny answers too.
# No real Claude: the chat's question (AskToRun) is asked directly.
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
    bar = lt.member(chat, '_answerBar')
    lt.check('no question: no bar', bar.Visible, False)

    ask = chat_type.GetMethod('AskToRun', lt.ANY)
    first = ask.Invoke(chat, ['evaluate', '[type(p).__name__ for p in points]'])
    yield 1
    lt.check('a question: the bar is shown', bar.Visible, True)
    lt.check("it says what, and the code's first line", lt.member(chat, '_answerQuestion').Text, 'Claude asks to run code: evaluate: [type(p).__name__ for p in points].')
    lt.check('the prompt is there to answer', view.IsPrompting, True)
    lt.check('the question in the chat', 'Run or Deny above, or type /run or /deny' in lt.member(view, 'TextNow'), True)

    # Click Run.
    run_button = [c for c in bar.Children if isinstance(c, ef.Button) and c.Text == 'Run'][0]
    run_button.PerformClick()
    yield 1
    lt.check('Run clicked: answered yes', (first.IsCompleted, first.Result if first.IsCompleted else None), (True, True))
    lt.check('the bar is gone', bar.Visible, False)
    lt.check('the chat says so', 'Running it.' in lt.member(view, 'TextNow'), True)

    # Typed /deny while Claude "answers": the second question refused that way.
    second = ask.Invoke(chat, ['run_python', 'print(1)'])
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
    form.Close()


lt.run('t_claudeanswer', test)
