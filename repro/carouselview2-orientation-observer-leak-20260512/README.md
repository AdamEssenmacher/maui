# CarouselView2 Orientation Observer Leak Repro

This repro targets a suspected iOS/MacCatalyst leak in `src/Controls/src/Core/Handlers/Items2/iOS/CarouselViewController2.cs`.

`CarouselViewController2.ViewDidLoad()` subscribes to `UIDevice.OrientationDidChangeNotification` with the block-based `NSNotificationCenter.AddObserver` overload, but it does not keep the returned observer token. `TearDown()` later calls `RemoveObserver(this, UIDevice.OrientationDidChangeNotification, null)`, which does not remove that block observer.

Build on MacCatalyst:

```sh
./.dotnet/dotnet build repro/carouselview2-orientation-observer-leak-20260512/CarouselView2OrientationObserverLeakRepro.csproj -f net10.0-maccatalyst -p:ValidateXcodeVersion=false
```

Run the generated MacCatalyst app:

```sh
RID=maccatalyst-x64
if [ "$(uname -m)" = "arm64" ]; then RID=maccatalyst-arm64; fi
artifacts/bin/CarouselView2OrientationObserverLeakRepro/Debug/net10.0-maccatalyst/$RID/CarouselView2OrientationObserverLeakRepro.app/Contents/MacOS/CarouselView2OrientationObserverLeakRepro
```

The app writes `carouselview2-orientation-observer-leak-result.txt` to `FileSystem.AppDataDirectory` and exits:

- `0` when the leak is reproduced.
- `1` when the expected retention signal is not observed.
- `3` when the repro fails before producing a result.

The expected signal is that the CarouselView2 controller remains alive after navigation pop, detach, handler disconnect, and forced GC, while the CollectionView control scenario collects under the same path. The result also records post-detach CarouselView2 state so the output can show that the teardown path ran before GC.

Open issue searches on May 12, 2026 did not find an open `dotnet/maui` issue tracking this observer-token leak:

- `OrientationDidChangeNotification CarouselView2`
- `"CarouselView2" "memory"`
- `"CarouselView" "platform/ios" "memory"`

Broader searches found unrelated open issues: Shell page memory leak (#22645), a Lottie/CollectionView.EmptyView rendering issue (#22529), ListView deprecation (#28699), and an Android-scoped CarouselView memory leak (#23825).
