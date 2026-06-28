# iOS FlyoutPage Left Bar Button Accessibility Retention Repro

This sample proves that retained legacy iOS/Mac Catalyst FlyoutPage left bar button peers keep native accessibility label and hint payloads after the managed accessibility values are cleared.

Static path:

- `NavigationRenderer.SetFlyoutLeftBarButton()` creates the native left `UIBarButtonItem` for a legacy `FlyoutPage`.
- It maps `AutomationProperties.HelpText` and `AutomationProperties.Name` from the `FlyoutPage` into `UIBarButtonItem.AccessibilityHint` and `AccessibilityLabel`.
- Renderer cleanup does not clear retained native bar-button accessibility text.

Run:

```sh
dotnet build src/Controls/samples/IosFlyoutPageLeftBarButtonAccessibilityRetentionRepro/IosFlyoutPageLeftBarButtonAccessibilityRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
artifacts/bin/IosFlyoutPageLeftBarButtonAccessibilityRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/IosFlyoutPageLeftBarButtonAccessibilityRetentionRepro.app/Contents/MacOS/IosFlyoutPageLeftBarButtonAccessibilityRetentionRepro
```

The app writes the result to `/tmp/ios-flyoutpage-leftbarbutton-accessibility-retention-results.txt`.
