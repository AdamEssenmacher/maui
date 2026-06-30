# Android ShellSectionRenderer State Retention Repro

This repro proves that retained Android compatibility `ShellSectionRenderer` fragments keep Shell section/context state after disposal. The sample intentionally does not call `OnCreateView()`, so the `ViewPager2` callback and `TabLayoutMediator` leak tracked separately by C328 are not part of this proof.

The sample creates 96 disposed renderers with a synthetic `IShellContext` carrying a payload-backed `MauiContext` and a `ShellSection` carrying a 1 MiB payload. The control run clears the private `_shellContext` field and the public `ShellSection` property by reflection/assignment after disposal; the current run leaves MAUI cleanup as-is.

Results are written to `files/autorun-results.txt`.

## Build

```sh
dotnet build src/Controls/samples/AndroidShellSectionRendererStateRetentionRepro/AndroidShellSectionRendererStateRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:EmbedAssembliesIntoApk=true -p:AndroidFastDeploymentType= -m:1 -nr:false -v:minimal -clp:Summary
```
