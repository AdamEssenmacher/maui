# Mac Catalyst Compatibility Platform Dialog Retention Repro

This repro mirrors the obsolete compatibility iOS dialog implementation in
`src/Compatibility/Core/src/iOS/Platform.cs`. That implementation creates
`UIAlertController` dialogs, adds `UIAlertAction` callbacks with
`CreateActionWithWindowHide`, and captures both the dialog arguments and the
temporary compatibility `UIWindow` in each native action callback.

The repro intentionally does not present the dialogs. It constructs the same
native alert/action/window graph, completes the argument tasks, keeps only the
native alert peers alive, and then verifies which managed objects remain alive
through the native callback graph.

Run from the repository root:

```sh
dotnet build src/Controls/samples/MacCatalystCompatPlatformDialogRetentionRepro/MacCatalystCompatPlatformDialogRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false -v:minimal -clp:Summary
artifacts/bin/MacCatalystCompatPlatformDialogRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/MacCatalystCompatPlatformDialogRetentionRepro.app/Contents/MacOS/MacCatalystCompatPlatformDialogRetentionRepro
```

The app writes its result to
`/tmp/maccatalyst-compat-platform-dialog-retention-results.txt`.
