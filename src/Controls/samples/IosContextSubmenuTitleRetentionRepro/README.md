# iOS Context Submenu UIMenu Title Retention Repro

This Mac Catalyst repro exercises the iOS/Mac Catalyst context `MenuFlyoutSubItemHandler` path. Context subitems create native `UIMenu` instances with `title: VirtualView.Text`.

The autorun scenario creates 256 context-menu rebuilds with 8 generated submenu labels per menu. The current MAUI path uses 8 KiB submenu labels, for 2,048 retained native submenu title slots. Because `UIMenu.Title` is read-only in the binding, the control path retains the same native submenu graph shape with short realistic titles; the current path demonstrates the retained payload-sized submenu titles after the flyout, subitems, and child items collect.

Run:

```bash
dotnet run --project src/Controls/samples/IosContextSubmenuTitleRetentionRepro/IosContextSubmenuTitleRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

Results are written to `/tmp/ios-context-submenu-title-retention-results.txt`.
