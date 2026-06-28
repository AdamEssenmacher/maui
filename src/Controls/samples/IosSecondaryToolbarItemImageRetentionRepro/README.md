# iOS Secondary ToolbarItem Image Retention Repro

This sample proves whether iOS/Mac Catalyst compatibility `SecondaryToolbarItem` leaves assigned native `UIImageView.Image` state on retained secondary toolbar custom-view peers. Each cycle creates a secondary `ToolbarItem` with a custom `IconImageSource`, routes it through the existing `ToolbarItem.ToUIBarButtonItem()` conversion path, retains only the secondary toolbar item's `CustomView`, and counts the nested `UIImageView.Image` assignments.

The control run manually loads and disposes the image-service result, assigns a native `UIImageView.Image`, then clears it before retaining the native view. The current run uses MAUI's secondary toolbar item conversion path.

Run:

```sh
dotnet run --project src/Controls/samples/IosSecondaryToolbarItemImageRetentionRepro/IosSecondaryToolbarItemImageRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes the result to `/tmp/ios-secondary-toolbaritem-image-retention-results.txt`.

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
  alive pages/toolbar items/handlers/image sources: 0/0/0/0

RESULT: PROVEN
```
