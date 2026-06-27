# VSM WatchAddList trigger retention repro

This sample proves whether live mutations of VSM `WatchAddList` collections leave removed `StateTriggerBase` instances attached.

It compares explicit trigger detachment against current `StateTriggers.Clear()`, `VisualStateGroup.States.Clear()`, and `VisualStateGroupList.Clear()` behavior using `DisplayRotationStateTrigger`, which subscribes to `DeviceDisplay.MainDisplayInfoChanged` while attached.

Run:

```sh
dotnet build src/Controls/samples/VsmWatchAddListTriggerRetentionRepro/VsmWatchAddListTriggerRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/VsmWatchAddListTriggerRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/VsmWatchAddListTriggerRetentionRepro.app --args --results=/tmp/vsmwatchaddlisttriggerretentionrepro-results.txt
cat /tmp/vsmwatchaddlisttriggerretentionrepro-results.txt
```
