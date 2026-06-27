# Menu Child Handler Retention Leak Repro

This Mac Catalyst repro demonstrates that removing items from `MenuFlyout` and
`MenuFlyoutSubItem` leaves removed child `MenuFlyoutItem` handlers connected.
If app code keeps those removed menu items for reuse, each stale handler keeps
its old `MauiContext` and scoped service graph alive.

Run:

```sh
dotnet build src/Controls/samples/MenuChildHandlerRetentionLeakRepro/MenuChildHandlerRetentionLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/MenuChildHandlerRetentionLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/MenuChildHandlerRetentionLeakRepro.app --args --results=/tmp/menuchildhandlerretentionleakrepro-results.txt
cat /tmp/menuchildhandlerretentionleakrepro-results.txt
```
