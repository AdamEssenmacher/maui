# IosShellItemRendererContextRetentionRepro

This focused Mac Catalyst repro retains disposed `ShellItemRenderer` peers and compares current MAUI cleanup against a control that clears only the private readonly `_context` field after the real dispose path.

The retained context points to a realistic Shell graph with a window-scoped `MauiContext` and a 1 MiB payload service. If the retained renderer keeps `_context`, it also keeps the old Shell, Shell item, Shell section, service provider, and payload buffer alive.

