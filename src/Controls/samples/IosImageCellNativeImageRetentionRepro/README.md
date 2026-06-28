# iOS ImageCell Native Image Retention Repro

This sample proves that legacy iOS/Mac Catalyst compatibility `ImageCellRenderer` leaves assigned native `UITableViewCell.ImageView.Image` state on retained native cell peers after the cell renderer disconnects. Each cycle loads a custom MAUI `ImageSource`, disconnects the renderer, keeps the native `CellTableViewCell` alive, and counts assigned native images.

The control run disposes the image-service result and clears the native cell image before disconnect. The current run uses MAUI's handler-created `ImageCellRenderer` platform view, which invokes the `GetCell` / `ImageSourceExtensions.LoadImage` path.

Run:

```sh
dotnet run --project src/Controls/samples/IosImageCellNativeImageRetentionRepro/IosImageCellNativeImageRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes the result to `/tmp/ios-imagecell-native-image-retention-results.txt`.

Verified result:

```text
Cycles: 240
Cell image size: 256 x 256 pixels
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
  alive virtual views/handlers/sources: 0/0/0

RESULT: PROVEN
```
