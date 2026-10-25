#! python 3
"""Used by 09_step_over_other_files.py -- open that one and debug it."""
import importlib

import Rhino.Geometry as rg

grid = importlib.reload(importlib.import_module("09_step_over_other_files_grid_tools"))   # reload: picks up your edits


def column_grid(nx, ny, spacing):
    bases = []
    for i in range(1, nx):          # the bug: should be range(nx)
        for j in range(ny):
            bases.append(rg.Point3d(i * spacing, j * spacing, 0))
    return grid.columns_at(bases, 3.5)   # ends with a call into a third file
