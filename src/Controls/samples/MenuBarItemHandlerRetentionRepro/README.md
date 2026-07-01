# MenuBarItem handler retention repro

This sample proves that removed root `MenuBarItem`s keep their handlers connected unless callers explicitly disconnect the removed root item handler.

The repro retains only the removed root `MenuBarItem`s in app code. Each handler has a throwaway `MauiContext` whose service provider carries a 1 MiB payload. The control path disconnects the removed root item handler; current MAUI only removes the item from the `MenuBar`, leaving the item-to-handler-to-`MauiContext` graph alive.

Run:

```sh
dotnet build src/Controls/samples/MenuBarItemHandlerRetentionRepro/MenuBarItemHandlerRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/MenuBarItemHandlerRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/MenuBarItemHandlerRetentionRepro.app --args --results=/tmp/menubaritemhandlerretentionrepro-results.txt
cat /tmp/menubaritemhandlerretentionrepro-results.txt
```
