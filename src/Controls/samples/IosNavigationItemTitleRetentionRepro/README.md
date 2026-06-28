# iOS NavigationItem Title Retention Repro

This Mac Catalyst repro exercises the iOS/Mac Catalyst compatibility `NavigationRenderer` page-controller title path. `ParentingViewController.UpdateTitleArea()` assigns `NavigationItem.Title` from `Page.Title` and creates a `BackBarButtonItem` with `NavigationPage.BackButtonTitle`.

The autorun scenario creates 96 navigation page controllers with 128 KiB generated page titles and 128 KiB generated back-button titles, retains each native `UINavigationItem`, disconnects the page controller, and verifies that MAUI pages and renderers collect. The control path explicitly clears `UINavigationItem.Title` and `BackBarButtonItem.Title` before retaining; the current MAUI path leaves both native title slots assigned.

Run:

```bash
dotnet run --project src/Controls/samples/IosNavigationItemTitleRetentionRepro/IosNavigationItemTitleRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

Results are written to `/tmp/ios-navigationitem-title-retention-results.txt`.
