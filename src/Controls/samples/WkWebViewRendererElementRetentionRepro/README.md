# WkWebViewRenderer Element Retention Repro

This sample proves that disposed legacy iOS/Mac Catalyst `WkWebViewRenderer` peers keep their last MAUI `WebView` assigned through the renderer `Element` property.

The autorun keeps disposed native renderer peers alive in both scenarios to model a native peer or app-owned native view outliving disposal. The control scenario clears only the stale `Element` backing field after dispose. Current MAUI leaves `Element` assigned because `WkWebViewRenderer.SetElement(null)` does not actually write `Element = null`.

Run:

```sh
dotnet run --project src/Controls/samples/WkWebViewRendererElementRetentionRepro/WkWebViewRendererElementRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes results to the Mac Catalyst process temp directory as `wkwebviewrenderer-element-retention-results.txt`.
