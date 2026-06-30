# ShellContentMenuItemsCollectionRetentionRepro

This repro targets `ShellContent.MenuItems`, the public owner-created menu item collection surface in this source tree.

`ShellContent` creates a `MenuItemCollection` by default and subscribes an owner instance method in its constructor:

```text
((INotifyCollectionChanged)MenuItems).CollectionChanged += MenuItemsCollectionChanged
```

`MenuItemCollection` forwards that subscription to its private `ObservableCollection<MenuItem>`. App code that keeps the public `MenuItems` collection after discarding the `ShellContent` can root the old owner through:

```text
app collection cache -> MenuItemCollection._inner.CollectionChanged -> ShellContent.MenuItemsCollectionChanged -> discarded ShellContent
```

The sample adds realistic menu actions, removes them one by one, and then retains only the empty `MenuItems` collection. This avoids the already tracked `ShellContent.MenuItems.Clear()` reset/parent cleanup leak. The control run keeps the same collection handles alive but clears the retained nested collection event fields first.

## Run

```bash
dotnet build src/Controls/samples/ShellContentMenuItemsCollectionRetentionRepro/ShellContentMenuItemsCollectionRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/ShellContentMenuItemsCollectionRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/ShellContentMenuItemsCollectionRetentionRepro.app --args --results=/tmp/shellcontent-menuitems-collection-retention-results.txt
```

The control run should retain zero `ShellContent` owners and payloads after full GC. The current run should retain every discarded owner and its 1 MiB binding payload through the app-retained `MenuItems` collections.
