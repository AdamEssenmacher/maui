# Android ShellItemRenderer State Retention Repro

This repro proves that retained Android compatibility `ShellItemRenderer` fragments keep Shell state after cleanup. The current renderer disconnect path clears `ShellContext` but leaves `ShellItem`, while the fragment destroy path leaves both `ShellContext` and `ShellItem`.

The sample creates 48 disconnected renderers and 48 destroyed renderers with realistic 1 MiB payloads attached to their Shell item graphs. Destroyed renderers also carry a synthetic Shell context with a payload-backed `MauiContext`. The control run clears the private `ShellItemRendererBase` backing fields by reflection after cleanup; the current run leaves MAUI cleanup as-is.

Results are written to `files/autorun-results.txt`.

## Build

```sh
dotnet build src/Controls/samples/AndroidShellItemRendererStateRetentionRepro/AndroidShellItemRendererStateRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:EmbedAssembliesIntoApk=true -p:AndroidFastDeploymentType= -m:1 -nr:false -v:minimal -clp:Summary
```
