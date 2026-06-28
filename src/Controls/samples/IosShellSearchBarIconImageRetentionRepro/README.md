# iOS Shell Search Bar Icon Image Retention Repro

This sample proves whether the iOS/Mac Catalyst `ShellPageRendererTracker.SetSearchBarIcon()` path leaves assigned native `UISearchBar` icon images on retained search bar peers. Each cycle creates three custom `ImageSource` instances for Search, Clear, and Bookmark icons, invokes the real private Shell tracker method, keeps only the produced `UISearchBar` alive, and counts assigned icon-state images.

The control run manually loads and disposes the image-service results, assigns the same icon-state images, then clears all search-bar icon slots before retaining the native search bar. The current run uses MAUI's Shell search-bar icon helper.

Run:

```sh
dotnet run --project src/Controls/samples/IosShellSearchBarIconImageRetentionRepro/IosShellSearchBarIconImageRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes the result to `/tmp/ios-shell-searchbar-icon-image-retention-results.txt`.

Verified result:

```text
Cycles: 120
Source image size: 256 x 256 pixels
Icons per search bar: 3
States per icon: 3
Leak proved: True

control:
  service results created/disposed: 360/360
  retained native peers: 120/120
  native peers with assigned icons: 0/120
  assigned icon state slots: 0/1080
  estimated assigned native image MiB: 0.0

current:
  service results created/disposed: 360/0
  retained native peers: 120/120
  native peers with assigned icons: 120/120
  assigned icon state slots: 1080/1080
  estimated assigned native image MiB: 90.0
  alive shells/source handlers/image sources: 0/0/0

RESULT: PROVEN
```
