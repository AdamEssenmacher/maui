# ShellContentMenuItemsClearRetentionLeakRepro

This repro proves that `ShellContent.MenuItems.Clear()` can leave removed `MenuItem` objects rooted by the still-live `ShellContent`.

`MenuItemCollection.Clear()` delegates to `ObservableCollection<MenuItem>.Clear()`, which raises a reset notification without old items. `ShellContent.MenuItemsCollectionChanged()` clears child parent hooks only from `NotifyCollectionChangedEventArgs.OldItems`. A long-lived `ShellContent` can therefore keep removed menu items and their binding-context payloads alive after menu churn.

Run:

```sh
dotnet build src/Controls/samples/ShellContentMenuItemsClearRetentionLeakRepro/ShellContentMenuItemsClearRetentionLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/ShellContentMenuItemsClearRetentionLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/ShellContentMenuItemsClearRetentionLeakRepro.app --args --results=/tmp/shellcontentmenuitemsclearretentionleakrepro-results.txt
cat /tmp/shellcontentmenuitemsclearretentionleakrepro-results.txt
```
