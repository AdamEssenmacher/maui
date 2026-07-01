# Android ScrollView Native Content Retention Repro

This repro isolates current Android `ScrollViewHandler.DisconnectHandler()` cleanup. The handler unsubscribes `ScrollChange`, but it does not remove the current inset `ContentViewGroup` or clear `MauiScrollView._content`.

The app creates 96 `ScrollView` handlers with payload-bearing `Label` content, retains only the native `MauiScrollView` peers with JNI global refs, disconnects the handlers, and clears known non-candidate owner fields in both scenarios. The control additionally removes the native content panel and clears the private native content slot. If current MAUI retains the native content panel, copied label text, and child content graph while the parent `ScrollView` and parent `ScrollViewHandler` collect, the leak is proved.

The default run uses 128 Ki UTF-16 characters per `Label`, which proves a 24 MiB payload delta while keeping the 1 GiB low-RAM Android emulator responsive.

Run:

```bash
dotnet build src/Controls/samples/AndroidScrollViewNativeContentRetentionRepro/AndroidScrollViewNativeContentRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:IncludeAndroidTargetFrameworks=true -p:EmbedAssembliesIntoApk=true -m:1 -nr:false -t:SignAndroidPackage -v:minimal -clp:Summary
```

Then install and launch the signed APK. Results are written to `files/autorun-results.txt` inside the app sandbox.
