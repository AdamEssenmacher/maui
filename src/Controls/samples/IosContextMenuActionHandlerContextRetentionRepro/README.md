# iOS Context Menu UIAction Handler Context Retention Repro

This Mac Catalyst repro exercises the iOS/Mac Catalyst context `MenuFlyoutItemHandler` path. Context flyout items create `UIAction` instances with a callback block that captures the item handler through `VirtualView?.Clicked()`. Handler disconnect clears `VirtualView` and `PlatformView`, but it does not clear `MauiContext`, so a retained native action can keep the disconnected handler and its old context graph alive.

The control scenario builds and retains the same MAUI-created native menu/actions, then simulates the minimal framework cleanup by clearing `ElementHandler.MauiContext` on the disconnected item handlers. The current scenario retains the real MAUI-created native menu/actions without that cleanup. Each cycle uses a throwaway `IMauiContext` wrapper with a 1 MiB payload so the retained context graph is visible in managed heap and weak-reference counts.

Run:

```bash
dotnet run --project src/Controls/samples/IosContextMenuActionHandlerContextRetentionRepro/IosContextMenuActionHandlerContextRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

Results are written to `/tmp/ios-context-menu-uiaction-handler-context-retention-results.txt`.
