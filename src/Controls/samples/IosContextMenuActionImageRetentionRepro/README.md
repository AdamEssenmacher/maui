# iOS Context Menu UIAction Image Retention Repro

This Mac Catalyst repro exercises the iOS/Mac Catalyst context `MenuFlyoutItemHandler` path. Context flyout items create `UIAction` instances and assign `UIAction.Image` from `MenuItem.IconImageSource` through the synchronous file-image path.

The control scenario builds the same native `UIMenu` graph and then explicitly clears every `UIAction.Image` before retaining the native menu. The current MAUI scenario keeps the retained native menus as MAUI leaves them after handler disconnect. With 100 context-menu rebuilds and 8 realistic 192x192 action icons per menu, retained action images represent about 112.5 MiB of native image payload.

Run:

```bash
dotnet run --project src/Controls/samples/IosContextMenuActionImageRetentionRepro/IosContextMenuActionImageRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

Results are written to `/tmp/ios-context-menu-uiaction-image-retention-results.txt`.
