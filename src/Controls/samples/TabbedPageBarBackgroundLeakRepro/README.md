# TabbedPage BarBackground leak repro

This standalone app demonstrates a non-Shell `TabbedPage` leak where an app
resource `Style` sets `TabbedPage.BarBackground` to a shared
`LinearGradientBrush`.

The repro creates two isolated probes:

1. `Leak path`: applies an app resource `Style` whose `BarBackground` setter
   points at a shared app resource brush, replaces `Window.Page`, then forces
   full GC.
2. `Control path`: runs the same sequence, but clears `BarBackground` before
   replacing `Window.Page`.

On an affected build, the leak path leaves the shared brush with a live renderer
or manager subscriber. The control path leaves the brush with zero subscribers.

## Build

From the repository root:

```bash
bash ./eng/common/dotnet.sh build src/Controls/samples/TabbedPageBarBackgroundLeakRepro/Maui.Controls.TabbedPageBarBackgroundLeakRepro.csproj -f net10.0-android -p:IncludeAndroidTargetFrameworks=true
bash ./eng/common/dotnet.sh build src/Controls/samples/TabbedPageBarBackgroundLeakRepro/Maui.Controls.TabbedPageBarBackgroundLeakRepro.csproj -f net10.0-ios -p:IncludeIosTargetFrameworks=true -p:RuntimeIdentifier=iossimulator-arm64
```

If the in-tree build-task artifacts have not been built yet, generate them first:

```bash
bash ./eng/common/dotnet.sh build src/Controls/src/Build.Tasks/Controls.Build.Tasks.csproj
bash ./eng/common/dotnet.sh build src/SingleProject/Resizetizer/src/Resizetizer.csproj
```

## iOS simulator run

```bash
xcrun simctl install booted artifacts/bin/Maui.Controls.TabbedPageBarBackgroundLeakRepro/Debug/net10.0-ios/iossimulator-arm64/Maui.Controls.TabbedPageBarBackgroundLeakRepro.app
xcrun simctl launch booted com.microsoft.maui.repros.tabbedpagebarbackgroundleak
container=$(xcrun simctl get_app_container booted com.microsoft.maui.repros.tabbedpagebarbackgroundleak data)
find "$container" -name tabbedpage-barbackground-leak-result.txt -print -exec cat {} \;
```

Expected affected iOS result:

```text
Leak reproduced: app resource style gradient retained the removed TabbedPage renderer/manager.

Leak path
  Brush subscribers: 1
  Subscriber targets: Microsoft.Maui.Controls.Handlers.Compatibility.TabbedRenderer
  Captured subscriber target: alive

Control path
  Brush subscribers: 0
  Subscriber targets: <none>
  Captured subscriber target: collected
```

## Android run

```bash
bash ./eng/common/dotnet.sh build src/Controls/samples/TabbedPageBarBackgroundLeakRepro/Maui.Controls.TabbedPageBarBackgroundLeakRepro.csproj -f net10.0-android -t:Install -p:IncludeAndroidTargetFrameworks=true -p:AndroidDeviceSerial=emulator-5554
adb -s emulator-5554 shell monkey -p com.microsoft.maui.repros.tabbedpagebarbackgroundleak 1
```
