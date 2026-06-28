# IosLabelRendererFormattedStringRetentionRepro

This Mac Catalyst repro proves that legacy iOS `LabelRenderer` keeps its private `_formatted`
field assigned after disposal when the disposed renderer peer is retained.

The control scenario clears `_formatted` after disposal. The current MAUI scenario leaves
`_formatted` assigned and verifies the `Label` collects while the `FormattedString` payload remains alive.
