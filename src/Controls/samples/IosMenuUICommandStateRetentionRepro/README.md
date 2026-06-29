# iOS Menu UICommand State Retention Repro

This Mac Catalyst repro exercises the iOS/Mac Catalyst non-context `MenuFlyoutItemHandler` path. Non-context menu flyout items create native `UICommand` and `UIKeyCommand` instances through `KeyboardAcceleratorExtensions.CreateMenuItem(...)`, copying `MenuFlyoutItem.Text` and `IconImageSource` into immutable native command title and image state.

The autorun scenario creates 128 cycles with 8 native commands per cycle, alternating ordinary `UICommand` and keyboard-accelerated `UIKeyCommand` entries. The current path uses 8 KiB generated workflow labels and 192 x 192 generated PNG icons. The control path retains the same number of native commands with short labels and no images. Both paths clear the static `MenuFlyoutItemHandler.menus` dictionary after every cycle so the older static-menu leak is not part of this proof.

Run:

```bash
dotnet run --project src/Controls/samples/IosMenuUICommandStateRetentionRepro/IosMenuUICommandStateRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

Results are written to `/tmp/ios-menu-uicommand-state-retention-results.txt`.
