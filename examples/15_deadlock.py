#! python 3
# async:true
"""15 -- Find a hang: Where is everyone?, Break now

Needs: the line '# async:true' above: the script runs on a thread of its own, Rhino stays free.

The bug
    A mason lays bricks and a plasterer covers them; each needs both the bricks and the
    mortar. Sometimes -- here: always -- the script never finishes. Rhino stays usable,
    but the wall never gets built.

Try this
    1. Click Debug (the editor's Debug button, or V°Threads' Debug): the script runs
       in the debugger and hangs. (F5 without a breakpoint is a plain Run: Break now
       can't pause that one.)
    2. Open the V°Threads tab and click "Where is everyone?" -- while the script runs:
       mason 'waiting in with' at  with mortar: , plasterer 'waiting in with' at
       with bricks: , and the script's thread waiting. Each holds the lock the other
       one waits for: a deadlock.
    3. Press Ctrl+Alt+B, or click "Break now": the script's own thread pauses at its next line (in its
       waiting loop). The workers can't: they wait inside a lock, not at a line. While
       paused, watch  wall  in the V°Watch tab: what was built so far.
    4. Stop (Shift+F5). Fix it: take the locks in the same order in both functions
       (bricks, then mortar) -- the wall gets built.
    5. Break now from Rhino's command line: the command  miniVivisection BreakNow
       does what the button does.

Without miniVivisection
    A script that hangs says nothing. You'd add prints around every lock, run it again,
    and guess from the last line printed.
"""
import threading
import time
import Rhino.Geometry as rg

bricks = threading.Lock()
mortar = threading.Lock()
wall = []


def mason():
    for course in range(5):
        with bricks:
            time.sleep(0.05)
            with mortar:
                wall.append(rg.Box(rg.Plane.WorldXY, rg.Interval(0, 10), rg.Interval(0, 0.3), rg.Interval(course * 0.5, course * 0.5 + 0.45)))


def plasterer():
    for course in range(5):
        with mortar:
            time.sleep(0.05)
            with bricks:  # the bug: the other order than mason()
                wall.append(rg.Box(rg.Plane.WorldXY, rg.Interval(0, 10), rg.Interval(0.3, 0.35), rg.Interval(course * 0.5, course * 0.5 + 0.45)))


started = time.time()
workers = [threading.Thread(target=mason, name="mason", daemon=True), threading.Thread(target=plasterer, name="plasterer", daemon=True)]
for worker in workers:
    worker.start()
while any(w.is_alive() for w in workers) and time.time() - started < 120:
    time.sleep(0.2)     # waiting, a line at a time: Break now pauses here
print("wall: {0} pieces{1}".format(len(wall), "" if not any(w.is_alive() for w in workers) else " -- gave up after 2 minutes: the workers are stuck"))
