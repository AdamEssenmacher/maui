# iOS Shell Left Bar Button Image Retention Repro

This sample proves whether iOS/Mac Catalyst compatibility `ShellPageRendererTracker` leaves assigned native `UIBarButtonItem.Image` state and image source graphs on retained native Shell left bar button peers. Each cycle creates a `Shell` page with a custom `BackButtonBehavior.IconOverride`, routes it through `ShellPageRendererTracker.UpdateLeftToolbarItems()`, removes the native left bar button from its navigation item, keeps only that native `UIBarButtonItem` alive, and counts assigned native images plus surviving image sources.

The control run manually loads and disposes the image-service result, assigns a native `UIBarButtonItem.Image`, then clears it before retaining the native item. The current run uses MAUI's Shell left bar button image path.

Run:

```sh
dotnet run --project src/Controls/samples/IosShellLeftBarButtonImageRetentionRepro/IosShellLeftBarButtonImageRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes the result to `/tmp/ios-shell-leftbarbutton-image-retention-results.txt`.

Verified result:

```text
Cycles: 240
Source image size: 256 x 256 pixels
Leak proved: True

control:
  service results created/disposed: 240/240
  retained native peers: 240/240
  native peers with assigned UIImages: 0/240
  estimated assigned native image MiB: 0.0

current:
  service results created/disposed: 240/0
  retained native peers: 240/240
  native peers with assigned UIImages: 240/240
  estimated assigned native image MiB: 60.0
  alive shells/pages/shell handlers/page handlers/image sources: 1/1/0/1/240

RESULT: PROVEN
```
