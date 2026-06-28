# iOS Context Submenu Image Retention Repro

This Mac Catalyst repro exercises the iOS/Mac Catalyst `MenuFlyoutSubItemHandler` path. Submenu items create native `UIMenu` instances and pass `MenuFlyoutSubItem.IconImageSource` into `UIMenu.Create(title, image, ...)`.

The control scenario retains the same number of native context root menus and submenus, but without assigning submenu icons. The current MAUI scenario assigns realistic 192x192 file-backed submenu icons through the production synchronous file-image path. With 100 context-menu rebuilds and 8 submenus per menu, retained submenu images represent about 112.5 MiB of native image payload.

Run:

```bash
dotnet run --project src/Controls/samples/IosContextSubmenuImageRetentionRepro/IosContextSubmenuImageRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

Results are written to `/tmp/ios-context-submenu-image-retention-results.txt`.
