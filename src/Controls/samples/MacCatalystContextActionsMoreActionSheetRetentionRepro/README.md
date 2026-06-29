# Mac Catalyst ContextActions More Action Sheet Retention Repro

This repro mirrors the `ActivateMore()` action-sheet construction in the iOS
compatibility `ContextActionsCell` implementations:

- `src/Controls/src/Core/Compatibility/Handlers/ListView/iOS/ContextActionCell.cs`
- `src/Compatibility/Core/src/iOS/ContextActionCell.cs`

The source creates a `UIAlertController` and adds `UIAlertAction` callbacks that
use `_scroller`, so the callback captures the `ContextActionsCell`. `Dispose()`
clears `_scroller`, `_cell`, `_tableView`, buttons, and menu subscriptions, but
it does not clear `ContentCell`. If the native More action sheet/actions survive
native cleanup timing, the callback can keep the disposed context-action cell and
its native row payload alive.

Run from the repository root:

```sh
dotnet build src/Controls/samples/MacCatalystContextActionsMoreActionSheetRetentionRepro/MacCatalystContextActionsMoreActionSheetRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false -v:minimal -clp:Summary
artifacts/bin/MacCatalystContextActionsMoreActionSheetRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/MacCatalystContextActionsMoreActionSheetRetentionRepro.app/Contents/MacOS/MacCatalystContextActionsMoreActionSheetRetentionRepro
```

The app writes its result to
`/tmp/maccatalyst-contextactions-more-actionsheet-retention-results.txt`.
