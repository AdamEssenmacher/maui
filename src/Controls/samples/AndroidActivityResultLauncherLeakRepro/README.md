# Android ActivityResultLauncher Leak Repro

This repro proves that the Android photo-picker `ActivityResultLauncher` singletons can retain a destroyed `ComponentActivity`.

The repro uses the real MAUI internal singleton instances:

- `PickVisualMediaForResult.Instance`
- `PickMultipleVisualMediaForResult.Instance`

It registers both against a short-lived `ProbeActivity`, destroys that activity, and then inspects the AndroidX launcher root:

`ActivityForResultRequest.launcher -> ActivityResultRegistry$2.this$0 -> ComponentActivity$1.this$0 -> ProbeActivity`

The probe activity carries an 80 MiB payload to demonstrate severity. The control path unregisters and clears both singleton launchers after activity destruction; the current MAUI path leaves both launchers pointing to the destroyed activity.

Run:

```sh
dotnet build src/Controls/samples/AndroidActivityResultLauncherLeakRepro/AndroidActivityResultLauncherLeakRepro.csproj -f net10.0-android -p:UseMaui=false -p:IncludeAndroidTargetFrameworks=true -p:EmbedAssembliesIntoApk=true
adb install -r artifacts/bin/AndroidActivityResultLauncherLeakRepro/Debug/net10.0-android/com.microsoft.maui.androidactivityresultlauncherleakrepro-Signed.apk
adb shell monkey -p com.microsoft.maui.androidactivityresultlauncherleakrepro 1
adb shell run-as com.microsoft.maui.androidactivityresultlauncherleakrepro cat files/autorun-results.txt
```

Expected result:

```text
RESULT: PROVEN
```
