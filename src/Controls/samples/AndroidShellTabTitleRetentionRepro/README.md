# Android Shell Tab Title Retention Repro

This repro exercises Android Shell title-copy paths that assign generated `ShellSection.Title` and `ShellContent.Title` values into native Material tab peers:

- `ShellItemRenderer` builds bottom-tab data from `ShellSection.Title` and calls `BottomNavigationViewUtils.SetupMenu()`, which assigns Android `IMenuItem` titles.
- `ShellSectionRenderer` implements `TabLayoutMediator.ITabConfigurationStrategy.OnConfigureTab()` and assigns `ShellContent.Title` into `TabLayout.Tab` text.

The app runs a control pass that clears native title slots before retaining the native peers, then runs the current MAUI behavior without the explicit clear. It writes `autorun-results.txt` under the app's private files directory and exits.

Build:

```sh
dotnet build src/Controls/samples/AndroidShellTabTitleRetentionRepro/AndroidShellTabTitleRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:EmbedAssembliesIntoApk=true -p:AndroidFastDeploymentType= -m:1 -nr:false -v:minimal -clp:Summary
```
