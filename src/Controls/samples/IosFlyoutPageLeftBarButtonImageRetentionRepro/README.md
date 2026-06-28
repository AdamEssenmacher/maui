# iOS FlyoutPage Left Bar Button Image Retention Repro

This sample proves whether the legacy iOS/Mac Catalyst `NavigationRenderer.SetFlyoutLeftBarButton()` path leaves native left bar button action and image state on retained native `UIBarButtonItem` peers. Each cycle creates a `FlyoutPage` with a 1 MiB binding-context payload and a flyout page custom `IconImageSource`, invokes the real compatibility renderer helper, keeps only the produced native `UIBarButtonItem` alive, and counts retained page payloads plus assigned native images.

The control run manually loads and disposes the image-service result, assigns a native `UIBarButtonItem.Image`, uses a static action that does not capture the page graph, then clears the image before retaining the native bar button item. The current run uses MAUI's legacy FlyoutPage helper.

Run:

```sh
dotnet run --project src/Controls/samples/IosFlyoutPageLeftBarButtonImageRetentionRepro/IosFlyoutPageLeftBarButtonImageRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes the result to `/tmp/ios-flyoutpage-leftbarbutton-image-retention-results.txt`.

Verified result:

```text
Cycles: 120
Source image size: 256 x 256 pixels
Payload per cycle: 1,048,576 bytes
Leak proved: True

control:
  retained native peers: 120/120
  native peers with assigned UIImages: 0/120
  alive FlyoutPages/flyouts/details: 0/0/0
  alive payload byte arrays: 0/120

current:
  retained native peers: 120/120
  native peers with assigned UIImages: 120/120
  estimated assigned native image MiB: 30.0
  alive FlyoutPages/flyouts/details: 120/120/120
  alive payload byte arrays: 120/120
  alive payload MiB: 120.0

RESULT: PROVEN
```
