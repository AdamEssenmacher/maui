# PhoneFlyoutPageRenderer Background Pattern Retention Repro

This sample demonstrates that the iOS/Mac Catalyst compatibility `PhoneFlyoutPageRenderer`
leaves a native `UIColor.FromPatternImage(...)` background assigned after the renderer is
disposed.

The control run forces the same renderer path, then explicitly clears `UIView.BackgroundColor`
before retaining the native peer. The current MAUI run disposes the renderer and clears the
managed `Element` path, but leaves the native pattern color assigned.

Run:

```bash
dotnet run --project src/Controls/samples/PhoneFlyoutPageBackgroundPatternRetentionRepro/PhoneFlyoutPageBackgroundPatternRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The result is written to `/tmp/phoneflyoutpage-background-pattern-retention-results.txt`.
