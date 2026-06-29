# Android ListView ActionMode Menu State Retention Repro

This repro drives the real Android compatibility `CellAdapter` context-action menu creation path.

The control scenario clears retained native `IMenuItem` title, content-description, and icon slots before ActionMode teardown. The current MAUI scenario lets `CellAdapter.OnDestroyActionModeImpl()` remove managed subscriptions, but it does not clear the native ActionMode menu item state. Retained native menu item peers keep copied action labels, automation IDs, and decoded icon drawables after the `Cell`, managed `MenuItem`s, image sources, adapter, and fake cell handler all collect.

Run:

```sh
dotnet build src/Controls/samples/AndroidListViewActionModeMenuStateRetentionRepro/AndroidListViewActionModeMenuStateRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:EmbedAssembliesIntoApk=true -p:AndroidFastDeploymentType= -m:1 -nr:false -v:minimal -clp:Summary
```

After installing and launching the APK, read `/data/data/com.microsoft.maui.androidlistviewactionmodemenustateretentionrepro/files/autorun-results.txt`.
