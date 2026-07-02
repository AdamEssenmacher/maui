# DispatcherProvider Static Current Retention Repro

This repro proves that a custom `IDispatcherProvider` registered in a throwaway `MauiApp` can remain rooted by the process-static `DispatcherProvider.s_currentProvider` field after the app is disposed.

`ConfigureDispatching()` resolves the app's registered `IDispatcherProvider` during `MauiAppBuilder.Build()`. `GetDispatcher(...)` then calls `DispatcherProvider.SetCurrent(provider)`, which stores that provider in a process-static field. Disposing the `MauiApp` does not clear the field. This is normally invisible when MAUI uses the built-in default provider, but it can retain provider/backend state from embedding hosts, tests, plugin hosts, or custom platform backends that override `IDispatcherProvider`.

The repro creates one collectible dynamic assembly that defines a custom provider implementing both `IDispatcherProvider` and `IDispatcher`. The provider instance owns a 64 MiB payload, and the provider type owns a 64 MiB static payload. The repro builds and disposes a real `MauiApp` with that provider registered, then compares clearing versus leaving `DispatcherProvider.s_currentProvider`.

## Result

Run the command below to write the current result:

```bash
dotnet run --project src/Core/samples/DispatcherProviderStaticCurrentRetentionRepro/DispatcherProviderStaticCurrentRetentionRepro.csproj -c Release -- --results=/tmp/dispatcherprovider-static-current-retention-results.txt
```

Latest local result:

```text
MAUI DispatcherProvider static-current retention repro
Result: PROVEN

Trigger:
  ConfigureDispatching() resolves the app's registered IDispatcherProvider during MauiAppBuilder.Build().
  GetDispatcher(...) copies that provider into DispatcherProvider.s_currentProvider via DispatcherProvider.SetCurrent(provider).
  Disposing the MauiApp does not clear the process-static current provider.
  A custom provider registered by an embedding, test, plugin, or nonstandard backend can therefore remain rooted after the app is disposed.

Instance payload on dynamic provider: 64 MiB
Static payload on dynamic provider type: 64 MiB

Control: DispatcherProvider.s_currentProvider cleared after app disposal and before forced GC
  Current provider before collect: PluginDispatcherProvider
  Current provider after collect: <null>
  Retained assemblies: 0
  Retained provider types: 0
  Retained provider instances: 0
  Retained instance payloads: 0
  Retained static payloads: 0
  Retained payload bytes: 0
  Managed heap delta: 51,192 bytes

Current MAUI: DispatcherProvider.s_currentProvider left intact after app disposal
  Current provider before collect: PluginDispatcherProvider
  Current provider after collect: PluginDispatcherProvider
  Retained assemblies: 1
  Retained provider types: 1
  Retained provider instances: 1
  Retained instance payloads: 1
  Retained static payloads: 1
  Retained payload bytes: 134,217,728
  Managed heap delta: 134,244,704 bytes
```

Optional scale control:

```bash
dotnet run --project src/Core/samples/DispatcherProviderStaticCurrentRetentionRepro/DispatcherProviderStaticCurrentRetentionRepro.csproj -c Release -- --payload-mib=128
```

## Tracking Check

Official `dotnet/maui` issue searches for `DispatcherProvider memory leak`, `DispatcherProvider SetCurrent`, `IDispatcherProvider leak`, and `ConfigureDispatching memory` found no exact memory-retention issue. The only relevant hits were global-state test race and custom-backend behavior PRs, not disposed-app provider retention. Fork branch filters for dispatcher-provider terms found no existing repro branch for this class.
