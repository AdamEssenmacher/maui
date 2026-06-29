# Android Alert Dialog String Retention Repro

This repro exercises Android dialog string-copy paths used by `AlertManager.AlertRequestHelper`:

- alert title/message/accept/cancel strings assigned through `AlertDialog.SetTitle`, `SetMessage`, and `SetButton`
- action-sheet title/button strings assigned through `AlertDialog.Builder.SetTitle`, `SetItems`, and button setters
- prompt title/message/accept/cancel strings plus native `AppCompatEditText` hint/text assigned through `SetView`

The app neutralizes click callbacks in both runs so the measurement isolates native dialog string state. It writes `autorun-results.txt` under the app's private files directory and exits.

Build:

```sh
dotnet build src/Controls/samples/AndroidAlertDialogStringRetentionRepro/AndroidAlertDialogStringRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:EmbedAssembliesIntoApk=true -p:AndroidFastDeploymentType= -m:1 -nr:false -v:minimal -clp:Summary
```
