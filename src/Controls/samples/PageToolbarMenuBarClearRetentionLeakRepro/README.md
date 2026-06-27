# Page toolbar and menu bar clear retention leak repro

This sample proves whether `Page.ToolbarItems.Clear()` and `Page.MenuBarItems.Clear()` leave removed items parented to a live page.

Run:

```sh
dotnet build src/Controls/samples/PageToolbarMenuBarClearRetentionLeakRepro/PageToolbarMenuBarClearRetentionLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/PageToolbarMenuBarClearRetentionLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/PageToolbarMenuBarClearRetentionLeakRepro.app --args --results=/tmp/pagetoolbarmenubarclearretentionleakrepro-results.txt
cat /tmp/pagetoolbarmenubarclearretentionleakrepro-results.txt
```
