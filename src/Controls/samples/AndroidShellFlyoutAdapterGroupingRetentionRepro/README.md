# Android Shell Flyout Adapter Grouping Retention Repro

This sample proves that disposed Android `ShellFlyoutRecyclerAdapter` instances retain generated Shell flyout groupings through `_flyoutGroupings`.

The repro creates Shell graphs with payload-bearing `ShellContent` objects, creates and disposes a `ShellFlyoutRecyclerAdapter`, releases the Shell graph, and compares:

- a baseline with no retained adapter
- a control that keeps disposed adapters but clears `_flyoutGroupings`
- current MAUI behavior, which keeps `_flyoutGroupings` assigned

Expected result:

```text
Result: PROVEN
Current MAUI behavior retained discarded ShellContent payloads while only disposed adapters remained rooted.
```

Retained graph:

```text
Retained disposed ShellFlyoutRecyclerAdapter -> _flyoutGroupings -> ShellSection/ShellContent -> BindingContext payload
```

Run on an Android emulator:

```bash
dotnet build src/Controls/samples/AndroidShellFlyoutAdapterGroupingRetentionRepro/AndroidShellFlyoutAdapterGroupingRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:IncludeAndroidTargetFrameworks=true -t:Run -m:1 -nr:false
adb shell run-as com.microsoft.maui.androidshellflyoutadaptergroupingretention cat files/android-shell-flyout-adapter-grouping-retention-results.txt
```
