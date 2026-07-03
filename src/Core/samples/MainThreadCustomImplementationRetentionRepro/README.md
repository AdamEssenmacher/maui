# MainThread Custom Implementation Retention Repro

This repro proves that the netstandard/external-TFM MainThread bridge can retain a disposed app's custom dispatcher provider through the process-static `MainThread.s_mainThreadImplementation` field.

On non-platform TFMs, `UseEssentials()` registers `MainThreadBridgeInitializer`. During `MauiAppBuilder.Build()`, the initializer resolves the app dispatcher and passes two lambdas that capture it into `MainThread.SetCustomImplementation(...)`. `MainThread` stores those delegates in a static implementation object and does not clear them on `MauiApp.Dispose()`.

The repro creates one collectible dynamic assembly with a custom provider implementing both `IDispatcherProvider` and `IDispatcher`. The provider instance owns a 128 MiB payload. It registers that provider in a real `MauiApp`, lets `UseEssentials()` install the MainThread bridge, disposes the app, clears `DispatcherProvider.s_currentProvider` in both scenarios to isolate from C487, and then compares clearing versus leaving `MainThread.s_mainThreadImplementation`.

## Result

Run the command below to write the current result:

```bash
dotnet run --project src/Core/samples/MainThreadCustomImplementationRetentionRepro/MainThreadCustomImplementationRetentionRepro.csproj -c Release -- --results=/tmp/mainthread-custom-implementation-retention-results.txt
```

Latest local result:

```text
MAUI MainThread custom implementation retention repro
Result: PROVEN

Trigger:
  On netstandard/external TFMs, UseEssentials() registers MainThreadBridgeInitializer.
  During MauiAppBuilder.Build(), the initializer resolves the app dispatcher and passes lambdas capturing it to MainThread.SetCustomImplementation(...).
  MainThread stores those delegates in process-static s_mainThreadImplementation and MauiApp.Dispose() does not clear them.
  This repro clears DispatcherProvider.s_currentProvider in both scenarios to isolate this from C487.

Payload on dynamic dispatcher provider: 128 MiB

Control: MainThread.s_mainThreadImplementation cleared after app disposal and before forced GC
  MainThread implementation before collect: Microsoft.Maui.ApplicationModel.MainThread+MainThreadImplementation
  MainThread implementation after collect: <null>
  DispatcherProvider current after collect: <null>
  Retained assemblies: 0
  Retained provider types: 0
  Retained provider instances: 0
  Retained payloads: 0
  Retained payload bytes: 0
  Managed heap delta: 78,200 bytes

Current MAUI: MainThread.s_mainThreadImplementation left intact after app disposal
  MainThread implementation before collect: Microsoft.Maui.ApplicationModel.MainThread+MainThreadImplementation
  MainThread implementation after collect: Microsoft.Maui.ApplicationModel.MainThread+MainThreadImplementation
  DispatcherProvider current after collect: <null>
  Retained assemblies: 1
  Retained provider types: 1
  Retained provider instances: 1
  Retained payloads: 1
  Retained payload bytes: 134,217,728
  Managed heap delta: 134,280,232 bytes
```

Optional scale control:

```bash
dotnet run --project src/Core/samples/MainThreadCustomImplementationRetentionRepro/MainThreadCustomImplementationRetentionRepro.csproj -c Release -- --payload-mib=64
```

## Tracking Check

Official `dotnet/maui` issue searches for `MainThread SetCustomImplementation memory leak`, `MainThreadBridgeInitializer leak`, `s_mainThreadImplementation`, and `MainThread custom backend memory` found no exact memory-retention issue. The relevant hits were feature/custom-backend PRs and global-state test race work, not disposed-app dispatcher retention. Fork branch filters for MainThread/custom-implementation bridge terms found no existing repro branch for this class.
