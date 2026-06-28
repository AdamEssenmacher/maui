# iOS ToolbarItem Native Image Retention Repro

This sample proves that iOS/Mac Catalyst compatibility toolbar item conversion leaves assigned native `UIBarButtonItem.Image` state on retained native toolbar item peers. Each cycle creates a MAUI `ToolbarItem` with a custom image source, converts it through the existing toolbar extension path, disconnects the page, keeps the native `UIBarButtonItem` alive, and counts assigned native images.

The control run manually loads and disposes the image-service result, assigns the native toolbar item image, then clears it before retaining the native item. The current run uses MAUI's `ToolbarItemExtensions.ToUIBarButtonItem` path.

Run:

```sh
dotnet run --project src/Controls/samples/IosToolbarItemNativeImageRetentionRepro/IosToolbarItemNativeImageRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes the result to `/tmp/ios-toolbaritem-native-image-retention-results.txt`.

Verified result:

```text
Cycles: 240
Toolbar icon size: 256 x 256 pixels
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
  alive pages/toolbar items/handlers/sources: 0/0/0/0

RESULT: PROVEN
```
