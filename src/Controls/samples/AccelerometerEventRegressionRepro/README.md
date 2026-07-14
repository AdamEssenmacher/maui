# AccelerometerEventRegressionRepro

This sample demonstrates a persistent retention regression in the weak event
source added for `Accelerometer.ReadingChanged` by PR #36503, introducing commit
`abc5b4b32fe5182ddfd51b08e976721b9ea0278a`.

When two composite delegates share the same final target and method, removing
the exact first composite removes the second subscription instead. The
correctly removed screen-like subscriber and its reachable state remain rooted
by `Accelerometer.Default` through repeated full garbage collections.

This is a genuine regression versus the introducing commit's exact parent,
`e243d630d43955f5ff58b4464e011012bf4dff0b`, where the same `-=` removes the
requested composite and the screen collects.

## Minimal Scenario

Two screen-level consumers compose their callbacks with one shared,
application-scoped service callback. Each delegate has a different first
handler but the same final target and method:

```csharp
EventHandler<AccelerometerChangedEventArgs> first = removedScreen.OnReadingChanged;
first += appScopedService.OnReadingChanged;

EventHandler<AccelerometerChangedEventArgs> second = activeScreen.OnReadingChanged;
second += appScopedService.OnReadingChanged;

Accelerometer.ReadingChanged += first;
Accelerometer.ReadingChanged += second;
Accelerometer.ReadingChanged -= first; // Exact operand previously added.
```

Normal multicast-event semantics remove `first` and leave `second` subscribed.
The affected implementation records only the final target and method of each
whole composite. Because those values are identical, `Unsubscribe` scans
backward and removes `second`, leaving `first` in the source.

## Retained Graph

The repro keeps the shared service alive to model an application-scoped DI
service. That live object is the `ConditionalWeakTable` key whose stored value
contains the surviving `first` composite:

```text
Application root -> app-scoped service (live CWT key)
Accelerometer.Default -> WeakEventSource -> HandlerStore value
  -> first composite delegate -> removed screen -> 1 MiB screen state
```

The retained screen and state remain alive until another matching `-=` removes
the stale source entry or the shared service dies. If neither occurs and the
service is application-lifetime, the retained graph can remain for the rest of
the process.

## What the Probe Proves

The probe:

1. creates the two composite delegates in a non-inlined method;
2. adds both and removes the exact first operand;
3. retains only weak references to the removed screen and its reachable 1 MiB
   managed state;
4. performs four forced full collections and verifies both objects remain
   alive;
5. removes the event-source entry that actually survived; and
6. performs four more full collections and verifies both objects collect.

That final collection control establishes that the event source was the
retaining root rather than merely observing objects that happened to survive a
GC.

The 1 MiB state is a deterministic managed surrogate for a page/view-model
graph. It proves that objects reachable from the subscriber are retained; it
does not estimate every page's size or claim that a native visual tree is
always retained.

## Why No Sensor Hardware Is Required

The regression is entirely in event subscription and unsubscription. The
sample uses only the public `Accelerometer.ReadingChanged` add/remove accessors,
never calls `Accelerometer.Start`, injects no readings, and uses no reflection.

## Run

Build from the repo root:

```bash
dotnet build Microsoft.Maui.BuildTasks.slnf
dotnet build src/Controls/samples/AccelerometerEventRegressionRepro/AccelerometerEventRegressionRepro.csproj -f net10.0-android
dotnet build src/Controls/samples/AccelerometerEventRegressionRepro/AccelerometerEventRegressionRepro.csproj -f net10.0-maccatalyst
dotnet build src/Controls/samples/AccelerometerEventRegressionRepro/AccelerometerEventRegressionRepro.csproj -f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64
```

Run interactively on Mac Catalyst:

```bash
dotnet run --project src/Controls/samples/AccelerometerEventRegressionRepro/AccelerometerEventRegressionRepro.csproj -f net10.0-maccatalyst
```

Run automatically, write a result file, and exit with `0` when the affected
retention signature is confirmed (`2` means it was not present):

```bash
ACCELEROMETER_EVENT_REPRO_AUTORUN=1 \
ACCELEROMETER_EVENT_REPRO_RESULTS=/private/tmp/accelerometer-event-repro-results.txt \
dotnet run --project src/Controls/samples/AccelerometerEventRegressionRepro/AccelerometerEventRegressionRepro.csproj -f net10.0-maccatalyst -- \
  --auto-run \
  --results=/private/tmp/accelerometer-event-repro-results.txt
```

Run automatically on Android:

```bash
dotnet run --project src/Controls/samples/AccelerometerEventRegressionRepro/AccelerometerEventRegressionRepro.csproj \
  -f net10.0-android \
  -p:AccelerometerEventRegressionReproAutoRun=true
adb shell run-as com.microsoft.maui.accelerometereventregressionrepro \
  find . -name accelerometer-event-repro-results.txt -print -exec cat {} \;
```

## Observed Differential

The exact `AccelerometerProbe.cs` used by this app was compiled into a headless
`net10.0` harness and run against real `Microsoft.Maui.Essentials` assemblies
from both revisions:

| Evidence | Parent `e243d630d4` | `inflight/current` `d31bd4e615` |
| --- | ---: | ---: |
| Removed screen alive after exact `-=` and four full GCs | no | yes |
| Reachable 1 MiB state alive | no | yes |
| Removed screen collected after source cleanup | yes | yes |
| Reachable state collected after source cleanup | yes | yes |

The expected affected report begins with:

```text
RESULT: AFFECTED RETENTION REGRESSION CONFIRMED

After exact -= and four full GCs
  removed screen alive: True
  reachable 1,048,576-byte screen state alive: True
  removed graph persistently retained: True
```

## Trigger Scope

- The collision requires at least two already-combined delegates whose final
  invocation has the same target and method.
- Ordinary handlers added with separate `+=` statements do not trigger this
  composite-identity collision.
- The shared final target must remain alive, and no later matching `-=` may
  clear the stale entry, for the demonstrated retention to persist.
- The sample runtime-tests `ReadingChanged`. `ShakeDetected` uses the analogous
  non-generic source, so impact there is source-inferred only.

A correct implementation must preserve full multicast `Delegate.Combine` /
`Delegate.Remove` behavior, including duplicate registrations and removal of
the last matching contiguous invocation-list sequence. Matching only a
composite delegate's final target and method is insufficient.

See `GITHUB_ISSUE_DRAFT.md` for the issue report based on this evidence.
