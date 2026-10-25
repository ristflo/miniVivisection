#! python 3
r"""12 -- Look at files from the paused script: the V°PowerShell tab

The bug
    The script reads survey points from a CSV export and finds 0 points, with
    no error. Is the file empty? Missing? Something else?

Try this
    1. Set a breakpoint on the line marked BREAKPOINT, then Debug (F5).
       The Terminal prints the file's path.
    2. Open the V°PowerShell tab and look at the file:
           Get-Content $env:TEMP\minivivisection_survey.csv -TotalCount 5
       The values are separated by commas; the script splits on semicolons.
           Import-Csv $env:TEMP\minivivisection_survey.csv | Measure-Object
       25 rows.
    3. Fix it without stopping, in the V°Python tab:  separator = ","
       Then F5: 25 points.
    4. Anything else in PowerShell works too: dir, git status, starting tools.
       The path and profile are in V°Settings.

Without miniVivisection
    A separate console window, or a print() of the file's first lines, and
    another run.
"""
import os
import tempfile

path = os.path.join(tempfile.gettempdir(), "minivivisection_survey.csv")
with open(path, "w") as export:   # set-up: an "export" from another program
    export.write("x,y,z\n")
    for k in range(25):
        export.write("{0},{1},{2}\n".format(k * 2.0, (k % 5) * 3.0, 0.5 * k))
print("reading " + path)

separator = ";"
points = []
with open(path) as survey:   # <-- BREAKPOINT
    for line in survey.read().splitlines()[1:]:
        parts = line.split(separator)
        if len(parts) == 3:
            points.append([float(v) for v in parts])
print("{0} points read".format(len(points)))
