# IosMapPinRemovedPinHandlerRetentionRepro

This repro targets the iOS/Mac Catalyst `MapHandler` pin rebuild path where
removed `Pin` instances can keep their internal `MapPinHandler`, `MauiContext`,
service provider, and native annotation marker state after removal from
`Map.Pins`.

The app intentionally keeps removed `Pin` models alive in a long-lived list.
That is a normal app pattern for route, cache, or view-model pin collections.
The leak is that `MauiMKMapView.AddPins()` removes old native annotations but
does not disconnect or clear the handler/`MarkerId` stored on pins that were
removed from the map.

## Run

```bash
dotnet build src/Controls/samples/IosMapPinRemovedPinHandlerRetentionRepro/IosMapPinRemovedPinHandlerRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true
open -W artifacts/bin/IosMapPinRemovedPinHandlerRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/IosMapPinRemovedPinHandlerRetentionRepro.app
cat /tmp/ios-mappin-removed-pin-handler-retention-results.txt
```

The harness auto-runs on launch and exits with code `0` when the leak is
proved.

Observed Mac Catalyst proof on 2026-07-01 retained 80 removed `Pin` models in
both scenarios. The explicit cleanup control retained `0/80` removed pin
handlers, `0/80` `MauiContext` graphs, and `0 B` of context payload. Current
MAUI retained `80/80` removed pin handlers, `80/80` `MauiContext` graphs, and
`80.0 MiB` of context payload while `Map`s and `MapHandler`s collected.
