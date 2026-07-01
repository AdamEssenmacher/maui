# Android SwipeView Native Content Retention Repro

This repro isolates current Android `SwipeViewHandler` disconnect cleanup. The handler has no Android disconnect override, so it does not remove the current native content view or clear `MauiSwipeView._contentView`.

The app creates 96 `SwipeView` handlers with payload-bearing `Label` content, retains only the native `MauiSwipeView` peers with JNI global refs, disconnects the handlers, and clears known non-candidate owner fields in both scenarios. The control additionally removes/disposes the native content view and clears the private native content field. If current MAUI retains `_contentView`, copied label text, and the child content graph while the parent `SwipeView` and parent `SwipeViewHandler` collect, the leak is proved.

The default run uses 128 Ki UTF-16 characters per `Label`, which proves a 24 MiB payload delta while keeping the 1 GiB low-RAM Android emulator responsive.

Run:

```bash
dotnet build src/Controls/samples/AndroidSwipeViewNativeContentRetentionRepro/AndroidSwipeViewNativeContentRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:IncludeAndroidTargetFrameworks=true -p:EmbedAssembliesIntoApk=true -m:1 -nr:false -t:SignAndroidPackage -v:minimal -clp:Summary
```

Then install and launch the signed APK. Results are written to `files/autorun-results.txt` inside the app sandbox.
