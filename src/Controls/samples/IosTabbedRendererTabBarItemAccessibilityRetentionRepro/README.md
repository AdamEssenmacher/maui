# iOS TabbedRenderer Tab Bar Accessibility Retention Repro

This Mac Catalyst sample proves that retained legacy `TabbedRenderer` tab item peers keep native accessibility identifier payloads assigned from child page `AutomationId`.

Static path:

- `TabbedRenderer.SetTabBarItem()` creates `renderer.ViewController.TabBarItem = new UITabBarItem(page.Title, image, selectedImage)`.
- It sets `AccessibilityIdentifier = page.AutomationId` on that native tab item.
- The repro uses short titles, generated payload-sized automation IDs, clears native titles in both runs, then compares current cleanup with explicit native accessibility identifier clearing.

Run:

```bash
dotnet build src/Controls/samples/IosTabbedRendererTabBarItemAccessibilityRetentionRepro/IosTabbedRendererTabBarItemAccessibilityRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
artifacts/bin/IosTabbedRendererTabBarItemAccessibilityRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/IosTabbedRendererTabBarItemAccessibilityRetentionRepro.app/Contents/MacOS/IosTabbedRendererTabBarItemAccessibilityRetentionRepro
```

The app writes the result to `/tmp/ios-tabbedrenderer-tabbaritem-accessibility-retention-results.txt`.
