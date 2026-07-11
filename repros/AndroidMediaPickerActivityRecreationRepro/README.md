# Android MediaPicker Activity-Recreation Repro

This standalone .NET MAUI app demonstrates an Android regression introduced by [dotnet/maui#35944](https://github.com/dotnet/maui/pull/35944): `MediaPicker.PickPhotosAsync()` can remain incomplete when its launching `ComponentActivity` is recreated while the system photo picker is open.

The app opens a native child `AppCompatActivity` that calls `Platform.Init` and intentionally does not handle orientation configuration changes. It retains the exact picker `Task` for diagnostics and displays its completion state after Android recreates the activity.

## Verified Environment

- `dotnet/inflight/current`: `f594a31d59e9c444e0e7fc040a69a4660926cf59`
- MediaPicker change: `f9f3c80e34928c0329ab4e959e0d50e8a3fcdfc6`
- Verified: July 11, 2026
- Emulator: Pixel 9 Pro AVD with the `android-36.1` system image
- Android: 16 / API 36, build `BE4B.251210.005`
- Build fingerprint: `google/sdk_gphone64_arm64/emu64a:16/BE4B.251210.005/14574095:user/release-keys`

The control path completed with zero results and `IsCompleted=True`. The rotation path reproduced on two out of two fresh app launches: activity 1 was destroyed with `IsChangingConfigurations=True`, activity 2 was created, and the original picker task remained `WaitingForActivation` after the picker returned.

## Build

From the MAUI repository root:

```bash
./build.sh -restore
dotnet build Microsoft.Maui.BuildTasks.slnf -c Debug
dotnet build repros/AndroidMediaPickerActivityRecreationRepro/AndroidMediaPickerActivityRecreationRepro.csproj \
  -f net10.0-android \
  -c Debug \
  -p:UseMaui=false \
  -p:IncludeAndroidTargetFrameworks=true \
  -p:EmbedAssembliesIntoApk=true
```

Embedding assemblies produces a self-contained APK and avoids depending on fast-deployment state.

The project uses in-tree MAUI project references by default. Add `-p:UseWorkload=true` to build against workload packages instead; pass `-p:MauiVersion=<version>` when a specific package version is required.

## Install and Run

```bash
adb install --no-incremental -r artifacts/bin/AndroidMediaPickerActivityRecreationRepro/Debug/net10.0-android/com.microsoft.maui.repros.mediapickerrecreation-Signed.apk
adb shell monkey -p com.microsoft.maui.repros.mediapickerrecreation 1
```

To watch the diagnostic log:

```bash
adb logcat -s MediaPickerRecreationRepro:I
```

## Control Path

1. Launch the app and tap **Open child activity**.
2. Confirm the child displays `Current activity instance: 1`.
3. Tap **Pick photos**.
4. Press Back without rotating the device.
5. Observe `PASS: request 1 completed with 0 result(s)` and `IsCompleted=True`.

Representative output:

```text
Activity 1: request 1 started
Request 1: task attached (WaitingForActivation)
PASS: request 1 completed with 0 result(s)
```

## Regression Path

1. Launch or restart the app in portrait and tap **Open child activity**.
2. Confirm the child displays `Current activity instance: 1`.
3. Tap **Pick photos**.
4. While the Android photo picker is open, rotate the device.
5. Press Back to cancel the picker.
6. Observe that Android created child activity instance 2.
7. After two seconds, observe `FAIL: picker returned through activity 2, but request 1 from activity 1 is still pending` and `IsCompleted=False`.

The rotation can also be driven with ADB. Start in portrait, launch the picker, then lock landscape:

```bash
adb shell cmd window user-rotation lock 0
# Launch the picker, then rotate to landscape.
adb shell cmd window user-rotation lock 1
adb shell input keyevent KEYCODE_BACK
# Restore sensor-controlled rotation after the test.
adb shell cmd window user-rotation free
```

Representative output:

```text
Activity 1: OnCreate
Activity 1: OnResume
Activity 1: request 1 started
Request 1: task attached (WaitingForActivation)
Activity 1: OnDestroy changingConfigurations=True finishing=False
Activity 2: OnCreate
Activity 2: OnResume
Activity 2: OnResume
FAIL: picker returned through activity 2, but request 1 from activity 1 is still pending
```

There is no completion or cancellation entry for request 1. The two-second diagnostic check only observes the retained task; it does not cancel or complete it.

## Expected Behavior

Android's Activity Result API delivers the in-flight picker result through the callback registered by the recreated activity. That callback should resolve the task created by the original activity, so cancelling returns an empty result list just as it does in the control path.

## Actual Behavior

The pending task is keyed by the original activity instance. The recreated activity's callback looks up its own instance, does not find the original task, and drops the result. The user returns from the picker, but the application workflow remains pending indefinitely.
