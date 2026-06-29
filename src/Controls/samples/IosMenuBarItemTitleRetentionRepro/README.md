# iOS Menu Bar Item Title Retention Repro

This Mac Catalyst repro exercises the iOS/Mac Catalyst non-context `MenuBarItemHandler` path. Menu bar items create native root `UIMenu` instances through `MenuExtensions.ToPlatformMenu(...)`, copying `MenuBarItem.Text` into immutable native menu title state.

The autorun scenario creates 1,024 native root menu-bar menus. The current path uses 8 KiB generated workflow group labels. The control path retains the same number of native menus with short labels. Child menu commands use short labels and no icons, and both paths clear the static `MenuFlyoutItemHandler.menus` dictionary after every cycle so the older static-menu and command-state leaks are not part of this proof.

Run:

```bash
dotnet run --project src/Controls/samples/IosMenuBarItemTitleRetentionRepro/IosMenuBarItemTitleRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

Results are written to `/tmp/ios-menubaritem-title-retention-results.txt`.
