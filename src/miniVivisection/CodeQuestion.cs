using System;
using System.Linq;
using System.Threading.Tasks;
using Eto.Drawing;
using Eto.Forms;

namespace MiniVivisection
{
    /// <summary>
    /// Claude asks to run code ("Claude may run Python": Ask), in either V°Claude window -- Python
    /// in Rhino (run_python, evaluate, a V Watch expression) or a script Claude writes into a
    /// Grasshopper script component (gh_set_script: it runs when the definition computes). The
    /// code goes into the chat, and a bar above it names it with Run, Always and Deny -- the same
    /// as typing /run, /auto on, /deny. Always runs it and switches the option to On (no more
    /// questions; /auto off or the Options page asks again). No answer within 2 minutes: not run.
    /// One question at a time: a new one withdraws the one still open.
    /// </summary>
    internal sealed class CodeQuestion
    {
        public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

        private readonly Action<string> _write;
        private readonly Action _prompt;     // show the prompt and focus the input, to answer now
        private readonly Action<Action> _ui; // runs an action on the UI thread
        private TaskCompletionSource<bool> _waiting;
        private Label _question;

        public CodeQuestion(Action<string> write, Action prompt, Action<Action> ui)
        {
            _write = write;
            _prompt = prompt;
            _ui = ui;
        }

        /// <summary>A question is open (UI thread).</summary>
        public bool Waiting => _waiting != null;

        /// <summary>The bar above the chat, shown only while a question is open.</summary>
        public Panel Bar { get; private set; }

        public Control MakeBar()
        {
            _question = new Label { VerticalAlignment = VerticalAlignment.Center, Wrap = WrapMode.Word, TextColor = Color.FromArgb(60, 45, 0) };
            var run = new Button { Text = "Run", ToolTip = "Run the code Claude asks to run (/run)", MinimumSize = new Size(70, 0) };
            var always = new Button { Text = "Always", ToolTip = "Run it, and from now on run Claude's code without asking (/auto on; /auto off asks again)", MinimumSize = new Size(70, 0) };
            var deny = new Button { Text = "Deny", ToolTip = "Don't run it (/deny)", MinimumSize = new Size(70, 0) };
            run.Click += (s, e) => Answer(true);
            always.Click += (s, e) => Auto(true);
            deny.Click += (s, e) => Answer(false);
            var row = new TableLayout { Spacing = new Size(8, 0), Rows = { new TableRow(new TableCell(_question, true), run, always, deny) } };
            Bar = new Panel { Content = row, Padding = new Padding(8, 5), Visible = false, BackgroundColor = Color.FromArgb(255, 244, 206) };
            return Bar;
        }

        /// <summary>Asks the user (any thread); true when they let it run. The callers ask only
        /// when "Claude may run Python" is Ask.</summary>
        public Task<bool> Ask(string what, string code, string language)
        {
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ui(() =>
            {
                // A question still open is withdrawn -- said, so that Run isn't read as an answer to the code above
                if (_waiting != null && _waiting.TrySetResult(false))
                    _write("[the question above is withdrawn: not run -- Claude asks again]\n");
                _waiting = answer;
                var shown = (code ?? "").TrimEnd();
                _write(string.Format("[Claude asks to run code: {0}]\n```{1}\n{2}\n```\nRun, Always or Deny above, or type /run, /auto on or /deny ({3} minutes).\n",
                    what, language, shown, (int)Timeout.TotalMinutes));
                // The bar names the code too (its first line), so Run means this code and no other
                var firstLine = shown.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
                if (_question != null)
                {
                    _question.Text = "Claude asks to run code: " + what + (firstLine.Length > 0 ? ": " + (firstLine.Length > 60 ? firstLine.Substring(0, 60) + "…" : firstLine) : "") + ".";
                    Bar.Visible = true;
                }
                _prompt();
                UserLog.Info("Claude asks to run code ({0}): Run, Always or Deny in V Claude.", what);
            });
            Task.Delay(Timeout).ContinueWith(_ =>
            {
                if (answer.TrySetResult(false))
                    _ui(() =>
                    {
                        _write(string.Format("[not run: no answer within {0} minutes]\n", (int)Timeout.TotalMinutes));
                        if (_waiting == answer)
                            Close();
                    });
            }, TaskScheduler.Default);
            return answer.Task;
        }

        /// <summary>The answer: a button, /run or /deny (UI thread).</summary>
        public void Answer(bool run)
        {
            var waiting = _waiting;
            Close();
            if (waiting == null || !waiting.TrySetResult(run))
                _write("No code is waiting to run.\n");
            else
                _write(run ? "Running it.\n" : "Refused.\n");
            _prompt();
        }

        /// <summary>/auto on|off, or Always: Claude's code runs without asking ("Claude may run
        /// Python": On; the open question, if any, is answered Run) -- or is asked again (Ask).</summary>
        public void Auto(bool on)
        {
            MiniVivisectionSettings.Set("ClaudeRunCode", on ? "On" : "Ask");
            if (on && _waiting != null)
                Answer(true);
            _write(on
                ? "Claude's code runs without asking from now on (Options > miniVivisection > Claude > \"Claude may run Python\": On; /auto off asks again).\n"
                : "Claude asks before its code runs (\"Claude may run Python\": Ask).\n");
        }

        /// <summary>The window goes: the open question is answered Deny.</summary>
        public void Withdraw()
        {
            _waiting?.TrySetResult(false);
            _waiting = null;
        }

        private void Close()
        {
            _waiting = null;
            if (Bar != null)
                Bar.Visible = false;
        }
    }
}
