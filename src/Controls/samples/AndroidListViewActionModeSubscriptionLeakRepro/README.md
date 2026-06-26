# Android ListView ActionMode Subscription Leak Repro

This repro drives the real Android `CellAdapter` context-action menu rebuild path.

The control scenario opens and destroys a context-action menu once for each retained `Cell`. The suspect scenario repeatedly invalidates and rebuilds the active menu before destroy. `CellAdapter.OnDestroyActionModeImpl()` removes one matching handler from each `MenuItem`, but each rebuild added another `PropertyChanged`, `PropertyChanging`, and `ICommand.CanExecuteChanged` subscription. Long-lived `Cell.ContextActions` therefore retain disposed adapters and their payloads.

Run:

```sh
dotnet build src/Controls/samples/AndroidListViewActionModeSubscriptionLeakRepro/AndroidListViewActionModeSubscriptionLeakRepro.csproj -f net10.0-android -p:UseMaui=false -p:IncludeAndroidTargetFrameworks=true -p:EmbedAssembliesIntoApk=true
```

After installing and launching the APK, read `/data/data/com.microsoft.maui.androidlistviewactionmodesubscriptionleakrepro/files/autorun-results.txt`.
