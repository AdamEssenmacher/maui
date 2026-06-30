# VSM WatchAddList Collection Handle Retention Repro

This repro demonstrates that retaining empty public `VisualState.StateTriggers` or `VisualStateGroup.States` handles can keep discarded VSM owner objects alive.

Both properties are backed by `WatchAddList<T>`. The list stores a readonly `_onAdd` delegate created from an instance method on the owning `VisualState` or `VisualStateGroup`. If app code caches the public list handle after the owner is discarded, the empty list retains the owner through that delegate.

The sample creates 80 `VisualState.StateTriggers` handles and 80 `VisualStateGroup.States` handles. Each discarded owner has a 1 MiB payload associated through a `ConditionalWeakTable`, so payload retention proves owner retention without adding a normal strong field to the MAUI type. Each list is populated with three child items and then emptied before the app cache retains the public list handle.

The control run keeps the same empty list handles but clears `WatchAddList._onAdd` by reflection before forcing GC. The current run keeps MAUI's `_onAdd` delegate intact.

Run:

```bash
dotnet build src/Controls/samples/VsmWatchAddListCollectionHandleRetentionRepro/VsmWatchAddListCollectionHandleRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/VsmWatchAddListCollectionHandleRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/VsmWatchAddListCollectionHandleRetentionRepro.app
cat /tmp/vsm-watchaddlist-collection-handle-retention-results.txt
```

Expected unpatched result:

```text
RESULT: PROVEN
...
Run: control: retained empty public collections after clearing WatchAddList._onAdd
  owners alive after full GC: 0/160
...
Run: current: retained empty public collections with WatchAddList._onAdd intact
  owners alive after full GC: 160/160
  owner payload buffers alive after full GC: 160/160
  retained payload bytes: 160.0 MiB
```
