# iOS Page UIViewController Title Retention Repro

This Mac Catalyst repro exercises the current iOS/Mac Catalyst `PageHandler` title path. `PageHandler.MapTitle()` calls `UIViewController.UpdateTitle(page)`, which assigns `UIViewController.Title = ITitledElement.Title`.

The autorun scenario creates 96 page handlers with 256 KiB generated page titles, retains each native `UIViewController` peer, disconnects the handler, and verifies that the MAUI pages, page content, and handlers collect. The control path explicitly clears the retained native `UIViewController.Title` before retaining; the current MAUI path leaves the native title assigned after handler disconnect.

Run:

```bash
dotnet run --project src/Controls/samples/IosPageTitleRetentionRepro/IosPageTitleRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

Results are written to `/tmp/ios-page-title-retention-results.txt`.
