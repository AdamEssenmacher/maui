# Android Shell ContainerView MauiContext Retention Repro

This repro isolates the Android Shell compatibility `ContainerView`/`ShellViewRenderer` context-retention path.

`ContainerView.Dispose(bool)` tears down the hosted MAUI view and handler, but `ContainerView` keeps its readonly `_mauiContext` field and keeps its `_shellContentView` object. `ShellViewRenderer.TearDown()` clears its hosted view, handler, platform view, and weak Android context, but it also keeps its readonly `_mauiContext`. If disposed native `ContainerView` peers survive Android cleanup timing, those fields keep old window-scoped service providers and services alive after the hosted view collects.

The app creates 96 disposed `ContainerView` peers with a synthetic `MauiContext` containing a 1 MiB payload service. The control run keeps the same native peers alive but clears both context fields by reflection; the current run leaves MAUI cleanup as-is. Results are written to `files/autorun-results.txt`.

Run with:

```sh
dotnet build src/Controls/samples/AndroidShellContainerViewMauiContextRetentionRepro/AndroidShellContainerViewMauiContextRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:EmbedAssembliesIntoApk=true -p:AndroidFastDeploymentType= -m:1 -nr:false -v:minimal -clp:Summary
```
