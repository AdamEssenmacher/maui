# iOS Shell Left Bar Button Accessibility Identifier Retention Repro

This repro proves that retained iOS/Mac Catalyst Shell left bar button peers keep their last accessibility identifier payload after the managed image-source `AutomationId` value is cleared.

The Shell image-source path keeps image sources alive in both runs. This repro isolates the additional native `AccessibilityIdentifier` payload by comparing current MAUI behavior with explicit native identifier clearing.

Static path:

- `ShellPageRendererTracker.UpdateLeftToolbarItems()` assigns `ImageSource.AutomationId` to the native left bar button `AccessibilityIdentifier` when a Shell back/flyout image source is present.
- Tracker disposal clears Shell/page events and native navigation slots, but it does not clear retained native bar-button accessibility identifier state.

Run:

```bash
dotnet build src/Controls/samples/IosShellLeftBarButtonAccessibilityIdRetentionRepro/IosShellLeftBarButtonAccessibilityIdRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
artifacts/bin/IosShellLeftBarButtonAccessibilityIdRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/IosShellLeftBarButtonAccessibilityIdRetentionRepro.app/Contents/MacOS/IosShellLeftBarButtonAccessibilityIdRetentionRepro
```

The result file is written to `/tmp/ios-shell-leftbarbutton-accessibilityid-retention-results.txt`.
