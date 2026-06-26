# AndroidDrawableResultDisposeLeakRepro

Android repro for direct image-service helpers that await `GetDrawableAsync`, use
`IImageSourceServiceResult<Drawable>.Value`, and drop the result without calling
`Dispose()`.

This covers the public Android background-image helper, the public `SeekBar`
slider thumb-image helper, the Shell/TabbedPage bottom-navigation icon helper,
and the Shell toolbar/back/flyout icon path that awaits `GetPlatformImageAsync`
and drops the returned result:

- `Android.Views.View.UpdateBackgroundImageSourceAsync`
- `SeekBar.UpdateThumbImageSourceAsync`
- `BottomNavigationViewUtils.SetMenuItemIcon`
- `ShellToolbarTracker.UpdateLeftBarButtonItem` direct `GetPlatformImageAsync` pattern

Each custom image-source result owns a 1 MiB native-like unmanaged payload that
is released only by the result's dispose callback.

## Result

On `Pixel_9_Pro` Android emulator:

```text
RESULT: PROVEN
control-background-disposes-result: results=80/80, allocated=80.0 MiB, disposed=80.0 MiB, leaked=0 B
leak-current-android-background-helper: results=80/0, allocated=80.0 MiB, disposed=0 B, leaked=80.0 MiB
control-seekbar-thumb-disposes-result: results=80/80, allocated=80.0 MiB, disposed=80.0 MiB, leaked=0 B
leak-current-android-seekbar-thumb-helper: results=80/0, allocated=80.0 MiB, disposed=0 B, leaked=80.0 MiB
control-bottomnav-icon-disposes-result: results=80/80, allocated=80.0 MiB, disposed=80.0 MiB, leaked=0 B
leak-current-bottomnav-icon-helper: results=80/0, allocated=80.0 MiB, disposed=0 B, leaked=80.0 MiB
control-shell-toolbar-icon-disposes-result: results=80/80, allocated=80.0 MiB, disposed=80.0 MiB, leaked=0 B
leak-current-shell-toolbar-getplatformimage-pattern: results=80/0, allocated=80.0 MiB, disposed=0 B, leaked=80.0 MiB
payload-bytes-per-result=1048576
payload-bytes-per-leak-path=83886080
```

## Run

```bash
dotnet build src/Controls/samples/AndroidDrawableResultDisposeLeakRepro/AndroidDrawableResultDisposeLeakRepro.csproj \
  -f net10.0-android \
  -p:UseMaui=false \
  -p:IncludeAndroidTargetFrameworks=true \
  -p:EmbedAssembliesIntoApk=true

adb install --no-incremental -r artifacts/bin/AndroidDrawableResultDisposeLeakRepro/Debug/net10.0-android/com.microsoft.maui.androiddrawableresultdisposeleakrepro-Signed.apk
adb shell pm clear com.microsoft.maui.androiddrawableresultdisposeleakrepro
adb shell monkey -p com.microsoft.maui.androiddrawableresultdisposeleakrepro 1
adb shell run-as com.microsoft.maui.androiddrawableresultdisposeleakrepro cat files/autorun-results.txt
```
