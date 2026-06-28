# iOS ToolbarItem Accessibility Retention Repro

This repro proves that retained iOS/Mac Catalyst toolbar native peers keep their last accessibility text payloads after `ToolbarItem` objects are gone.

Static path:

- `src/Controls/src/Core/Compatibility/iOS/Extensions/ToolbarItemExtensions.cs` copies `ToolbarItem.AutomationId` into `AccessibilityIdentifier` for primary, secondary custom-view, and secondary overflow native peers.
- The same file calls `SetAccessibilityHint()` and `SetAccessibilityLabel()` for primary and secondary custom-view `UIBarButtonItem` peers.
- Disposal unsubscribes `ToolbarItem.PropertyChanged`, but it does not clear retained native accessibility slots.

Run:

```bash
dotnet build src/Controls/samples/IosToolbarItemAccessibilityRetentionRepro/IosToolbarItemAccessibilityRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
artifacts/bin/IosToolbarItemAccessibilityRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/IosToolbarItemAccessibilityRetentionRepro.app/Contents/MacOS/IosToolbarItemAccessibilityRetentionRepro
```

The result file is written to `/tmp/ios-toolbaritem-accessibility-retention-results.txt`.
