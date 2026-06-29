# Mac Catalyst Picker Alert Action Retention Repro

This Mac Catalyst repro exercises the current `PickerHandler.DisplayAlert` path. The Mac Catalyst picker creates a native `UIAlertController` with a `UIAlertAction` callback that captures the `PickerHandler`; after handler disconnect, `ElementHandler` clears `VirtualView` and `PlatformView` but leaves `MauiContext` assigned.

The harness uses the real handler and private `DisplayAlert` method, but supplies a detached `UIWindow` through the throwaway context so the native alert/action graph is created without presenting many popovers. It retains the generated native alert/action peers and compares current disconnect behavior with a control that reflectively clears the disconnected handler's `MauiContext`.

Run:

```sh
dotnet run --project src/Controls/samples/MacCatalystPickerAlertActionRetentionRepro/MacCatalystPickerAlertActionRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes the authoritative result to `/tmp/maccatalyst-picker-alert-action-retention-results.txt` and exits with code `0` only when the leak is proven.
