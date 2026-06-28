# iOS View Accessibility Text Retention Repro

This repro proves that retained iOS/Mac Catalyst native `UIView` peers keep large accessibility strings assigned after current MAUI view handler disconnect.

Static paths:

- `src/Core/src/Handlers/View/ViewHandler.cs` maps `IView.AutomationId` through `MapAutomationId()` and maps `IView.Semantics` through `MapSemantics()`.
- `src/Core/src/Platform/iOS/ViewExtensions.cs` assigns `UIView.AccessibilityIdentifier = view.AutomationId`.
- `src/Core/src/Platform/iOS/SemanticExtensions.cs` assigns `UIView.AccessibilityLabel = semantics.Description` and `UIView.AccessibilityHint = semantics.Hint`.
- `src/Core/src/Handlers/View/ViewHandlerOfT.cs` has an empty default `DisconnectHandler()`, so generic view cleanup does not clear those retained native string slots.

Run:

```bash
dotnet build src/Controls/samples/IosViewAccessibilityTextRetentionRepro/IosViewAccessibilityTextRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
artifacts/bin/IosViewAccessibilityTextRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/IosViewAccessibilityTextRetentionRepro.app/Contents/MacOS/IosViewAccessibilityTextRetentionRepro
```

The result file is written to `/tmp/ios-view-accessibility-text-retention-results.txt`.
