# iOS Shell Left Bar Button Accessibility Retention Repro

This repro proves that retained iOS/Mac Catalyst Shell left bar button peers keep their last accessibility label and hint payloads after Shell/page objects are gone.

Static path:

- `ShellPageRendererTracker.UpdateLeftToolbarItems()` calls `SetAccessibilityHint()` and `SetAccessibilityLabel()` on the left bar button when the Shell back/flyout image source is present.
- Tracker disposal clears Shell/page events and native navigation slots, but it does not clear retained native bar-button accessibility text.

Run:

```bash
dotnet build src/Controls/samples/IosShellLeftBarButtonAccessibilityRetentionRepro/IosShellLeftBarButtonAccessibilityRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
artifacts/bin/IosShellLeftBarButtonAccessibilityRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/IosShellLeftBarButtonAccessibilityRetentionRepro.app/Contents/MacOS/IosShellLeftBarButtonAccessibilityRetentionRepro
```

The result file is written to `/tmp/ios-shell-leftbarbutton-accessibility-retention-results.txt`.
