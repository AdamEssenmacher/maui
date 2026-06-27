# GradientStops.Clear retention leak repro

This sample proves whether `GradientBrush.GradientStops.Clear()` leaves removed `GradientStop` instances parented to a live brush.

Run:

```sh
dotnet build src/Controls/samples/GradientStopsClearRetentionLeakRepro/GradientStopsClearRetentionLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/GradientStopsClearRetentionLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/GradientStopsClearRetentionLeakRepro.app --args --results=/tmp/gradientstopsclearretentionleakrepro-results.txt
cat /tmp/gradientstopsclearretentionleakrepro-results.txt
```
