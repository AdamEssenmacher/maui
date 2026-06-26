# WebAuthenticatorOptionsRetentionLeakRepro

This repro demonstrates that `WebAuthenticator.Default` can retain the last failed request options.

On Android, `WebAuthenticatorImplementation.AuthenticateAsync` assigns `currentOptions = webAuthenticatorOptions` before callback-intent validation. If validation throws because the app has not registered a `WebAuthenticatorCallbackActivity` intent filter, the singleton default implementation keeps the failed `WebAuthenticatorOptions`, including a custom `ResponseDecoder` and anything captured by that decoder.

The project also builds for Mac Catalyst to document the contrast: current Mac Catalyst runtime versions do not take the failing preflight path for this callback scheme, so Android is the proof target for this leak.

## Android proof

Observed on the `Pixel_9_Pro` Android emulator:

```text
WebAuthenticatorOptionsRetentionLeakRepro
Attempts: 20
Payload per attempt: 8 MiB
Failed preflight path available: True
Leak proved: True

Run: control: options and decoder not passed to WebAuthenticator singleton
  tracked attempts: 20
  failed preflight attempts: 0/20
  WebAuthenticatorOptions alive after full GC: 0/20
  response decoders alive after full GC: 0/20
  decoder payloads alive after full GC: 0/20
  retained payload bytes: 0 B (0.0%)

Run: leak: failed WebAuthenticator validation retains last options
  tracked attempts: 20
  failed preflight attempts: 20/20
  WebAuthenticatorOptions alive after full GC: 1/20
  response decoders alive after full GC: 1/20
  decoder payloads alive after full GC: 1/20
  retained payload bytes: 8.0 MiB (5.0%)

Managed heap delta: 8.0 MiB
```

```sh
dotnet build src/Controls/samples/WebAuthenticatorOptionsRetentionLeakRepro/WebAuthenticatorOptionsRetentionLeakRepro.csproj -f net10.0-android -p:UseMaui=false -p:IncludeAndroidTargetFrameworks=true -p:EmbedAssembliesIntoApk=true
adb install --no-incremental -r artifacts/bin/WebAuthenticatorOptionsRetentionLeakRepro/Debug/net10.0-android/com.microsoft.maui.webauthenticatoroptionsretentionleakrepro-Signed.apk
adb shell pm clear com.microsoft.maui.webauthenticatoroptionsretentionleakrepro
adb shell monkey -p com.microsoft.maui.webauthenticatoroptionsretentionleakrepro 1
adb shell run-as com.microsoft.maui.webauthenticatoroptionsretentionleakrepro cat files/autorun-results.txt
```

## Mac Catalyst comparison

```sh
dotnet build src/Controls/samples/WebAuthenticatorOptionsRetentionLeakRepro/WebAuthenticatorOptionsRetentionLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/WebAuthenticatorOptionsRetentionLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/WebAuthenticatorOptionsRetentionLeakRepro.app --args --auto-run --results=/tmp/webauthenticatoroptionsretentionleakrepro-results.txt
cat /tmp/webauthenticatoroptionsretentionleakrepro-results.txt
```
