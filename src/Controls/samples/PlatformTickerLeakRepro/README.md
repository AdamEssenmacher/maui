# PlatformTickerLeakRepro

Android repro for `PlatformTicker.Dispose()` leaving its infinite `ValueAnimator`
running. A running native animator keeps the `Update` delegate alive, which keeps
the disposed ticker alive. In the normal MAUI path, that also retains the
disposed `AnimationManager` and any active animation payloads.

## Result

On `Pixel_9_Pro` Android emulator, built from commit `a6d9e30a62`:

```text
RESULT: PROVEN
direct-stop-before-dispose-control: payloads=0/80, tickers=0/80
direct-running-dispose: payloads=80/80, tickers=80/80
animation-manager-running-dispose: payloads=80/80, tickers=80/80, managers=80/80
payload-bytes-per-scenario=83886080
dotnet-version=10.0.7
```

The control stops each ticker before disposal and retains nothing. The leak
scenarios dispose running tickers/managers and retain all 80 payloads. Each leak
scenario uses 80 MiB of payload to make the severity visible.

## Run

```bash
dotnet build src/Controls/samples/PlatformTickerLeakRepro/PlatformTickerLeakRepro.csproj \
  -f net10.0-android \
  -p:UseMaui=false \
  -p:IncludeAndroidTargetFrameworks=true \
  -p:EmbedAssembliesIntoApk=true

adb install --no-incremental -r artifacts/bin/PlatformTickerLeakRepro/Debug/net10.0-android/com.microsoft.maui.platformtickerleakrepro-Signed.apk
adb shell pm clear com.microsoft.maui.platformtickerleakrepro
adb shell monkey -p com.microsoft.maui.platformtickerleakrepro 1
adb shell run-as com.microsoft.maui.platformtickerleakrepro cat files/autorun-results.txt
```
