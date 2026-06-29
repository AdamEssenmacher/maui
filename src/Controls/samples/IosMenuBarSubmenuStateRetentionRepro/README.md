# iOS Menu Bar Submenu State Retention Repro

This Mac Catalyst repro exercises the iOS/Mac Catalyst non-context `MenuFlyoutSubItemHandler` path under a `MenuBar` / `MenuBarItem` parent. Menu bar subitems create native `UIMenu` instances through `MenuExtensions.ToPlatformMenu(...)`, copying `MenuFlyoutSubItem.Text` and `IconImageSource` into immutable native submenu title and image state.

The autorun scenario creates 128 cycles with 8 native menu-bar submenus per cycle. The current path uses 8 KiB generated workflow group labels and 192 x 192 generated PNG icons. The control path retains the same number of native submenus with short labels and no images. Child menu commands use short labels and no icons, and both paths clear the static `MenuFlyoutItemHandler.menus` dictionary after every cycle so the older static-menu and command-state leaks are not part of this proof.

Run:

```bash
dotnet run --project src/Controls/samples/IosMenuBarSubmenuStateRetentionRepro/IosMenuBarSubmenuStateRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

Results are written to `/tmp/ios-menubar-submenu-state-retention-results.txt`.
