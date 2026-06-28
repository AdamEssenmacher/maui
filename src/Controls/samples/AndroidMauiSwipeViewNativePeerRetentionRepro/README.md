# Android MauiSwipeView Native Peer Retention Repro

This sample proves that Android `MauiSwipeView` can keep materialized swipe item state alive after handler disconnect. `MauiSwipeView.UpdateSwipeItems()` stores virtual `ISwipeItem` keys in the strong `_swipeItems` dictionary, and `DisposeSwipeItems()` clears that dictionary only on reset/close paths, not during `SwipeViewHandler` disconnect.

The repro opens 80 swipe rows, detaches the virtual content and `RightItems`, disconnects their handlers, and keeps only the Android native `MauiSwipeView` peers alive. Both scenarios clear the already-known owner fields from C129 (`CrossPlatformLayout`, `Element`, native children, and content fields), so retained payloads come from materialized platform swipe state rather than from the `SwipeView` owner field or content handler graph. The control also clears platform swipe state; current MAUI leaves it assigned. Each swipe item carries a 1 MiB command-parameter payload.

Run:

```sh
dotnet build src/Controls/samples/AndroidMauiSwipeViewNativePeerRetentionRepro/AndroidMauiSwipeViewNativePeerRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:IncludeAndroidTargetFrameworks=true -p:EmbedAssembliesIntoApk=true -m:1 -nr:false -t:SignAndroidPackage
```

Install and launch the signed APK, then read the app-private `files/autorun-results.txt`.
