# Geolocation Static Event Retention Repro

This sample proves that transient pages/view models subscribed to `Geolocation.LocationChanged`
and `Geolocation.ListeningFailed` are retained by the app-lifetime `Geolocation.Default`
singleton when cleanup only calls `Geolocation.StopListeningForeground()`.

The leak is managed and does not require location permissions or real GPS updates. The
static event subscription itself is the root; `StopListeningForeground()` stops platform
location listeners but does not clear the managed multicast delegates.

## Run

```bash
dotnet run --project src/Controls/samples/GeolocationStaticEventRetentionRepro/GeolocationStaticEventRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
```

The harness auto-runs on launch, writes `/tmp/maui-geolocation-static-event-retention-results.txt`,
and exits.

## Local Result

Mac Catalyst on 2026-07-02:

```text
Geolocation static event subscriber retention repro
Cycles: 80
Payload per page/view-model: 1 MiB
Root under test: Geolocation.Default singleton LocationChanged/ListeningFailed multicast delegates
Cleanup under test: StopListeningForeground() without matching -= event cleanup
Leak proved: True
RESULT: PROVEN

control: transient pages never subscribe to Geolocation static events
  retained pages after full GC: 0/80
  retained view-models after full GC: 0/80
  retained payloads after full GC: 0/80
  retained payload bytes: 0

mitigation: transient pages unsubscribe with -= before cleanup
  retained pages after full GC: 0/80
  retained view-models after full GC: 0/80
  retained payloads after full GC: 0/80
  retained payload bytes: 0

current cleanup: transient pages call StopListeningForeground() but do not unsubscribe
  retained pages after full GC: 80/80
  retained view-models after full GC: 80/80
  retained payloads after full GC: 80/80
  retained payload bytes: 83,886,080
```

## Tracking Notes

Related upstream issue `dotnet/maui#36216` tracks the same static/default singleton
event-delegate class for Essentials sensors plus Connectivity/Battery, but exact upstream
searches did not find a Geolocation `LocationChanged` / `StopListeningForeground()` issue.
The fork had `repro/geolocation-timeouttoken-leak-20260626`, which tracks a different
pending timeout-token path.
