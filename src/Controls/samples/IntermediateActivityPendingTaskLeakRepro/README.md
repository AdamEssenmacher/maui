# IntermediateActivityPendingTaskLeakRepro

Android repro for `IntermediateActivity.StartAsync` leaving task and callback
state in its static `pendingTasks` table if the launch of the intermediate
activity fails after insertion.

The repro forces `MainActivity.StartActivityForResult` to throw for a sentinel
request code. That exception happens after `pendingTasks[data.Id] = data` and
before Android starts `IntermediateActivity`, so there is no
`OnActivityResult` or `OnDestroy` callback to remove the entry.

## Result

On `Pixel_9_Pro` Android emulator, built from commit `a6d9e30a62`:

```text
RESULT: PROVEN
completed-launch-control: payloads=0/80, pending-task-delta=0, exceptions=0
forced-launch-failure: payloads=80/80, pending-task-delta=80, exceptions=80
payload-bytes-per-scenario=83886080
app-data-directory=/data/user/0/com.microsoft.maui.intermediateactivitypendingtaskleakrepro/files
dotnet-version=10.0.7
```

The control path launches a result activity through `IntermediateActivity` and
confirms normal `OnActivityResult` cleanup. The failure path forces the host
activity launch call to throw after the static insertion point and retains all
80 payloads through the private `pendingTasks` dictionary.

## Run

```bash
dotnet build src/Controls/samples/IntermediateActivityPendingTaskLeakRepro/IntermediateActivityPendingTaskLeakRepro.csproj \
  -f net10.0-android \
  -p:UseMaui=false \
  -p:IncludeAndroidTargetFrameworks=true \
  -p:EmbedAssembliesIntoApk=true

adb install --no-incremental -r artifacts/bin/IntermediateActivityPendingTaskLeakRepro/Debug/net10.0-android/com.microsoft.maui.intermediateactivitypendingtaskleakrepro-Signed.apk
adb shell pm clear com.microsoft.maui.intermediateactivitypendingtaskleakrepro
adb shell monkey -p com.microsoft.maui.intermediateactivitypendingtaskleakrepro 1
adb shell run-as com.microsoft.maui.intermediateactivitypendingtaskleakrepro cat files/autorun-results.txt
```
