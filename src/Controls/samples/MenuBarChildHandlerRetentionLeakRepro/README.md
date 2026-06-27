# MenuBar child handler retention leak repro

This sample proves that removed `MenuFlyoutSubItem` children under a live `MenuBarItem` keep their handlers connected unless callers explicitly disconnect the removed child handler.

Run:

```sh
dotnet build src/Controls/samples/MenuBarChildHandlerRetentionLeakRepro/MenuBarChildHandlerRetentionLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/MenuBarChildHandlerRetentionLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/MenuBarChildHandlerRetentionLeakRepro.app --args --results=/tmp/menubarchildhandlerretentionleakrepro-results.txt
cat /tmp/menubarchildhandlerretentionleakrepro-results.txt
```
