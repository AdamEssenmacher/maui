# Android FlyoutView Native Content Retention Repro

This repro isolates current Android `FlyoutViewHandler` disconnect cleanup. The handler installs flyout native children into a retained `DrawerLayout`, but disconnect cleanup only removes listeners and toolbar state. It does not remove the current flyout/native child graph from the drawer.

The app creates 96 `FlyoutPage` handlers with payload-bearing flyout `BindingContext`s, retains only the native `DrawerLayout` peers with JNI global refs, disconnects the handlers, and clears known non-candidate pending/scoped fragment fields in both scenarios. The repro installs the flyout native child graph directly into the handler's drawer to avoid unrelated synthetic detail-fragment attachment failures; disconnect cleanup remains the real `FlyoutViewHandler.DisconnectHandler()` path. The control additionally removes/disposes the drawer child graph and clears the private flyout/native child fields. If current MAUI retains drawer children and flyout content pages while the parent `FlyoutPage` and `FlyoutViewHandler` collect, the leak is proved.

The default run uses a 512 KiB BindingContext payload per flyout content page, which proves a 48 MiB payload delta while keeping the 1 GiB low-RAM Android emulator responsive.

Run:

```bash
dotnet build src/Controls/samples/AndroidFlyoutViewNativeContentRetentionRepro/AndroidFlyoutViewNativeContentRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:IncludeAndroidTargetFrameworks=true -p:EmbedAssembliesIntoApk=true -m:1 -nr:false -t:SignAndroidPackage -v:minimal -clp:Summary
```

Then install and launch the signed APK. Results are written to `files/autorun-results.txt` inside the app sandbox.
