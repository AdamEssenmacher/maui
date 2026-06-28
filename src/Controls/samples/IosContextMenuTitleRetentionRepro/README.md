# iOS Context Menu UIAction Title Retention Repro

This Mac Catalyst repro exercises the iOS/Mac Catalyst context `MenuFlyoutItemHandler` path. Context flyout items create native `UIAction` instances with `title: VirtualView.Text`.

The autorun scenario creates 256 context-menu rebuilds with 8 generated workflow action labels per menu. Each label is 8 KiB, for 2,048 retained native action title slots. The control path explicitly clears every retained `UIAction.Title` before retaining the native menu; the current MAUI path leaves the native action titles assigned after the flyout and menu items collect.

Run:

```bash
dotnet run --project src/Controls/samples/IosContextMenuTitleRetentionRepro/IosContextMenuTitleRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

Results are written to `/tmp/ios-context-menu-title-retention-results.txt`.
