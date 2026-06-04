# IndicatorView ItemsSource leak repro

This standalone MAUI sample probes an `IndicatorView` leak when `CarouselView.IndicatorView` is connected to an `IndicatorView` whose `ItemsSource` is a rooted, shared `ObservableCollection<T>`.

The repro intentionally compares three scenarios:

- `SnapshotListControl`: same navigation/pages, but each visit binds to a non-observable snapshot list.
- `SharedObservableFeed`: each visit binds to the same rooted `ObservableCollection<T>`.
- `ClearIndicatorOnDisappear`: uses the same shared feed, but clears `IndicatorView.Position`, `IndicatorView.ItemsSource`, and `CarouselView.ItemsSource` when the page disappears.

## What this proves

The page view models should collect in all scenarios. Each page owns a `VisitPayloadViewModel` as its page `BindingContext`, while the content/control tree is intentionally given a null local `BindingContext` so this metric measures page lifetime separately from inherited BindingContext retention. If the shared observable path keeps old controls alive after navigation and GC, that is the leak signal.

Each visit attaches two `RetainedPayloadBehavior` instances, one to the `IndicatorView` and one to the `CarouselView`. The payload is synthetic control-attached memory. It models realistic data that applications commonly hang from controls through behaviors, resources, commands, image/cache state, or other control-owned objects. With the default `1 MB` per visit and `40` visits, the leaky shared-feed scenario retains `40 MB` of payload when all 80 behaviors stay alive.

The sample also tracks handlers and platform views. In previous runs those were expected to be `0/40` after GC; this repro still reports them so retained native/platform objects would be visible if present.

Android can show a small baseline tail in the snapshot and cleanup controls. Compare the shared-feed scenario against the control scenarios rather than treating small Android tail retention as the primary finding.

## Defaults

- Visits: `40`
- Feed items: `120`
- Control payload per visit: `1 MB`
- Post-GC feed updates: `250`

## Build

```sh
dotnet build src/Controls/samples/IndicatorViewItemsSourceLeakRepro/IndicatorViewItemsSourceLeakRepro.csproj -f net10.0-maccatalyst --no-restore
dotnet build src/Controls/samples/IndicatorViewItemsSourceLeakRepro/IndicatorViewItemsSourceLeakRepro.csproj -f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64 --no-restore
dotnet build src/Controls/samples/IndicatorViewItemsSourceLeakRepro/IndicatorViewItemsSourceLeakRepro.csproj -f net10.0-android --no-restore
dotnet build src/Controls/samples/IndicatorViewItemsSourceLeakRepro/IndicatorViewItemsSourceLeakRepro.csproj -f net10.0-android --no-restore -p:EmbedAssembliesIntoApk=true
```

## Autorun

Mac Catalyst:

```sh
INDICATOR_REPRO_AUTORUN=1 artifacts/bin/IndicatorViewItemsSourceLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/IndicatorViewItemsSourceLeakRepro.app/Contents/MacOS/IndicatorViewItemsSourceLeakRepro
```

iOS simulator:

```sh
xcrun simctl install booted artifacts/bin/IndicatorViewItemsSourceLeakRepro/Debug/net10.0-ios/iossimulator-arm64/IndicatorViewItemsSourceLeakRepro.app
SIMCTL_CHILD_INDICATOR_REPRO_AUTORUN=1 xcrun simctl launch --console --terminate-running-process booted com.microsoft.maui.indicatorviewitemsourceleakrepro
```

Android:

```sh
adb install --no-incremental -r artifacts/bin/IndicatorViewItemsSourceLeakRepro/Debug/net10.0-android/com.microsoft.maui.indicatorviewitemsourceleakrepro-Signed.apk
adb shell logcat -c
adb shell am start -W -n com.microsoft.maui.indicatorviewitemsourceleakrepro/com.microsoft.maui.indicatorviewitemsourceleakrepro.MainActivity --ez autorun true
adb shell logcat -d -v time -e IndicatorViewItemsSourceLeakRepro
```

## Expected result

On iOS simulator and Mac Catalyst, the snapshot and cleanup scenarios should report `control payload behaviors: 0/80` and `retained control payload: 0 B`.

The shared observable scenario should report `control payload behaviors: 80/80` and `retained control payload: 40.0 MB`.

On Android, the shared observable scenario should also retain `80/80` payload behaviors and `40.0 MB`. Snapshot and cleanup may retain a small tail depending on runtime/device state.

The shared observable feed update burst should be materially slower than the control scenarios because the shared feed still notifies the leaked controls.
