# ListView RefreshCommand leak repro

This sample demonstrates that `ListView.RefreshCommand` can retain closed list pages when the command is a long-lived `ICommand` implementation with a normal strong `CanExecuteChanged` event.

`ListView.OnRefreshCommandChanged` subscribes `newCommand.CanExecuteChanged += OnCommandCanExecuteChanged` and only detaches when the `RefreshCommand` property changes. If an app uses a shared command for short-lived pull-to-refresh pages and those pages close without clearing `RefreshCommand`, the command can keep each `ListView` alive. The sample gives every retained `ListView` a page-scoped refresh view model with cached data to show the real-world impact.

The app runs three scenarios:

1. Control: create `ListView` pages without a refresh command.
2. Leak: create `ListView` pages with one shared strong refresh command.
3. Cleanup: create pages with the shared command, then clear `RefreshCommand`.

The built-in MAUI `Command` uses weak event handlers. This repro uses a custom strong `ICommand` because many app and MVVM command implementations expose a normal strong `CanExecuteChanged` event.

## Mac Catalyst autorun

```bash
dotnet build src/Controls/samples/ListViewRefreshCommandLeakRepro/ListViewRefreshCommandLeakRepro.csproj -f net10.0-maccatalyst
open -W artifacts/bin/ListViewRefreshCommandLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/ListViewRefreshCommandLeakRepro.app --args \
  --auto-run \
  --results=/private/tmp/listviewrefreshcommandleakrepro-results.txt
```

If the sandbox blocks `/private/tmp`, the app writes the report to:

`~/Library/Containers/com.microsoft.maui.listviewrefreshcommandleakrepro/Data/Documents/ListViewRefreshCommandLeakRepro/autorun-results.txt`

Observed Mac Catalyst autorun result on 2026-06-25:

- Control retained `0/60` pages, `0/60` `ListView`s, and `0/60` payload view models.
- Shared strong `ICommand` retained `60/60` `ListView`s, `60/60` payload view models, and `120.0 MB` of payload.
- Cleanup by clearing `RefreshCommand` retained `0/60` pages, `0/60` `ListView`s, and `0/60` payload view models.
