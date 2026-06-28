# iOS SwipeItem Title Retention Repro

This Mac Catalyst sample demonstrates that current iOS `SwipeItemMenuItemHandler` leaves native `UIButton` text slots assigned when native swipe action button peers survive through Objective-C reference counting.

The autorun scenario creates 96 real `SwipeItemMenuItemHandler` cycles with generated 256 KiB workflow action labels. It retains only native `UIButton` peers with Objective-C `retain`, disconnects the handler, clears the managed `SwipeItem.Text`, and lets the `SwipeItem`/handler graph collect. The control path explicitly clears native title/attributed-title/title-label state and `RestorationIdentifier`.

Expected proof output:

```text
SwipeItem cycles per scenario: 96
Payload per native swipe item title: 256 KiB
Leak proved: True
Control native buttons with assigned text: 0/96
Current native buttons with assigned text: 96/96
Current estimated retained native text MiB: 24.0
Alive SwipeItems: 0/96
Alive handlers: 0/96
```

Run:

```bash
dotnet run --project src/Controls/samples/IosSwipeItemTitleRetentionRepro/IosSwipeItemTitleRetentionRepro.csproj \
  -f net10.0-maccatalyst \
  -p:UseMaui=false \
  -p:IncludeMacCatalystTargetFrameworks=true \
  -m:1 \
  -nr:false
```
