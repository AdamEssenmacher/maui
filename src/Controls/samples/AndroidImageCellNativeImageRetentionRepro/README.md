# Android ImageCell native image retention repro

This repro exercises the legacy Android `ImageCellRenderer` path.

It retains native `BaseCellView` row peers after `CellRenderer` disconnect, clears the known C107 `_cell` back-reference in both scenarios, and compares current MAUI cleanup with explicit clearing of the native row `_imageSource` field and child `ImageView` drawable.
