# Android Shell Fragment MauiContext Retention Repro

This repro isolates two Android Shell compatibility fragment context-retention paths:

- `ShellFragmentStateAdapter.Dispose(bool)` clears `_shellSection`, `_items`, and `_createdShellContent`, but leaves `_mauiContext`.
- `ShellFragmentContainer` instances created by `ShellFragmentStateAdapter.CreateFragment()` keep `_mauiContext` after disposal.

The sample creates 48 disposed adapters and 48 disposed fragment containers with a synthetic `MauiContext` containing a 1 MiB payload service. The fragment-container path clears the adapter context in both runs so the retained fragment field is tested independently. The control run clears the relevant fragment/adapter context fields by reflection; the current run leaves MAUI cleanup as-is. Results are written to `files/autorun-results.txt`.

Run with:

```sh
dotnet build src/Controls/samples/AndroidShellFragmentMauiContextRetentionRepro/AndroidShellFragmentMauiContextRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:EmbedAssembliesIntoApk=true -p:AndroidFastDeploymentType= -m:1 -nr:false -v:minimal -clp:Summary
```
