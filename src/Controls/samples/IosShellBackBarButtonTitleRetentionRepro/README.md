# iOS Shell Back Bar Button Title Retention Repro

This sample proves whether iOS/Mac Catalyst compatibility `ShellPageRendererTracker.UpdateBackButtonTitle()` leaves assigned native `BackBarButtonItem.Title` state on retained previous `UINavigationItem` peers. Each cycle creates a `Shell` page with `BackButtonBehavior.TextOverride`, routes it through the real Shell tracker back-button-title path in a two-controller native navigation stack, keeps only the previous native `UINavigationItem` alive, and counts payload-sized native back-button title slots after the Shell, page, tracker, behavior, and handlers are released.

The control run clears `BackBarButtonItem.Title` before retaining the native peer. The current run uses MAUI's Shell tracker cleanup.

Run:

```sh
dotnet run --project src/Controls/samples/IosShellBackBarButtonTitleRetentionRepro/IosShellBackBarButtonTitleRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes the result to `/tmp/ios-shell-backbarbutton-title-retention-results.txt`.

Verified result:

```text
Cycles: 96
Payload per native back title slot: 256 KiB
Leak proved: True

control:
  retained previous native navigation items: 96/96
  assigned payload-sized back title slots: 0/96
  estimated retained native back title MiB: 0.0
  alive trackers/shells/pages/behaviors/shell handlers/page handlers: 0/0/0/0/0/0

current:
  retained previous native navigation items: 96/96
  assigned payload-sized back title slots: 96/96
  estimated retained native back title MiB: 24.0
  alive trackers/shells/pages/behaviors/shell handlers/page handlers: 0/0/0/0/0/0

RESULT: PROVEN
```
