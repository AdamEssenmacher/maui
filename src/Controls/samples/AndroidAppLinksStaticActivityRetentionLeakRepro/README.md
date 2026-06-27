# AndroidAppLinksStaticActivityRetentionLeakRepro

This repro exercises Android compatibility AppLinks initialization.

`AndroidAppLinks.Init(Activity)` stores the supplied `Activity` in the static `AndroidAppLinks.Context` property and never clears it. If the activity is later destroyed or recreated, the static field can keep that old activity and its view/service graph alive for the rest of the process.

The control path clears the static AppLinks fields after initialization. The current path uses normal MAUI AppLinks initialization behavior.

Run on Android and read:

```sh
adb shell run-as com.microsoft.maui.androidapplinksstaticactivityretentionleakrepro cat files/autorun-results.txt
```
