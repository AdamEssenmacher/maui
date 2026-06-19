# ActivityStateManagerLeakRepro

This Android-only sample demonstrates an `ActivityStateManager` lifecycle callback leak.

`ActivityStateManagerImplementation.Init(Activity, Bundle?)` calls `Init(Application)` for every activity creation. `Init(Application)` creates a new `ActivityLifecycleContextListener` and registers it with `Application.RegisterActivityLifecycleCallbacks`, but the listener is never unregistered and registration is not idempotent.

The sample proves the leak by:

- recreating the Android `Activity` with real `Activity.Recreate()` calls;
- counting `ActivityLifecycleContextListener` registrations in `MainApplication`;
- storing only weak references to those listeners and counting which ones remain alive after full GC;
- subscribing realistic app handlers to `Platform.ActivityStateChanged` and counting duplicate fan-out.

## Run

From the repository root:

```bash
dotnet build src/Controls/samples/ActivityStateManagerLeakRepro/ActivityStateManagerLeakRepro.csproj -f net10.0-android
dotnet build src/Controls/samples/ActivityStateManagerLeakRepro/ActivityStateManagerLeakRepro.csproj -f net10.0-android -t:Run
```

The dashboard defaults model a common long-running field app:

- `120` activity recreations across a work shift
- `4` lifecycle subscribers
- `25 ms` estimated work per subscriber notification
- `250 ms` between recreations

## Autorun

Build with:

```bash
dotnet build src/Controls/samples/ActivityStateManagerLeakRepro/ActivityStateManagerLeakRepro.csproj -f net10.0-android -p:ActivityStateManagerLeakReproAutoRun=true
```

The app writes an autorun report to app local storage, with a `/tmp` fallback path where available. You can also set:

- `ACTIVITY_STATE_MANAGER_LEAK_REPRO_AUTORUN=1`
- `ACTIVITY_STATE_MANAGER_LEAK_REPRO_RESULTS=/path/to/autorun-results.txt`

## Expected buggy result

On a buggy build, the report should show:

- `ActivityStateManager listener registrations during run` approximately equal to the recreate count;
- `ActivityStateManager listener unregisters during run` equal to `0`;
- `ActivityStateManager listeners alive after full GC` growing with the number of recreations;
- `Platform.ActivityStateChanged subscriber invocations` far above the one-listener expectation.

The retained listener objects are small. The severity is the unbounded duplicate lifecycle fan-out: apps often use lifecycle events to reconnect scanners, resume sync, refresh auth state, restart location/beacon scans, or write telemetry. After enough recreations, a single real Android lifecycle transition can trigger dozens or hundreds of duplicate app-level handlers.
