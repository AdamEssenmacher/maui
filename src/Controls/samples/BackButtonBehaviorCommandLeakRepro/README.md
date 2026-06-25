# BackButtonBehavior command leak repro

This sample demonstrates that `BackButtonBehavior.Command` can retain closed Shell page behavior objects when the command is a long-lived `ICommand` implementation with a normal strong `CanExecuteChanged` event.

`BackButtonBehavior.OnCommandChanged` subscribes `newCommand.CanExecuteChanged += CanExecuteChanged` and only detaches when the `Command` property changes. If an app uses a shared command for short-lived Shell pages and those pages close without clearing `BackButtonBehavior.Command`, the command can keep each `BackButtonBehavior` alive.

The app runs three scenarios:

1. Control: create Shell pages with `BackButtonBehavior` objects but no command.
2. Leak: create Shell pages whose `BackButtonBehavior.Command` points at one shared strong command.
3. Cleanup: create pages with the shared command, then clear `BackButtonBehavior.Command`.

The built-in MAUI `Command` uses weak event handlers. This repro uses a custom strong `ICommand` because many app and MVVM command implementations expose a normal strong `CanExecuteChanged` event.

## Mac Catalyst autorun

```bash
dotnet build src/Controls/samples/BackButtonBehaviorCommandLeakRepro/BackButtonBehaviorCommandLeakRepro.csproj -f net10.0-maccatalyst
open -W artifacts/bin/BackButtonBehaviorCommandLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/BackButtonBehaviorCommandLeakRepro.app --args \
  --auto-run \
  --results=/private/tmp/backbuttonbehaviorcommandleakrepro-results.txt
```

If the sandbox blocks `/private/tmp`, the app writes the report to:

`~/Library/Containers/com.microsoft.maui.backbuttonbehaviorcommandleakrepro/Data/Documents/BackButtonBehaviorCommandLeakRepro/autorun-results.txt`

Observed Mac Catalyst autorun result on 2026-06-25:

- Control retained `0/60` pages, `0/60` `BackButtonBehavior`s, and `0/60` payload view models.
- Shared strong `ICommand` retained `60/60` `BackButtonBehavior`s, `60/60` payload view models, and `120.0 MB` of payload.
- Cleanup by clearing `BackButtonBehavior.Command` retained `0/60` pages, `0/60` `BackButtonBehavior`s, and `0/60` payload view models.
