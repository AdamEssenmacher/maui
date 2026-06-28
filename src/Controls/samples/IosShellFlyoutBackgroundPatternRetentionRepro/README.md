# iOS Shell Flyout Background Pattern Retention Repro

This sample demonstrates that iOS/Mac Catalyst `ShellFlyoutContentRenderer`
leaves the generated native pattern background assigned after teardown when
`Shell.FlyoutBackground` is a brush.

The control run forces the same `UpdateBackground()` path, then clears
`UIView.BackgroundColor` before retaining the native flyout view. The current
MAUI run tears down managed Shell renderer bookkeeping but leaves the native
pattern color assigned.

Run:

```bash
dotnet run --project src/Controls/samples/IosShellFlyoutBackgroundPatternRetentionRepro/IosShellFlyoutBackgroundPatternRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The result is written to `/tmp/ios-shell-flyout-background-pattern-retention-results.txt`.
