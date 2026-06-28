# iOS ShellSection TabBarItem Image Retention Repro

This sample proves whether iOS/Mac Catalyst compatibility `ShellSectionRenderer` leaves assigned native `UITabBarItem.Image` state on retained native tab item peers. Each cycle creates a MAUI `ShellSection` with a custom image source, routes it through the existing `ShellSectionRenderer.UpdateTabBarItem` path, clears the managed renderer fields that normal disposal clears, keeps only the native `UITabBarItem` alive, and counts assigned native images.

The control run manually loads and disposes the image-service result, assigns a native tab item image, then clears it before retaining the native item. The current run uses MAUI's `ShellSectionRenderer` tab item update path.

Run:

```sh
dotnet run --project src/Controls/samples/IosShellSectionTabBarItemImageRetentionRepro/IosShellSectionTabBarItemImageRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes the result to `/tmp/ios-shellsection-tabbaritem-image-retention-results.txt`.

Verified result:

```text
Cycles: 240
Source icon size: 256 x 256 pixels
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
  alive shells/shell sections/section handlers/sources: 0/0/0/0

RESULT: PROVEN
```
