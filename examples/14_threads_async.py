#! python 3
# async:true
"""14 -- Debug threads: V°Threads, thread names in conditions, watches in every thread

Needs: the line '# async:true' above: the script runs on a thread of its own, Rhino stays free.

The bug
    Four workers each place 60 points on their own circle and count them in a shared
    total. The total should be 240, but it comes out lower, and differs from run to run.

Try this
    1. Set a breakpoint on the line marked BREAKPOINT. Right-click it > Breakpoint
       Condition… >  thread_name == 'Worker-3' and i == 20  > Enter.
       (thread_name, thread_id and is_main_thread work in every condition.)
    2. Debug (F5). Worker-3 pauses; the other threads stop at their next line.
       (Without the condition, Worker-1 pauses the first time it reaches the
       breakpoint -- before the script has even started Worker-2: a paused thread
       holds Python's lock, so no other thread can start or run until you continue.)
    3. Open the V°Threads tab: one row per thread -- Worker-3 'paused here', the other
       workers 'stopped by the debugger', the script's own thread 'waiting in join()'.
       Expand a row for its stack; double-click it to see its line in the code.
    4. In the V°Watch tab, watch  pt . With more than one thread, a watch is evaluated in every
       thread by itself: each worker's current point is drawn, labeled pt @Worker-1,
       pt @Worker-2, ... (right-click > In every thread turns it off or on for a watch).
       Expand the  pt  row: one row per thread with its point. Select a row: that
       thread's point is highlighted in Rhino; click a point in Rhino: its row is
       selected. Bake works on a single thread's row. The eye on a thread's row hides
       or shows that thread's point; the paused thread's dot says (paused).
    5. Remove the condition (right-click > Remove Condition), then right-click the
       line > Log Message… >  read {current}, total now {total}  > Enter. F5: the
       Terminal shows [Worker-2] read 17, total now 18 ... -- two workers reading the
       same total is the bug: both write back the same number, one count is lost.
    6. Fix it: put the three lines marked RACE under  with lock:  -- the total is 240.
    7. Delete the line '# async:true' and debug with a breakpoint in work(): Rhino
       would freeze (the script runs on Rhino's own thread). miniVivisection skips
       that pause and says why in the Terminal and V°Log (the freeze guard).

Without miniVivisection
    Prints from four threads, mixed up; no way to see where each thread is, or the
    values in the threads that aren't paused.
"""
import math
import threading
import time
import Rhino.Geometry as rg

total = 0
points = []
lock = threading.Lock()  # for the fix


def work(index, count):
    global total
    radius = 5.0 * (index + 1)
    for i in range(count):
        angle = 2 * math.pi * i / count
        pt = rg.Point3d(radius * math.cos(angle), radius * math.sin(angle), 0)
        points.append(pt)
        current = total            # RACE: read
        time.sleep(0)              # RACE: another thread may run here
        total = current + 1        # RACE: write  <-- BREAKPOINT


workers = [threading.Thread(target=work, args=(k, 60), name="Worker-%d" % (k + 1)) for k in range(4)]
for worker in workers:
    worker.start()
for worker in workers:
    worker.join()
print("total = {0} (expected 240), {1} points".format(total, len(points)))
