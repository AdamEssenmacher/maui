# [inflight regression] Accelerometer exact composite unsubscription can retain the removed subscriber

### Regression

Introduced by #36503 / commit
`abc5b4b32fe5182ddfd51b08e976721b9ea0278a` (parent
`e243d630d43955f5ff58b4464e011012bf4dff0b`) and currently present in
`inflight/current` at `d31bd4e615c81445ea355c70c1eae2b25f1d7149`.

Related multicast-delegate review discussion:
https://github.com/dotnet/maui/pull/36503#discussion_r3570753528

### Summary

The weak event source added for `Accelerometer.ReadingChanged` can persistently
retain a subscriber even when the application removes the exact composite
delegate it previously added.

If two composite subscriptions share the same final target and method,
`Unsubscribe` removes the most recent target/method match instead of the exact
requested composite. The requested composite remains in the event source and
strongly retains its screen-like subscriber and everything reachable from it.

This is a genuine regression versus the introducing commit's exact parent,
where the same `-=` removes the requested composite and the subscriber
collects.

### Repro Project

https://github.com/AdamEssenmacher/maui/tree/repro/accelerometer-weak-event-regression/src/Controls/samples/AccelerometerEventRegressionRepro

The project uses only the public `Accelerometer.ReadingChanged` add/remove
accessors. It never starts the sensor, injects no readings, and requires no
physical sensor hardware.

### Minimal Scenario

Two screen-level consumers compose their callbacks with one shared,
application-scoped service callback:

```csharp
EventHandler<AccelerometerChangedEventArgs> first = removedScreen.OnReadingChanged;
first += appScopedService.OnReadingChanged;

EventHandler<AccelerometerChangedEventArgs> second = activeScreen.OnReadingChanged;
second += appScopedService.OnReadingChanged;

Accelerometer.ReadingChanged += first;
Accelerometer.ReadingChanged += second;
Accelerometer.ReadingChanged -= first; // Exact operand previously added.
```

Both composites end with the same `appScopedService.OnReadingChanged` target
and method, but their first handlers are different.

### Steps to Reproduce

1. Check out the repro branch linked above.
2. From the repository root, build the repo tasks and sample:

   ```bash
   dotnet build Microsoft.Maui.BuildTasks.slnf
   dotnet build src/Controls/samples/AccelerometerEventRegressionRepro/AccelerometerEventRegressionRepro.csproj -f net10.0-maccatalyst
   ```

3. Run the sample and press **Run retention probe**:

   ```bash
   dotnet run --project src/Controls/samples/AccelerometerEventRegressionRepro/AccelerometerEventRegressionRepro.csproj -f net10.0-maccatalyst
   ```

The probe drops its local references to `removedScreen` and `first`, forces four
full garbage collections, and checks weak references to the screen and a
reachable 1 MiB managed state object. It then removes the event-source entry
that actually survived, forces four more full collections, and checks both weak
references again.

### Expected Behavior

`Accelerometer.ReadingChanged -= first` removes the exact composite previously
added. `removedScreen` and everything reachable only through it are eligible
for collection.

After four full GCs:

- `removedScreen` is collected; and
- its reachable 1 MiB state object is collected.

### Actual Behavior

On `inflight/current`, both objects remain alive through all four full GCs.

After the repro removes the stale event-source entry and forces four more full
collections, both objects collect. This control confirms that the event source
was the retaining root rather than merely observing objects that happened to
survive GC.

```text
RESULT: AFFECTED RETENTION REGRESSION CONFIRMED

After exact -= and four full GCs
  removed screen alive: True
  reachable 1,048,576-byte screen state alive: True
  removed graph persistently retained: True

After removing the remaining event-source entry and four more full GCs
  removed screen collected: True
  reachable screen state collected: True
  event source confirmed as retaining root: True
```

### Parent / Inflight Differential

| Evidence | Parent `e243d630d4` | `inflight/current` `d31bd4e615` |
| --- | ---: | ---: |
| Removed screen alive after exact `-=` and four full GCs | no | yes |
| Reachable 1 MiB state alive | no | yes |
| Removed screen collected after source cleanup | yes | yes |
| Reachable state collected after source cleanup | yes | yes |

The exact `AccelerometerProbe.cs` from the sample produced this differential
against real `Microsoft.Maui.Essentials` assemblies from both revisions.

### Cause

`Subscribe` treats an already-combined delegate as one subscription. It records
only `handler.Target` and `handler.Method`, which for a multicast delegate
describe its final invocation, while storing the entire composite delegate in
the `ConditionalWeakTable` value.

Both composites in the repro therefore have the same recorded identity:
`appScopedService` plus `OnReadingChanged`. `Unsubscribe` scans backward and
removes the first target/method match. When passed `first`, it finds and removes
the more recently added `second` entry, leaving `first` behind.

The app-scoped service is kept alive as the table key. Its stored handler value
contains the entire surviving `first` delegate, which strongly references the
removed screen and its reachable graph.

### Retained Graph

```text
Application root -> app-scoped service (live CWT key)
Accelerometer.Default -> WeakEventSource -> HandlerStore value
  -> first composite delegate -> removed screen -> 1 MiB screen state
```

The retained graph remains alive until another matching `-=` clears the stale
entry or the shared service dies. If neither occurs and the service is
application-lifetime, the retention can last for the remainder of the process.

### Real-World Impact

Applications can correctly retain and remove the exact composite delegate they
registered and still leave a navigated-away page or view model rooted by
`Accelerometer.Default`.

Anything reachable from that subscriber can remain alive with it, including
managed page state, a binding context, a view model, cached data, and captured
services. The repro uses a deterministic 1 MiB managed object to make this
retained graph observable; it does not claim that every page is exactly 1 MiB
or that every platform necessarily retains a native visual tree.

### Trigger Scope

- The collision requires at least two composite delegates whose final
  invocation has the same target and method.
- Separate ordinary `ReadingChanged += handler` statements do not trigger this
  composite-identity collision.
- The shared final target must remain alive, and no later matching `-=` may
  clear the stale entry, for the retention to persist.
- The project runtime-tests `ReadingChanged`. `ShakeDetected` uses the analogous
  non-generic source, so impact there is source-inferred only.

### Suggested Fix

Preserve full multicast `Delegate.Combine` / `Delegate.Remove` semantics,
including duplicate registrations and removal of the last matching contiguous
invocation-list sequence. Matching only a composite delegate's final target and
method is insufficient.

### Environment

- Repro comparison: `net10.0`, arm64 macOS
- .NET SDK: `10.0.203`
- Introducing commit: `abc5b4b32fe5182ddfd51b08e976721b9ea0278a`
- Exact parent baseline: `e243d630d43955f5ff58b4464e011012bf4dff0b`
- Tested inflight tip: `d31bd4e615c81445ea355c70c1eae2b25f1d7149`
