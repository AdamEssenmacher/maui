# ContentPresenter ParentSet Pending Retention Repro

This sample tests whether a retained off-tree `ContentPresenter` can retain removed content views through pending templated-parent lookup tasks.

Run it for Mac Catalyst:

```bash
dotnet build src/Controls/samples/ContentPresenterParentSetPendingRetentionRepro/ContentPresenterParentSetPendingRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
open -W artifacts/bin/ContentPresenterParentSetPendingRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/ContentPresenterParentSetPendingRetentionRepro.app
cat /tmp/contentpresenter-parentset-pending-retention-results.txt
```

Expected result: `RESULT: PROVEN`.

The repro keeps one `ContentPresenter` alive in both scenarios and assigns then clears 120 content views with 1 MiB payloads. The control keeps the presenter parented so `FindTemplatedParentAsync` completes. The current path keeps the presenter off-tree, so each content assignment leaves a pending `ParentSet` handler whose async continuation still captures the removed content view.
