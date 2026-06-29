# Mac Catalyst AlertManager Dialog Retention Repro

This Mac Catalyst repro mirrors the iOS/Mac Catalyst `AlertManager.AlertRequestHelper` construction paths for `DisplayAlert`, `DisplayActionSheet`, and `DisplayPrompt`. Those paths create `UIAlertController` instances with `UIAlertAction` callbacks and prompt text-field delegates that capture the internal alert argument objects and generated action labels.

The harness constructs the same native alert/action graphs without presenting modal UI, completes the argument tasks, then keeps the native alert/action peers alive. The control run keeps retained native alert peers alive with short action-sheet labels and clears alert/prompt payload strings from the argument objects and native text fields. The current MAUI run leaves the payload strings assigned.

Run:

```sh
dotnet run --project src/Controls/samples/MacCatalystAlertManagerDialogRetentionRepro/MacCatalystAlertManagerDialogRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes the authoritative result to `/tmp/maccatalyst-alertmanager-dialog-retention-results.txt` and exits with code `0` only when the leak is proven.
