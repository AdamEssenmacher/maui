# IndicatorView Template Disconnect Retention Repro

This sample checks whether iOS/Mac Catalyst `IndicatorViewHandler` disconnect leaves a templated native indicator subtree attached to retained `MauiPageControl` peers.

The repro compares current disconnect against an explicit cleanup control that disconnects the logical template tree, clears payload bindings, and disposes the native template subtree. Each templated indicator creates a custom native `UIView` carrying a 256 KiB payload to model real templated indicators with native image/cache state.

Run:

```sh
dotnet build src/Controls/samples/IndicatorViewTemplateDisconnectRetentionRepro/IndicatorViewTemplateDisconnectRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
artifacts/bin/IndicatorViewTemplateDisconnectRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/IndicatorViewTemplateDisconnectRetentionRepro.app/Contents/MacOS/IndicatorViewTemplateDisconnectRetentionRepro
```

The app writes results to `/tmp/ios-indicatorview-template-disconnect-results.txt`.
