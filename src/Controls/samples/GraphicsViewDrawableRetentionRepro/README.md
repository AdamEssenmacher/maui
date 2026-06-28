# GraphicsView Drawable Retention Repro

This sample proves that iOS/Mac Catalyst `GraphicsViewHandler` disconnect leaves native drawable state assigned on retained `PlatformTouchGraphicsView` peers. `PlatformTouchGraphicsView.Disconnect()` clears touch tracking, but inherited `PlatformGraphicsView.Drawable` and the default renderer's drawable remain assigned.

The repro keeps disposed native peers alive in both scenarios, then compares current disconnect against an explicit `platformView.Drawable = null` control. Each drawable carries a 1 MiB payload to model real dashboards or drawing surfaces with cached drawing state.

Run:

```sh
dotnet run --project src/Controls/samples/GraphicsViewDrawableRetentionRepro/GraphicsViewDrawableRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes results to `/tmp/graphicsview-drawable-retention-results.txt`.
