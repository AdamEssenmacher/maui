# Android SearchBar posted selection retention repro

This repro checks whether retained detached Android `SearchView` peers keep `SearchBarHandler` and its `MauiContext` alive through the deferred `QueryEditor.Post(...)` callback scheduled by `SearchBarHandler.OnQueryEditorSelectionChanged()`.

It compares current MAUI against a control run that creates and disconnects the same handler/native peer shape, but assigns the native query only after handler disconnect so the handler-capturing selection callback is not queued while connected. The sample autoruns on launch, writes `autorun-results.txt`, and exits.

Build:

```bash
dotnet build src/Controls/samples/AndroidSearchBarPostedSelectionRetentionRepro/AndroidSearchBarPostedSelectionRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:EmbedAssembliesIntoApk=true -p:AndroidFastDeploymentType= -m:1 -nr:false -v:minimal -clp:Summary
```

Run:

```bash
adb install --no-incremental -r artifacts/bin/AndroidSearchBarPostedSelectionRetentionRepro/Debug/net10.0-android/com.microsoft.maui.androidsearchbarpostedselectionretentionrepro-Signed.apk
adb shell am start -S -n com.microsoft.maui.androidsearchbarpostedselectionretentionrepro/crc644f4ae9743eaa95cd.MainActivity
adb shell run-as com.microsoft.maui.androidsearchbarpostedselectionretentionrepro cat files/autorun-results.txt
```
