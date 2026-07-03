# IosTabletFlyoutPageCachedFlyoutRetentionRepro

This Mac Catalyst repro checks whether the legacy iOS `TabletFlyoutPageRenderer`
retains disposed `FlyoutPage` graphs through its private cached `_flyoutPage` field.

The control scenario disposes each renderer and clears only `_flyoutPage`. The current
MAUI scenario disposes each renderer but leaves `_flyoutPage` assigned. Both scenarios
retain the same number of disposed renderer peers so the only intentional difference is
the stale cached field.

Run from the repository root:

```bash
dotnet build src/Controls/samples/IosTabletFlyoutPageCachedFlyoutRetentionRepro/IosTabletFlyoutPageCachedFlyoutRetentionRepro.csproj -c Release
open -W artifacts/bin/IosTabletFlyoutPageCachedFlyoutRetentionRepro/Release/net10.0-maccatalyst/maccatalyst-arm64/IosTabletFlyoutPageCachedFlyoutRetentionRepro.app
cat /tmp/ios-tabletflyoutpage-cached-flyout-retention-results.txt
```

Observed result:

```text
RESULT: PROVEN
cycles=80
payloadMegabytesPerCycle=1
scenario=control: dispose renderer and clear cached _flyoutPage
  renderersWithCachedFlyoutPage=0/80
  aliveFlyoutPages=0/80
  alivePayloads=0/80
  retainedPayloadMiB=0.0
scenario=current: dispose renderer with cached _flyoutPage still assigned
  renderersWithCachedFlyoutPage=80/80
  aliveFlyoutPages=80/80
  alivePayloads=80/80
  retainedPayloadMiB=80.0
```
