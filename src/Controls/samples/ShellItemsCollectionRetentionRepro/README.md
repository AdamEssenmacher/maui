# ShellItemsCollectionRetentionRepro

This repro targets the public Shell hierarchy `Items` collection wrappers:

- `Shell.Items`
- `ShellItem.Items`
- `ShellSection.Items`

These properties expose `ShellElementCollection` wrappers over each owner's `DeclaredChildren`. `BaseShellItem` subscribes an owner-capturing handler to `DeclaredChildren.CollectionChanged`, and Shell/ShellItem/ShellSection constructors also attach owner callbacks to the exposed wrapper's collection or visible-items events.

The retained path is:

```text
app collection cache -> Shell Items wrapper -> ElementCollection/DeclaredChildren events -> discarded Shell owner
```

The sample adds realistic Shell hierarchy children, removes them one by one, and then retains only the empty `Items` wrapper. This avoids the already tracked `Shell.Items.Clear()` root handler-disconnect class. The control run keeps the same wrappers alive but clears retained nested `NotifyCollectionChangedEventHandler` fields first.

## Run

```bash
dotnet build src/Controls/samples/ShellItemsCollectionRetentionRepro/ShellItemsCollectionRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/ShellItemsCollectionRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/ShellItemsCollectionRetentionRepro.app --args --results=/tmp/shell-items-collection-retention-results.txt
```

The control run should retain zero Shell owners and payloads after full GC. The current run should retain every discarded owner and its 1 MiB binding payload through the app-retained `Items` wrappers.
