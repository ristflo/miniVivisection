#! python 3
"""Used by 09_step_over_other_files_tools.py: column_grid() there ends with a call to
columns_at() here -- the case where stepping over across files used to run on."""
import Rhino.Geometry as rg


def columns_at(bases, height):
    columns = []
    for base in bases:
        columns.append(rg.LineCurve(base, base + rg.Vector3d(0, 0, height)))
    return columns
