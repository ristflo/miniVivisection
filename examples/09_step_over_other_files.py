#! python 3
"""09 -- Step through code in other files

The bug
    A 4 x 3 column grid should give 12 columns; the script reports 9. The grid
    comes from column_grid() in 09_step_over_other_files_tools.py, which ends
    by calling columns_at() in a third file, 09_step_over_other_files_grid_tools.py
    (keep the three files in the same folder).

Try this
    1. Set a breakpoint on the line marked BREAKPOINT, then Debug (F5).
    2. Step over (F10): the call runs and the debugger stops at the next line of
       this file. Hover  columns : 9 items. In the V°Watch tab, watch  columns  to see
       which row is missing.
    3. Stop and debug again. This time step into (F11) the call: the tools file
       opens. Step with F10 and read the line-end values: the first  i  is 1,
       not 0. Step out (Shift+F11) back here.
    4. Fix it in the tools file: range(nx) instead of range(1, nx).

Without miniVivisection
    In Rhino 8's Script Editor, stepping over a call whose function ends by
    calling into a third file -- column_grid() ends with
    return grid.columns_at(...) -- runs the rest of the script without
    stopping. miniVivisection fixes that.
"""
import importlib
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
tools = importlib.reload(importlib.import_module("09_step_over_other_files_tools"))   # reload: picks up your edits

columns = tools.column_grid(4, 3, 6.0)   # <-- BREAKPOINT
print("{0} columns".format(len(columns)))
