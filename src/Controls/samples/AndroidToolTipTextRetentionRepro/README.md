# Android tooltip text retention repro

This repro demonstrates that retained Android native view peers keep `ToolTipProperties.Text` assigned after MAUI handler disconnect.

It compares the current MAUI disconnect behavior against a control run that explicitly clears native tooltip text with `TooltipCompat.SetTooltipText(view, null)` before disconnecting the handlers. The sample autoruns on launch, writes the result to `autorun-results.txt`, and exits.

Build:

```bash
dotnet build src/Controls/samples/AndroidToolTipTextRetentionRepro/AndroidToolTipTextRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:EmbedAssembliesIntoApk=true -p:AndroidFastDeploymentType= -m:1 -nr:false -v:minimal -clp:Summary
```

Run:

```bash
adb install --no-incremental -r artifacts/bin/AndroidToolTipTextRetentionRepro/Debug/net10.0-android/com.microsoft.maui.androidtooltiptextretentionrepro-Signed.apk
adb shell am start -S -n com.microsoft.maui.androidtooltiptextretentionrepro/crc64e124a72a7b2e8db5.MainActivity
adb shell run-as com.microsoft.maui.androidtooltiptextretentionrepro cat files/autorun-results.txt
```
