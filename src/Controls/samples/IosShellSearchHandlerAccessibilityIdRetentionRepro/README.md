# iOS Shell SearchHandler Accessibility Identifier Retention Repro

This sample proves that retained iOS/Mac Catalyst Shell search-bar peers keep their last accessibility identifier payload after the managed `SearchHandler.AutomationId` value is cleared.

Static path:

- `ShellPageRendererTracker.UpdateAutomationId()` assigns `SearchHandler.AutomationId` to `UISearchBar.AccessibilityIdentifier`.
- `DettachSearchController()` removes most search-bar hooks and clears the search controller reference, but it does not clear retained native search-bar accessibility identifier state.

The sibling C229 search text and `SearchButtonClicked` event paths are cleared in both runs. This repro isolates the additional native `AccessibilityIdentifier` payload by comparing current MAUI behavior with explicit native identifier clearing.

Run:

```bash
dotnet build src/Controls/samples/IosShellSearchHandlerAccessibilityIdRetentionRepro/IosShellSearchHandlerAccessibilityIdRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
artifacts/bin/IosShellSearchHandlerAccessibilityIdRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/IosShellSearchHandlerAccessibilityIdRetentionRepro.app/Contents/MacOS/IosShellSearchHandlerAccessibilityIdRetentionRepro
```

The app writes the result to `/tmp/ios-shell-searchhandler-accessibilityid-retention-results.txt`.
