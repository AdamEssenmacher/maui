# EffectsRegistration Delegate Retention Repro

This repro checks whether MAUI keeps startup-only `ConfigureEffects` delegates alive after the app has already built its service provider.

The sample creates collectible dynamic registration target types with 1 MiB payloads. Each target registers one no-op `ConfigureEffects` delegate. The delegate intentionally does not call `IEffectsBuilder.Add`, so no dynamic effect or platform-effect types are registered and the already-tracked `EffectsFactory._registeredEffects` type-retention path is not needed for the retained graph.

## Run

```bash
dotnet run --project src/Controls/samples/EffectsRegistrationDelegateRetentionRepro/EffectsRegistrationDelegateRetentionRepro.csproj -c Release -- --results=/tmp/effects-registration-delegate-retention-results.txt
```

Expected result: the control replaces only `EffectsRegistration._registerEffects` with no-op delegates while keeping the app/provider live and retains 0 dynamic targets. Current MAUI leaves those delegate fields intact and retains every dynamic target, its collectible assembly, and its payload.

Latest local result:

```text
MAUI EffectsRegistration startup delegate retention repro
Result: PROVEN

Dynamic effect startup registration targets: 80
Delegates per target: 1
Payload per target: 1 MiB

Control: EffectsRegistration._registerEffects replaced with no-op delegates before forced GC while the app remains live
  Dynamic registration delegates before collect: 80
  Dynamic registration delegates after collect: 0
  EffectsFactory entries before collect: 0
  EffectsFactory entries after collect: 0
  Retained assemblies: 0
  Retained target types: 0
  Retained target instances: 0
  Retained payloads: 0
  Retained payload bytes: 0
  Managed heap delta: 188,080 bytes

Current MAUI: EffectsRegistration._registerEffects left intact while the app remains live
  Dynamic registration delegates before collect: 80
  Dynamic registration delegates after collect: 80
  EffectsFactory entries before collect: 0
  EffectsFactory entries after collect: 0
  Retained assemblies: 80
  Retained target types: 80
  Retained target instances: 80
  Retained payloads: 80
  Retained payload bytes: 83,886,080
  Managed heap delta: 84,002,552 bytes
```

Optional scale control:

```bash
dotnet run --project src/Controls/samples/EffectsRegistrationDelegateRetentionRepro/EffectsRegistrationDelegateRetentionRepro.csproj -c Release -- --registrations=40 --payload-mib=2
```

## Tracking Check

Official `dotnet/maui` searches for `EffectsRegistration`, `ConfigureEffects`, `IEffectsBuilder`, startup delegate, registration delegate, memory, leak, retain, and retention terms found no exact issue for this delegate-target retention path. Fork branch filters for effects delegate/startup/configure terms found adjacent repro branches, including `startup-registration-delegate-retention` and `effectsfactory-registration-type-retention`, but no exact `EffectsRegistration._registerEffects` delegate-target repro.
