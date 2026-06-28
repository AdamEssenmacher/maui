# NavigationRenderer Element Retention Repro

This sample proves that disposed legacy iOS/Mac Catalyst `NavigationRenderer` peers keep their last MAUI `NavigationPage` assigned through the renderer `Element` property.

The autorun keeps disposed native renderer peers alive in both scenarios to model a native `UINavigationController` peer outliving disposal. The control scenario clears only the stale `Element` backing field after dispose. Current MAUI leaves `Element` assigned because `NavigationRenderer.Dispose(bool)` detaches navigation state but never clears the renderer's virtual view reference.

The sample intentionally does not force `ViewDidLoad()`; the harness initializes the private secondary toolbar field needed by `Dispose()` so the proof isolates this stale-field path instead of the separate pending-navigation-task leak.

Run:

```sh
dotnet run --project src/Controls/samples/NavigationRendererElementRetentionRepro/NavigationRendererElementRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes results to the Mac Catalyst process temp directory as `navigationrenderer-element-retention-results.txt`.
