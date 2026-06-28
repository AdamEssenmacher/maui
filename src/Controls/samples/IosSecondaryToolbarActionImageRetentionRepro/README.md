# iOS Secondary Toolbar UIAction Image Retention Repro

This sample proves whether iOS/Mac Catalyst compatibility secondary toolbar menu actions leave assigned native `UIAction.Image` state on retained native action peers. Each cycle creates a secondary `ToolbarItem` with a custom `IconImageSource`, routes it through the internal `ToolbarItemExtensions.ToSecondarySubToolbarItem()` conversion path used by Shell and NavigationPage toolbar menus, keeps only the produced `UIAction` alive, and counts assigned action images.

The control run manually loads and disposes the image-service result, assigns a native `UIAction.Image`, then clears it before retaining the native action. The current run uses MAUI's secondary toolbar sub-menu action conversion path.

Run:

```sh
dotnet run --project src/Controls/samples/IosSecondaryToolbarActionImageRetentionRepro/IosSecondaryToolbarActionImageRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes the result to `/tmp/ios-secondary-toolbar-uiaction-image-retention-results.txt`.

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
