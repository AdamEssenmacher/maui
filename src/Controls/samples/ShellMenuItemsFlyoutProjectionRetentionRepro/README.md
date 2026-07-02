# Shell MenuItems Flyout Projection Retention Repro

This sample proves that generated Shell flyout groups can keep removed `ShellContent.MenuItems` alive after public one-by-one removal.

The repro creates live Shell owners, generates flyout groups that include payload-bearing `MenuItem` entries, removes every menu item with `MenuItems.RemoveAt`, and then compares:

- a control path that reflectively clears and regenerates `ShellFlyoutItemsManager._lastGeneratedFlyoutItems` after removal
- current MAUI behavior, which keeps the old generated flyout grouping cached

Expected result:

```text
Result: PROVEN
Current MAUI behavior retained all removed MenuItems and payload buffers.
```

Retained graph:

```text
Live Shell -> ShellFlyoutItemsManager generated flyout collections -> removed MenuItem -> BindingContext payload
```

Run:

```bash
dotnet build src/Controls/samples/ShellMenuItemsFlyoutProjectionRetentionRepro/ShellMenuItemsFlyoutProjectionRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
open -W "artifacts/bin/ShellMenuItemsFlyoutProjectionRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/Shell MenuItems Flyout Projection Retention.app" --args --results=/tmp/shell-menuitems-flyout-projection-retention-results.txt
cat /tmp/shell-menuitems-flyout-projection-retention-results.txt
```
