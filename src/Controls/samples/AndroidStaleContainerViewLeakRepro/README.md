# Android stale ContainerView leak repro

This standalone app demonstrates the Android leak where `NavigationRootManager` drops its old root view reference but the old `Microsoft.Maui.Platform.ContainerView` still keeps `CurrentView` pointing at the previous `FlyoutPage`.

## Build

From the repository root:

```bash
dotnet build src/Controls/samples/AndroidStaleContainerViewLeakRepro/Maui.Controls.AndroidStaleContainerViewLeakRepro.csproj -f net10.0-android
```

## Install and run

```bash
dotnet build src/Controls/samples/AndroidStaleContainerViewLeakRepro/Maui.Controls.AndroidStaleContainerViewLeakRepro.csproj -f net10.0-android -t:Install -p:AndroidDeviceSerial=emulator-5554
adb -s emulator-5554 shell monkey -p com.microsoft.maui.repros.androidstalecontainerviewleak 1
```

## Manual repro steps

1. Tap `Open FlyoutPage`.
2. Tap `Return to monitor`.
3. Tap `Force GC` several times.

On an unfixed build, the old root `FlyoutPage`, flyout page, detail `NavigationPage`, and detail content page stay alive after GC. With the Android `NavigationRootManager` cleanup that clears `ContainerView.CurrentView`, the tracked counts drop to zero after GC.
