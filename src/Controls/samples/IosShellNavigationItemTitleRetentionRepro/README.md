# iOS Shell Navigation Item Title Retention Repro

This sample proves whether iOS/Mac Catalyst compatibility `ShellPageRendererTracker.UpdateTitle()` leaves assigned native `UINavigationItem.Title` state on retained native Shell navigation item peers. Each cycle creates a `Shell` page with a generated page title, routes it through the real Shell tracker title update path, keeps only the native `UINavigationItem` alive, and counts payload-sized native title slots after the Shell, page, tracker, and handlers are released.

The control run clears `UINavigationItem.Title` before retaining the native peer. The current run uses MAUI's Shell tracker cleanup.

Run:

```sh
dotnet run --project src/Controls/samples/IosShellNavigationItemTitleRetentionRepro/IosShellNavigationItemTitleRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes the result to `/tmp/ios-shell-navigationitem-title-retention-results.txt`.

Verified result:

```text
Cycles: 96
Payload per native title slot: 256 KiB
Leak proved: True

control:
  retained native navigation items: 96/96
  assigned payload-sized title slots: 0/96
  estimated retained native title MiB: 0.0
  alive trackers/shells/pages/shell handlers/page handlers: 0/0/0/0/0

current:
  retained native navigation items: 96/96
  assigned payload-sized title slots: 96/96
  estimated retained native title MiB: 24.0
  alive trackers/shells/pages/shell handlers/page handlers: 0/0/0/0/0

RESULT: PROVEN
```
