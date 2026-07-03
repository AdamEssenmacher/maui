# Startup Registration Delegate Retention Repro

This repro checks whether MAUI keeps startup-only `Configure*` delegates alive after a live app has already initialized its runtime registration state.

The sample creates collectible dynamic registration target types with 1 MiB payloads. Each target registers five no-op startup delegates through:

- `ConfigureFonts`
- `ConfigureMauiHandlers`
- `ConfigureImageSources`
- `ConfigureLifecycleEvents`
- `ConfigureEssentials`

The delegates intentionally do not register dynamic fonts, handlers, image-source services, lifecycle callbacks, or Essentials actions. That isolates this from the already cataloged runtime metadata roots such as C473, C480, C482, C484, and C485.

## Run

```bash
dotnet run --project src/Core/samples/StartupRegistrationDelegateRetentionRepro/StartupRegistrationDelegateRetentionRepro.csproj -c Release -- --results=/tmp/startup-registration-delegate-retention-results.txt
```

Latest local result:

```text
MAUI startup registration delegate retention repro
Result: PROVEN

Dynamic startup registration targets: 40
Delegates per target: 5
Payload per target: 1 MiB

Control: dynamic startup registration delegate fields replaced with no-op delegates before forced GC while the app remains live
  Dynamic registration delegates before collect: 200
  Dynamic registration delegates after collect: 0
  Retained assemblies: 0
  Retained target types: 0
  Retained target instances: 0
  Retained payloads: 0
  Retained payload bytes: 0
  Managed heap delta: 466,576 bytes

Current MAUI: startup registration delegate fields left intact while the app remains live
  Dynamic registration delegates before collect: 200
  Dynamic registration delegates after collect: 200
  Retained assemblies: 40
  Retained target types: 40
  Retained target instances: 40
  Retained payloads: 40
  Retained payload bytes: 41,943,040
  Managed heap delta: 42,161,832 bytes
```

Optional scale control:

```bash
dotnet run --project src/Core/samples/StartupRegistrationDelegateRetentionRepro/StartupRegistrationDelegateRetentionRepro.csproj -c Release -- --registrations=20 --payload-mib=2
```

## Tracking Check

Official `dotnet/maui` searches for startup registration delegate, configure delegate, `configureDelegate`, `FontsRegistration`, `EssentialsRegistration`, `HandlerRegistration`, `ImageSourceRegistration`, `EffectsRegistration`, and the `ConfigureFonts` / `ConfigureEssentials` / `ConfigureEffects` / `ConfigureMauiHandlers` / `ConfigureImageSources` memory-retention terms found no exact issue for this startup delegate-retention class. Fork branch filters for registration/configure/startup delegate terms found adjacent delegate/metadata repro branches, but no exact startup registration delegate repro.
