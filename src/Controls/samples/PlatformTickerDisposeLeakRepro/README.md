# PlatformTickerDisposeLeakRepro

Mac Catalyst repro for active `PlatformTicker` instances not being stopped when
their owning `AnimationManager` is disposed. On iOS and Mac Catalyst,
`PlatformTicker` uses a `CADisplayLink`, but the ticker does not implement
`IDisposable`. `AnimationManager.Dispose()` only disposes tickers that implement
`IDisposable`, so a running display link can keep the ticker, manager, active
animation, and animation payload alive.

## Run

```bash
dotnet build src/Controls/samples/PlatformTickerDisposeLeakRepro/PlatformTickerDisposeLeakRepro.csproj \
  -f net10.0-maccatalyst \
  -p:UseMaui=false

open -W artifacts/bin/PlatformTickerDisposeLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-*/PlatformTickerDisposeLeakRepro.app
cat "$HOME/Library/Containers/com.microsoft.maui.platformtickerdisposeleakrepro/Data/Library/autorun-results.txt"
```

## Result

On Mac Catalyst, built from commit `a6d9e30a62`:

```text
RESULT: PROVEN
direct-stop-control: payloads=0/80, tickers=0/80
direct-running-no-dispose: payloads=80/80, tickers=80/80
animation-manager-running-dispose: payloads=80/80, tickers=80/80, managers=80/80
payload-bytes-per-scenario=83886080
app-data-directory=/Users/adam/Library/Containers/com.microsoft.maui.platformtickerdisposeleakrepro/Data/Library
dotnet-version=10.0.7
```
