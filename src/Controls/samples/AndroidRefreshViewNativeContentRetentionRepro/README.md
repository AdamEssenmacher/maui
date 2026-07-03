# Android RefreshView Native Content Retention Repro

This repro tests whether `RefreshViewHandler.DisconnectHandler()` clears the current native content field on Android.

The control scenario retains the same native `MauiSwipeRefreshLayout` peers through JNI global refs but explicitly removes the current content and clears private `_contentView` after normal handler disconnect. The current MAUI scenario performs only the normal disconnect path. Each `RefreshView.Content` child is a `Label` with a generated 256 KiB text payload, making retained native `TextView` text measurable.

Run:

```bash
dotnet build src/Controls/samples/AndroidRefreshViewNativeContentRetentionRepro/AndroidRefreshViewNativeContentRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:IncludeAndroidTargetFrameworks=true -p:EmbedAssembliesIntoApk=true
```

Install and launch the APK, then read:

```text
/data/data/com.microsoft.maui.androidrefreshviewnativecontentretentionrepro/files/autorun-results.txt
```
