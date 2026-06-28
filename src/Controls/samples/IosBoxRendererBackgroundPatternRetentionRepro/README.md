# IosBoxRendererBackgroundPatternRetentionRepro

This Mac Catalyst repro proves that legacy iOS `BoxRenderer` keeps generated brush background
pattern images alive after disposal when the disposed renderer peer is retained.

The control scenario clears the private `_colorToRenderer` pattern color after disposal.
The current MAUI scenario leaves the field assigned and verifies the `BoxView`, brush, and payload collect.
