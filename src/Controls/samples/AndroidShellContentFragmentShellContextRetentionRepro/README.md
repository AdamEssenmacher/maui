# Android ShellContentFragment ShellContext Retention Repro

This repro proves that disposed Android compatibility `ShellContentFragment` instances retain their `IShellContext` through the readonly `_shellContext` field. In current MAUI, `Dispose()`/`DisposePage()` tears down page-related state but leaves the context assigned, so a retained disposed fragment can keep the Shell context and its window-scoped service graph alive.

The sample creates 96 disposed fragments, each with a synthetic Shell context containing a `MauiContext` service provider with a 1 MiB payload service. The control run clears the private `_shellContext` field by reflection after disposal; the current run leaves MAUI cleanup as-is. Pages are tracked separately to show this is not the already-fixed removed-page disposal leak.

Results are written to `files/autorun-results.txt`.

## Build

```sh
dotnet build src/Controls/samples/AndroidShellContentFragmentShellContextRetentionRepro/AndroidShellContentFragmentShellContextRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:EmbedAssembliesIntoApk=true -p:AndroidFastDeploymentType= -m:1 -nr:false -v:minimal -clp:Summary
```
