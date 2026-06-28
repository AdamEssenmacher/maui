# iOS VisualElementRenderer Accessibility Retention Repro

This Mac Catalyst sample proves that retained legacy `VisualElementRenderer<T>` native peers keep accessibility string payloads assigned after renderer disposal.

Static path:

- `VisualElementRenderer<T>.SetElement()` copies `VisualElement.AutomationId` into `UIView.AccessibilityIdentifier`.
- The same path calls the compatibility accessibility helpers, which copy `AutomationProperties.Name` and `AutomationProperties.HelpText` into `UIView.AccessibilityLabel` and `AccessibilityHint`.
- `VisualElementRenderer<T>.Dispose()` calls `SetElement(null)`, but the null-element path does not clear those native slots.

Run:

```bash
dotnet build src/Controls/samples/IosVisualElementRendererAccessibilityRetentionRepro/IosVisualElementRendererAccessibilityRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
artifacts/bin/IosVisualElementRendererAccessibilityRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/IosVisualElementRendererAccessibilityRetentionRepro.app/Contents/MacOS/IosVisualElementRendererAccessibilityRetentionRepro
```

The app writes the result to `/tmp/ios-visualelementrenderer-accessibility-retention-results.txt`.
