# TabbedRenderer Element Retention Repro

This sample proves that disposed legacy iOS/Mac Catalyst `TabbedRenderer` peers keep their last MAUI `TabbedPage` assigned through the renderer `Element` property.

The autorun keeps disposed native renderer peers alive in both scenarios to model a native `UITabBarController` peer outliving disposal. The control scenario clears only the stale `Element` backing field after dispose. Current MAUI leaves `Element` assigned because `TabbedRenderer.Dispose(bool)` detaches events but never clears the renderer's virtual view reference.

Run:

```sh
dotnet run --project src/Controls/samples/TabbedRendererElementRetentionRepro/TabbedRendererElementRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes results to the Mac Catalyst process temp directory as `tabbedrenderer-element-retention-results.txt`.
