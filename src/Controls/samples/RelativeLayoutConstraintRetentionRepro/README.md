# RelativeLayout Constraint Retention Repro

This repro checks whether removed compatibility `RelativeLayout` child views retain discarded layout and sibling graphs through attached constraint state.

The scenario keeps only removed child views in an app cache. Each removed child was positioned relative to an anchor sibling in a discarded `RelativeLayout`. Current MAUI removes the children from the layout but leaves the removed child carrying `BoundsConstraint`, `XConstraint`, `YConstraint`, `WidthConstraint`, and `HeightConstraint` attached properties. Those objects contain compiled delegates that capture the old `RelativeLayout` and anchor sibling.

The control run clears those attached properties after removal. Both runs retain the same removed child count.

Run:

```bash
dotnet build src/Controls/samples/RelativeLayoutConstraintRetentionRepro/RelativeLayoutConstraintRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
dotnet run --project src/Controls/samples/RelativeLayoutConstraintRetentionRepro/RelativeLayoutConstraintRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false --no-build
cat /tmp/relativelayout-constraint-retention-results.txt
```

Proof from the Mac Catalyst run on 2026-07-02:

- control retained `0/80` discarded layouts, `0/80` anchor siblings, and `0/160` payload buffers
- current cleanup retained `80/80` discarded layouts, `80/80` anchor siblings, and `160/160` 1 MiB payload buffers
- current retained payload bytes: `167,772,160`

This is distinct from C307: C307 retains discarded compatibility layout owners through app-kept public `Children` wrappers. This repro keeps only removed child views.
