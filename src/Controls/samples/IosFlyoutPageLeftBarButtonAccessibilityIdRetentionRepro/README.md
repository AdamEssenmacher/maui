# iOS FlyoutPage Left Bar Button Accessibility Identifier Retention Repro

This sample proves that retained legacy iOS/Mac Catalyst FlyoutPage left bar button peers keep native accessibility identifier payloads after the managed `AutomationId` value is cleared.

The known legacy left-bar action sibling keeps the `FlyoutPage` graph alive in both runs. This repro isolates the additional native `AccessibilityIdentifier` payload by comparing current MAUI behavior with explicit native identifier clearing.

Static path:

- `NavigationRenderer.SetFlyoutLeftBarButton()` creates the native left `UIBarButtonItem` for a legacy `FlyoutPage`.
- It maps `FlyoutPage.AutomationId` into `UIBarButtonItem.AccessibilityIdentifier` as `btn_{AutomationId}`.
- Renderer cleanup does not clear retained native bar-button accessibility identifier text.

Run:

```sh
dotnet build src/Controls/samples/IosFlyoutPageLeftBarButtonAccessibilityIdRetentionRepro/IosFlyoutPageLeftBarButtonAccessibilityIdRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
artifacts/bin/IosFlyoutPageLeftBarButtonAccessibilityIdRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/IosFlyoutPageLeftBarButtonAccessibilityIdRetentionRepro.app/Contents/MacOS/IosFlyoutPageLeftBarButtonAccessibilityIdRetentionRepro
```

The app writes the result to `/tmp/ios-flyoutpage-leftbarbutton-accessibilityid-retention-results.txt`.
