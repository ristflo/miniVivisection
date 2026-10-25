"""miniVivisection's live tests: each test script runs in a fresh Rhino 8.

    python tests/live/run_live.py                 # every t_*.py here (not t_*_real.py: real Claude)
    python tests/live/run_live.py t_tracelock     # one (or more) by name

For each test: Rhino starts with /nosplash /notemplate and /runscript running the test
through RunPythonScript (Python 3). The test drives the plug-in from inside Rhino
(livetest.py) and writes %TEMP%/minivivisection-live/<test>.json. Meanwhile this runner
asks Rhino's main window every second whether it still answers (WM_NULL through
SendMessageTimeout): a window that stops answering for FREEZE_SECONDS is a freeze, and
the test fails. Rhino is closed after each test (killed if it doesn't exit).

Out of the way: the test Rhino starts minimized without taking the focus, and every window
it shows -- the Script Editor, Grasshopper, dialogs -- is moved far off-screen the moment it
appears (checked every 20 ms), without being activated; the windows stay real and visible
to Windows, so the tests behave as on screen. Rhino is killed, never closed, so no window
position is saved. --visible (or MINIVIVISECTION_TEST_VISIBLE=1) shows them as before;
--display N (or MINIVIVISECTION_TEST_DISPLAY=N) puts them on Windows' display N instead --
1, 2, 3 as numbered in Windows' display settings (--list-displays lists them);
--desktop vscode (or N, or MINIVIVISECTION_TEST_DESKTOP) on another virtual desktop -- the one
VS Code's window with this repository is on, or Windows' Nth: the plug-in moves Rhino's windows
there (TestDesktop.cs: Windows lets a program move only its own windows between desktops). But
Rhino and Grasshopper activate their own windows (Grasshopper's loading window, its editor), and
Windows then takes the user to that virtual desktop. --desktop hidden avoids that: Rhino runs on
a Win32 desktop of its own (CreateDesktop), never on screen, never taking the focus.

Needs the plug-in built in Release (bin/Release/net7.0, where Rhino has it registered)
and no other Rhino running (the build would be locked, and windows would mix up).
"""
import ctypes
import ctypes.wintypes as wt
import glob
import json
import os
import subprocess
import sys
import threading
import time

RHINO = r'C:\Program Files\Rhino 8\System\Rhino.exe'
HERE = os.path.dirname(os.path.abspath(__file__))
RESULT_DIR = os.path.join(os.environ.get('TEMP', '.'), 'minivivisection-live')
FREEZE_SECONDS = 60  # Grasshopper's first load keeps the UI busy for a while
user32 = ctypes.windll.user32


def windows_of(pid):
    found = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def each(hwnd, _):
        owner = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            found.append(hwnd)
        return True

    if HIDDEN_DESKTOP:
        user32.EnumDesktopWindows(HIDDEN_DESKTOP, each, 0)
    else:
        user32.EnumWindows(each, 0)
    return found


OFF_SCREEN = -32000
VISIBLE = os.environ.get('MINIVIVISECTION_TEST_VISIBLE') == '1'
DISPLAY = int(os.environ.get('MINIVIVISECTION_TEST_DISPLAY') or 0)  # 0: off-screen
AREA = None  # display DISPLAY's work area (main sets it)
DESKTOP = os.environ.get('MINIVIVISECTION_TEST_DESKTOP') or ''  # 'vscode', a number, 'hidden', or '' (off)
HIDDEN_DESKTOP_NAME = 'minivivisection-tests'
HIDDEN_DESKTOP = None  # with --desktop hidden: the handle of that Win32 desktop (main opens it)
kernel32 = ctypes.windll.kernel32
user32.CreateDesktopW.restype = wt.HANDLE
user32.CreateDesktopW.argtypes = [wt.LPCWSTR, wt.LPCWSTR, ctypes.c_void_p, wt.DWORD, wt.DWORD, ctypes.c_void_p]
user32.EnumDesktopWindows.argtypes = [wt.HANDLE, ctypes.c_void_p, wt.LPARAM]


class _StartupInfo(ctypes.Structure):
    _fields_ = [('cb', wt.DWORD), ('lpReserved', wt.LPWSTR), ('lpDesktop', wt.LPWSTR), ('lpTitle', wt.LPWSTR),
                ('dwX', wt.DWORD), ('dwY', wt.DWORD), ('dwXSize', wt.DWORD), ('dwYSize', wt.DWORD),
                ('dwXCountChars', wt.DWORD), ('dwYCountChars', wt.DWORD), ('dwFillAttribute', wt.DWORD),
                ('dwFlags', wt.DWORD), ('wShowWindow', wt.WORD), ('cbReserved2', wt.WORD),
                ('lpReserved2', ctypes.c_void_p), ('hStdInput', wt.HANDLE), ('hStdOutput', wt.HANDLE), ('hStdError', wt.HANDLE)]


class _ProcessInformation(ctypes.Structure):
    _fields_ = [('hProcess', wt.HANDLE), ('hThread', wt.HANDLE), ('dwProcessId', wt.DWORD), ('dwThreadId', wt.DWORD)]


class OnHiddenDesktop:
    """Rhino started on the hidden Win32 desktop (subprocess can't name one): what run_test uses of a Popen."""

    def __init__(self, command):
        startup = _StartupInfo()
        startup.cb = ctypes.sizeof(startup)
        startup.lpDesktop = HIDDEN_DESKTOP_NAME
        info = _ProcessInformation()
        line = ctypes.create_unicode_buffer(command)
        if not kernel32.CreateProcessW(None, line, None, None, False, 0, None, None, ctypes.byref(startup), ctypes.byref(info)):
            raise ctypes.WinError()
        kernel32.CloseHandle(info.hThread)
        self._handle = info.hProcess
        self.pid = info.dwProcessId
        self.returncode = None

    def poll(self):
        if self.returncode is None and kernel32.WaitForSingleObject(self._handle, 0) == 0:
            code = wt.DWORD()
            kernel32.GetExitCodeProcess(self._handle, ctypes.byref(code))
            self.returncode = code.value
        return self.returncode

    def wait(self, timeout=None):
        if kernel32.WaitForSingleObject(self._handle, 0xFFFFFFFF if timeout is None else int(timeout * 1000)) != 0:
            raise subprocess.TimeoutExpired('Rhino', timeout)
        return self.poll()


class _Rect(ctypes.Structure):
    _fields_ = [('left', ctypes.c_long), ('top', ctypes.c_long), ('right', ctypes.c_long), ('bottom', ctypes.c_long)]


class _Placement(ctypes.Structure):
    _fields_ = [('length', ctypes.c_uint), ('flags', ctypes.c_uint), ('showCmd', ctypes.c_uint),
                ('minPosition', wt.POINT), ('maxPosition', wt.POINT), ('normalPosition', _Rect)]


class _MonitorInfo(ctypes.Structure):
    _fields_ = [('size', wt.DWORD), ('monitor', _Rect), ('work', _Rect), ('flags', wt.DWORD), ('device', ctypes.c_wchar * 32)]


def displays():
    """Windows' displays: {number: (device, work area)} -- device DISPLAY3 is display 3."""
    found = {}

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HMONITOR, wt.HDC, ctypes.POINTER(_Rect), wt.LPARAM)
    def each(monitor, dc, rect, _):
        info = _MonitorInfo()
        info.size = ctypes.sizeof(_MonitorInfo)
        user32.GetMonitorInfoW(monitor, ctypes.byref(info))
        digits = ''.join(c for c in info.device if c.isdigit())
        number = int(digits) if digits else len(found) + 1
        found[number] = (info.device, (info.work.left, info.work.top, info.work.right, info.work.bottom))
        return True

    user32.EnumDisplayMonitors(None, None, each, 0)
    return dict(sorted(found.items()))


def target_area():
    """Where the test Rhino's windows go: display DISPLAY's work area, or None (off-screen)."""
    if not DISPLAY:
        return None
    known = displays()
    if DISPLAY not in known:
        raise SystemExit('No display %d -- there are: %s' % (DISPLAY, ', '.join('%d (%s)' % (n, d) for n, (d, _) in known.items())))
    return known[DISPLAY][1]


def keep_out_of_sight(pid, stop):
    """Every window of the test Rhino off-screen, as it appears (its own thread)."""
    SWP_NOSIZE, SWP_NOZORDER, SWP_NOACTIVATE, SWP_NOOWNERZORDER = 0x1, 0x4, 0x10, 0x200
    placed = set()
    while not stop.is_set():
        try:
            for hwnd in windows_of(pid):
                if DESKTOP:
                    continue  # the plug-in moves the windows to the test desktop, and shows Rhino's there
                if user32.IsIconic(hwnd):
                    if AREA is not None and 'Rhino 8' in title(hwnd) and hwnd not in placed:
                        # On a display: Rhino starts minimized (its loading would show at its saved
                        # place); once ready, its main window goes onto the display, not activated.
                        placed.add(hwnd)
                        placement = _Placement()
                        placement.length = ctypes.sizeof(_Placement)
                        user32.GetWindowPlacement(hwnd, ctypes.byref(placement))
                        left, top, right, bottom = AREA
                        placement.normalPosition = _Rect(left, top, right, bottom)
                        placement.showCmd = 4  # SW_SHOWNOACTIVATE
                        user32.SetWindowPlacement(hwnd, ctypes.byref(placement))
                    continue  # minimized: out of sight already
                if user32.IsZoomed(hwnd):
                    # Maximized (Grasshopper remembers it): un-maximized, off-screen, not activated
                    placement = _Placement()
                    placement.length = ctypes.sizeof(_Placement)
                    user32.GetWindowPlacement(hwnd, ctypes.byref(placement))
                    width = placement.normalPosition.right - placement.normalPosition.left
                    height = placement.normalPosition.bottom - placement.normalPosition.top
                    if AREA is not None:
                        left, top, right, bottom = AREA
                        placement.normalPosition = _Rect(left + 40, top + 40, min(left + 40 + max(width, 800), right), min(top + 40 + max(height, 600), bottom))
                    else:
                        placement.normalPosition = _Rect(OFF_SCREEN, OFF_SCREEN, OFF_SCREEN + max(width, 800), OFF_SCREEN + max(height, 600))
                    placement.showCmd = 4  # SW_SHOWNOACTIVATE
                    user32.SetWindowPlacement(hwnd, ctypes.byref(placement))
                    continue
                rect = _Rect()
                user32.GetWindowRect(hwnd, ctypes.byref(rect))
                if AREA is not None:
                    # On display DISPLAY: inside its work area, at most its size
                    left, top, right, bottom = AREA
                    if left <= rect.left and rect.right <= right and top <= rect.top and rect.bottom <= bottom:
                        continue
                    width = min(rect.right - rect.left, right - left)
                    height = min(rect.bottom - rect.top, bottom - top)
                    x = min(max(rect.left, left), right - width) if left <= rect.left < right else left + 40
                    y = min(max(rect.top, top), bottom - height) if top <= rect.top < bottom else top + 40
                    user32.SetWindowPos(hwnd, None, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER)
                elif rect.left > OFF_SCREEN + 1000:
                    user32.SetWindowPos(hwnd, None, OFF_SCREEN, OFF_SCREEN, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER)
        except Exception:
            pass
        stop.wait(0.02)


def title(hwnd):
    buffer = ctypes.create_unicode_buffer(512)
    user32.GetWindowTextW(hwnd, buffer, 512)
    return buffer.value


def main_window(pid):
    for hwnd in windows_of(pid):
        if 'Rhino 8' in title(hwnd):
            return hwnd
    return None


# Dialogs a test Rhino may show that nobody is there to answer: (window title, button to press).
# Grasshopper offers its autosave when a definition from an earlier, killed test Rhino opens.
DIALOGS = [
    ('Last Chance Recovery', 'Ignore Recovery File'),
]


def press_known_buttons(pid):
    """Presses the answer of a known dialog of the test's Rhino; returns what it pressed."""
    pressed = []
    for hwnd in windows_of(pid):
        for dialog, button in DIALOGS:
            if title(hwnd) != dialog:
                continue
            found = []

            @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
            def each(child, _):
                if title(child) == button:
                    found.append(child)
                return True

            user32.EnumChildWindows(hwnd, each, 0)
            if found:
                user32.PostMessageW(found[0], 0x00F5, 0, 0)  # BM_CLICK
                pressed.append(dialog + ' > ' + button)
            else:
                # A dialog drawn without child windows (WPF): its button by keyboard -- the
                # answer is the last of the dialog's buttons, so Tab to it from the first.
                pressed.append(dialog + ' (no button window: not pressed)')
    return pressed


def answers(hwnd):
    result = ctypes.c_size_t()
    # SMTO_ABORTIFHUNG | SMTO_BLOCK, 2 s
    return user32.SendMessageTimeoutW(hwnd, 0, 0, 0, 0x0002 | 0x0001, 2000, ctypes.byref(result)) != 0


def stacks_of(pid):
    """The managed stacks of a frozen Rhino (dotnet-stack, if found: MINIVIVISECTION_DOTNET_STACK or the PATH)."""
    tool = os.environ.get('MINIVIVISECTION_DOTNET_STACK') or 'dotnet-stack'
    try:
        out = subprocess.run([tool, 'report', '-p', str(pid)], capture_output=True, text=True, timeout=60)
        path = os.path.join(RESULT_DIR, 'freeze_stacks.txt')
        with open(path, 'w', encoding='utf-8') as f:
            f.write(out.stdout + out.stderr)
        return path
    except Exception as e:
        return 'no stacks (%s)' % e


def rhino_pids():
    out = subprocess.run(['tasklist', '/FI', 'IMAGENAME eq Rhino.exe', '/NH', '/FO', 'CSV'], capture_output=True, text=True).stdout
    return {int(line.split('","')[1]) for line in out.splitlines() if line.startswith('"Rhino.exe"')}


def rhino_running():
    return bool(rhino_pids())


def wait_for_other_rhinos():
    """A Rhino the user started meanwhile: the next test waits until it's closed -- it's never killed
    (its windows would mix with the test's, and its log and settings are the same files)."""
    if not rhino_running():
        return
    print('   (a Rhino is running -- yours? the tests wait until it is closed)', flush=True)
    while rhino_running():
        time.sleep(2)


GRASSHOPPER_AUTOSAVE = os.path.join(os.environ.get('APPDATA', ''), 'Grasshopper', 'AutoSave')


def clear_grasshopper_autosave():
    """Grasshopper's autosaves, deleted: a test Rhino is killed with definitions open, and
    their autosaves would bring up the "Last Chance Recovery" dialog in the next test."""
    if not os.path.isdir(GRASSHOPPER_AUTOSAVE):
        return
    for name in os.listdir(GRASSHOPPER_AUTOSAVE):
        try:
            os.remove(os.path.join(GRASSHOPPER_AUTOSAVE, name))
        except OSError:
            pass


def run_test(path, timeout):
    wait_for_other_rhinos()
    clear_grasshopper_autosave()
    name = os.path.splitext(os.path.basename(path))[0]
    result_file = os.path.join(RESULT_DIR, name + '.json')
    progress_file = os.path.join(RESULT_DIR, name + '.progress')
    os.makedirs(RESULT_DIR, exist_ok=True)
    for old in (result_file, progress_file):
        if os.path.exists(old):
            os.remove(old)
    # One command line as Rhino reads it: /runscript="..." -- subprocess would quote the
    # whole switch, which Rhino then ignores. The test's path has no spaces. ScriptEditor
    # Run with Debug off: plainly, whatever the "RunPythonScript command" setting says.
    command = '"%s" /nosplash /notemplate /runscript="_-ScriptEditor _Run _Debug=_Off %s"' % (RHINO, path)
    stop_hiding = threading.Event()
    if VISIBLE:
        process = subprocess.Popen(command)
    elif HIDDEN_DESKTOP:
        process = OnHiddenDesktop(command)  # never on screen: nothing to move
    else:
        startup = subprocess.STARTUPINFO()
        startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        startup.wShowWindow = 7  # SW_SHOWMINNOACTIVE: minimized, not activated (on a display: shown there once ready)
        environment = dict(os.environ)
        if DESKTOP:
            environment['MINIVIVISECTION_TEST_DESKTOP'] = DESKTOP
        process = subprocess.Popen(command, startupinfo=startup, env=environment)
        threading.Thread(target=keep_out_of_sight, args=(process.pid, stop_hiding), daemon=True).start()
    started = time.time()
    hung_since = None
    verdict = None
    try:
        while True:
            time.sleep(1)
            if os.path.exists(result_file):
                time.sleep(0.5)
                with open(result_file, encoding='utf-8') as f:
                    verdict = json.load(f)
                break
            if process.poll() is not None:
                verdict = {'test': name, 'ok': False, 'checks': [{'name': 'Rhino exited early', 'ok': False, 'actual': process.returncode, 'expected': ''}]}
                break
            for what in press_known_buttons(process.pid):
                print('   (pressed: %s)' % what, flush=True)
            hwnd = main_window(process.pid)
            if hwnd is not None and time.time() - started > 10:
                if answers(hwnd):
                    hung_since = None
                elif hung_since is None:
                    hung_since = time.time()
                elif time.time() - hung_since > FREEZE_SECONDS:
                    verdict = {'test': name, 'ok': False, 'checks': [{'name': 'FREEZE: Rhino stopped answering', 'ok': False, 'actual': '%d s' % FREEZE_SECONDS, 'expected': 'answers'}]}
                    verdict['stacks'] = stacks_of(process.pid)
                    break
            if time.time() - started > timeout:
                verdict = {'test': name, 'ok': False, 'checks': [{'name': 'timeout', 'ok': False, 'actual': '%d s' % timeout, 'expected': 'a result'}]}
                break
    finally:
        stop_hiding.set()
        close_rhino(process)
        clear_grasshopper_autosave()
    if not verdict['ok']:
        # Where it got to, and what the plug-in logged: for a freeze or a timeout.
        if os.path.exists(progress_file):
            with open(progress_file, encoding='utf-8') as f:
                verdict['progress'] = f.read()
        log = os.path.join(os.environ.get('TEMP', '.'), 'rhinovivisection', 'minivivisection', 'minivivisection.log')
        if os.path.exists(log):
            with open(log, encoding='utf-8', errors='replace') as f:
                verdict['log'] = ''.join(f.readlines()[-25:])
    return verdict


def close_rhino(process):
    """Ends the test's Rhino -- only that one: a Rhino the user started meanwhile stays."""
    for _ in range(30):
        subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'], capture_output=True)
        if process.pid not in rhino_pids():
            break
        time.sleep(1)
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        pass


def main(argv):
    global VISIBLE, DISPLAY, AREA, DESKTOP, HIDDEN_DESKTOP
    # A test's notes may hold any character (☐ ☑): printed as UTF-8, also into a pipe
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    if '--visible' in argv:
        VISIBLE = True
        argv = [a for a in argv if a != '--visible']
    if '--list-displays' in argv:
        for number, (device, area) in displays().items():
            print('display %d: %s, work area %s' % (number, device, area))
        return 0
    if '--display' in argv:
        at = argv.index('--display')
        DISPLAY = int(argv[at + 1])
        argv = argv[:at] + argv[at + 2:]
    if '--desktop' in argv:
        at = argv.index('--desktop')
        DESKTOP = argv[at + 1]
        argv = argv[:at] + argv[at + 2:]
    if DESKTOP.lower() == 'hidden':
        DESKTOP = ''
        HIDDEN_DESKTOP = user32.CreateDesktopW(HIDDEN_DESKTOP_NAME, None, None, 0, 0x10000000, None)  # GENERIC_ALL
        if not HIDDEN_DESKTOP:
            print('Could not create the hidden desktop: %s' % ctypes.WinError())
            return 2
        print('Test Rhino on the hidden desktop %s: never on screen' % HIDDEN_DESKTOP_NAME)
    if DESKTOP.lower() == 'vscode':
        # VS Code's window with this repository open: its title names the repository's folder
        repository = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..', '..', '..'))
        DESKTOP = 'vscode:' + os.path.basename(repository)
    AREA = target_area()
    if DESKTOP:
        print('Test Rhino windows on virtual desktop: %s' % DESKTOP)
    if AREA is not None:
        print('Test Rhino windows on display %d, work area %s' % (DISPLAY, AREA))
    if rhino_running():
        # Its windows would mix with the test's (and it holds the plug-in): the tests wait
        print('A Rhino is running -- the tests start when it is closed.', flush=True)
        while rhino_running():
            time.sleep(2)
    wanted = argv[1:]
    # t_*_real.py use the real Claude Code (they cost a little): only when named
    tests = sorted(t for t in glob.glob(os.path.join(HERE, 't_*.py')) if not t.endswith('_real.py'))
    if wanted:
        # any script named here, also one that isn't a test (shots_rhino, shots_gh: the screenshots)
        tests = [os.path.join(HERE, name + '.py') for name in wanted if os.path.exists(os.path.join(HERE, name + '.py'))]
    failed = 0
    for path in tests:
        timeout = 600 if path.endswith('_real.py') else 300
        print('== %s' % os.path.basename(path), flush=True)
        verdict = run_test(path, timeout)
        for c in verdict['checks']:
            if c['name'] == 'note':
                print('        ' + c['actual'])
                continue
            print('   %s %s%s' % ('ok  ' if c['ok'] else 'FAIL', c['name'],
                                  '' if c['ok'] else '  (got %s, want %s)' % (c['actual'], c['expected'])))
        if verdict.get('stacks'):
            print('   --- managed stacks of the frozen Rhino: ' + verdict['stacks'])
        for part in ('progress', 'log'):
            if verdict.get(part):
                print('   --- %s:' % part)
                for line in verdict[part].splitlines():
                    print('      ' + line)
        print('   -> %s' % ('PASSED' if verdict['ok'] else 'FAILED'), flush=True)
        failed += 0 if verdict['ok'] else 1
    print('%d of %d live tests failed' % (failed, len(tests)) if failed else 'ALL %d LIVE TESTS PASSED' % len(tests))
    return failed


if __name__ == '__main__':
    sys.exit(main(sys.argv))
