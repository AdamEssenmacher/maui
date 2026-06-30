# iOS VisualElementRenderer MauiContext retention repro

This Mac Catalyst repro checks whether retained disposed current Controls compatibility `VisualElementRenderer<T>` peers keep old window-scoped `MauiContext` service graphs alive after their virtual views collect.

It uses `FrameRenderer` as the concrete `VisualElementRenderer<Frame>` subtype and compares current MAUI with a control that clears only the inherited `VisualElementRenderer<Frame>._mauiContext` field after renderer disconnect/dispose while retaining the same renderer peers.

Run:

```bash
dotnet build src/Controls/samples/IosVisualElementRendererMauiContextRetentionRepro/IosVisualElementRendererMauiContextRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
dotnet run --project src/Controls/samples/IosVisualElementRendererMauiContextRetentionRepro/IosVisualElementRendererMauiContextRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -p:_DeviceName=:v2:udid=00000000-0000-0000-0000-000000000000
```

The app writes the result to `/tmp/ios-visualelementrenderer-mauicontext-retention-results.txt`.
