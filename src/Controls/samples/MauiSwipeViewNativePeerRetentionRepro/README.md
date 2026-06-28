# MauiSwipeView Native Peer Retention Repro

This sample proves that a retained iOS/Mac Catalyst `MauiSwipeView` native peer can keep materialized swipe item state alive after handler disconnect. When `_swipeItems` is not cleared, the native peer retains the virtual `SwipeItem` keys and their command-parameter payloads.

Run:

```sh
dotnet run --project src/Controls/samples/MauiSwipeViewNativePeerRetentionRepro/MauiSwipeViewNativePeerRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes the result to the temp file shown by `ReproSession.ResultsPath`.
