# MapPinLeakRepro

This repro targets the Android `MapHandler` path where removed `Pin` instances
can keep a stale `Pin.PropertyChanged` subscription to the map handler.

The app intentionally keeps `Pin` objects alive in a long-lived session cache,
which matches apps that reuse pin models from a view model, repository, or
offline cache. Retaining the pin objects is not the bug. The bug is that pins
removed from `Map.Pins` should not keep the old Android `MapHandler` alive after
the page is popped and the map is disconnected.

## Run

Build from the repo root:

```bash
dotnet build Microsoft.Maui.BuildTasks.slnf
dotnet build src/Controls/samples/MapPinLeakRepro/MapPinLeakRepro.csproj -f net10.0-android
```

Run on Android:

```bash
dotnet run --project src/Controls/samples/MapPinLeakRepro/MapPinLeakRepro.csproj -f net10.0-android
```

Run both scenarios automatically and collect the report:

```bash
dotnet run --project src/Controls/samples/MapPinLeakRepro/MapPinLeakRepro.csproj -f net10.0-android -p:MapPinLeakReproAutoRun=true
adb shell run-as com.microsoft.maui.mappinleakrepro find . -name autorun-results.txt -print -exec cat {} \;
```

For direct `adb install` workflows, embed the assemblies into the debug APK:

```bash
dotnet build src/Controls/samples/MapPinLeakRepro/MapPinLeakRepro.csproj -f net10.0-android -p:MapPinLeakReproAutoRun=true -p:EmbedAssembliesIntoApk=true
adb install -r artifacts/bin/MapPinLeakRepro/Debug/net10.0-android/com.microsoft.maui.mappinleakrepro-Signed.apk
adb shell am start -n com.microsoft.maui.mappinleakrepro/crc6405fc606fe80f943a.MainActivity
adb shell run-as com.microsoft.maui.mappinleakrepro cat files/MapPinLeakRepro/autorun-results.txt
```

The Android manifest includes an empty Maps API key placeholder:

```xml
<meta-data android:name="com.google.android.geo.API_KEY" android:value="" />
```

Use a valid key if your emulator/device does not create a `GoogleMap` instance
with the placeholder.

## What to Check

Use the default settings first:

- Pages/run: `20`
- Pins/page: `8`
- Dwell ms/page: `100`

Run these scenarios:

1. `Run control`
   - Each pushed page adds pins and the session keeps those same pins alive.
   - The pins remain in `Map.Pins` until the page is popped.
   - During map disconnect, Android `MapHandler.DisconnectPins()` unsubscribes
     those current pins.
   - After full GC, alive map handlers should stay near zero.

2. `Run removed-pin leak`
   - Each pushed page adds pins and the session keeps those pins alive.
   - The page then removes those retained pins from `Map.Pins` while the map is
     still connected and adds replacement pins.
   - On an affected build, removed retained pins keep stale
     `PropertyChanged += PinOnPropertyChanged` subscriptions. After the page is
     popped and full GC runs, alive map handlers grow with the page count.

The clearest signal is:

```text
Weak refs still alive after full GC:
  map handlers: N/N
```

On a fixed build, the removed-pin scenario should look like the control and the
alive map handler count should return close to zero.

On an affected build, the expected contrast is:

```text
Run: control: retained current pins
  map handlers: 0/20

Run: leak: retained removed pins
  map handlers: 20/20
```
