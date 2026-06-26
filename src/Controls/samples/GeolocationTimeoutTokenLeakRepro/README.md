# GeolocationTimeoutTokenLeakRepro

This repro demonstrates the retention pattern used by `GeolocationImplementation.GetLocationAsync`.

`Utils.TimeoutToken` creates a linked `CancellationTokenSource` and returns only the token, so callers cannot dispose the linked source. Android, iOS/Mac Catalyst, and Tizen geolocation then call `token.Register(...)` without keeping or disposing the registration. If the caller token source is long-lived and the location request completes normally, the caller token source retains the linked source, the linked source retains the callback registration, and the callback retains the completed native request state.

The autorun path compares that behavior with a control that disposes both the linked source and callback registration.

```sh
dotnet build src/Controls/samples/GeolocationTimeoutTokenLeakRepro/GeolocationTimeoutTokenLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/GeolocationTimeoutTokenLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/GeolocationTimeoutTokenLeakRepro.app --args --auto-run --results=/tmp/geolocationtimeouttokenleakrepro-results.txt
cat /tmp/geolocationtimeouttokenleakrepro-results.txt
```
