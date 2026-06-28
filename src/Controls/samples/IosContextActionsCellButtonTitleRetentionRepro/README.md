# iOS ContextActionsCell Button Title Retention Repro

This Mac Catalyst sample demonstrates that iOS compatibility `ContextActionsCell` leaves native context-action `UIButton` title slots assigned when the native action button peers survive through Objective-C reference counting.

The autorun scenario creates 2,048 real internal `ContextActionsCell.Update(...)` cycles with generated workflow `MenuItem.Text` values. It retains only native UIKit button peers with Objective-C `retain`, disposes the context-action cell, and lets the MAUI cell/menu-item graph collect. The control path explicitly clears native title and attributed-title slots before disposal.

Expected proof output:

```text
ContextActionsCell cycles per scenario: 2048
Payload per native action button title: 2 KiB
Leak proved: True
Control native buttons with assigned title: 0/2048
Current native buttons with assigned title: 2048/2048
Current estimated retained native title MiB: 4.0
Alive ContextActionsCells: 0/2048
Alive MAUI cells: 0/2048
Alive MenuItems: 0/2048
```

Run:

```bash
dotnet run --project src/Controls/samples/IosContextActionsCellButtonTitleRetentionRepro/IosContextActionsCellButtonTitleRetentionRepro.csproj \
  -f net10.0-maccatalyst \
  -p:UseMaui=false \
  -p:IncludeMacCatalystTargetFrameworks=true \
  -m:1 \
  -nr:false
```
