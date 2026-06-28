# iOS FlyoutPage Left Bar Button Title Retention Repro

This sample proves whether the legacy iOS/Mac Catalyst `NavigationRenderer.SetFlyoutLeftBarButton()` fallback title path leaves native `UIBarButtonItem.Title` state on retained native left bar button peers. Each cycle creates a `FlyoutPage` with a generated `Flyout.Title`, invokes the real compatibility renderer helper with no flyout icon, clears the managed title after native assignment, keeps only the produced native `UIBarButtonItem` alive, and counts payload-sized native title slots.

The control run creates a same-shape native bar button with a static action, then clears the native title before retaining it. The current run uses MAUI's legacy FlyoutPage helper.

Run:

```sh
dotnet run --project src/Controls/samples/IosFlyoutPageLeftBarButtonTitleRetentionRepro/IosFlyoutPageLeftBarButtonTitleRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes the result to `/tmp/ios-flyoutpage-leftbarbutton-title-retention-results.txt`.

Verified result:

```text
Cycles: 96
Payload per native title: 256 KiB
Managed payload per cycle: 1,048,576 bytes
Leak proved: True

control:
  retained native peers: 96/96
  assigned payload-sized titles: 0/96
  alive FlyoutPages/flyouts/details: 0/0/0
  alive payload byte arrays: 0/96

current:
  retained native peers: 96/96
  assigned payload-sized titles: 96/96
  estimated assigned native title MiB: 24.0
  alive FlyoutPages/flyouts/details: 96/96/96
  alive payload byte arrays: 96/96
  alive payload MiB: 96.0

RESULT: PROVEN
```
