# iOS ToolbarItem Title Retention Repro

This Mac Catalyst sample demonstrates that iOS toolbar item conversion leaves native title/text slots assigned when native `UIBarButtonItem` or `UIAction` peers survive through Objective-C reference counting.

The autorun scenario creates 1,024 cycles using real MAUI toolbar conversion helpers:

- primary `ToolbarItem` text stored in `UIBarButtonItem.Title`
- secondary custom-view `ToolbarItem` text stored in a nested `UILabel.Text`
- secondary overflow `ToolbarItem` text stored in `UIAction.Title`

Each title uses a generated 2 KiB workflow/action label. The repro retains only native UIKit peers with Objective-C `retain`, disposes the managed wrappers where applicable, and lets the MAUI `ToolbarItem` graph collect. The control path explicitly clears native title/text slots before disposal.

Expected proof output:

```text
ToolbarItem cycles per scenario: 1024
Payload per native title slot: 2 KiB
Native title slots per cycle: 3
Leak proved: True
Control native peers with assigned titles: 0/3072
Current native peers with assigned titles: 3072/3072
Current estimated retained native title MiB: 6.0
Alive ToolbarItems: 0/3072
```

Run:

```bash
dotnet run --project src/Controls/samples/IosToolbarItemTitleRetentionRepro/IosToolbarItemTitleRetentionRepro.csproj \
  -f net10.0-maccatalyst \
  -p:UseMaui=false \
  -p:IncludeMacCatalystTargetFrameworks=true \
  -m:1 \
  -nr:false
```
