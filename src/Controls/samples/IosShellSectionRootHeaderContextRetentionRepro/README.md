# IosShellSectionRootHeaderContextRetentionRepro

This Mac Catalyst repro retains disposed `ShellSectionRootHeader` peers and compares current MAUI cleanup against a control that clears only the private readonly `_shellContext` field after the real dispose path.

The retained context points to a realistic Shell section with multiple ShellContent items and a 1 MiB window-scoped service payload behind `Shell.Handler.MauiContext`.

