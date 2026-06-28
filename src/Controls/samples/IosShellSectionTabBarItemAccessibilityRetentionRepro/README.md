# iOS ShellSection Tab Bar Accessibility Retention Repro

This Mac Catalyst sample proves that retained Shell tab item peers keep native accessibility identifier payloads assigned by `ShellSectionRenderer.UpdateTabBarItem()`.

Static path:

- `ShellSectionRenderer.UpdateTabBarItem()` creates `new UITabBarItem(ShellSection.Title, image, null)`.
- It then assigns `TabBarItem.AccessibilityIdentifier = ShellSection.AutomationId ?? ShellSection.Title`.
- When `AutomationId` is absent, the generated Shell section title is copied into both the native tab title and accessibility identifier. This repro clears the native title in both runs, clears the managed `ShellSection.Title`, and counts only the retained native accessibility identifier slot.

Run:

```bash
dotnet build src/Controls/samples/IosShellSectionTabBarItemAccessibilityRetentionRepro/IosShellSectionTabBarItemAccessibilityRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
artifacts/bin/IosShellSectionTabBarItemAccessibilityRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/IosShellSectionTabBarItemAccessibilityRetentionRepro.app/Contents/MacOS/IosShellSectionTabBarItemAccessibilityRetentionRepro
```

The app writes the result to `/tmp/ios-shellsection-tabbaritem-accessibility-retention-results.txt`.
