# iOS compatibility Platform ActionSheet observer retention repro

This repro proves the obsolete iOS compatibility `Platform` action-sheet observer leak in
`src/Compatibility/Core/src/iOS/Platform.cs`.

On iPad, `PresentPopUp(...)` registers a `UIDevice.OrientationDidChangeNotification`
observer for compatibility `DisplayActionSheet` popovers and removes it only when
`ActionSheetArguments.Result` completes. If the native `UIAlertController` is dismissed
without a compatibility action callback completing the result, the observer remains
registered and captures the alert. The alert actions retain the compatibility `UIWindow`,
`ActionSheetArguments`, and generated action labels.

Run:

```bash
dotnet build src/Controls/samples/IosCompatActionSheetObserverRetentionRepro/IosCompatActionSheetObserverRetentionRepro.csproj -f net10.0-ios -p:UseMaui=false -p:IncludeIosTargetFrameworks=true -p:RuntimeIdentifier=iossimulator-arm64 -m:1 -nr:false -v:minimal -clp:Summary
```

Install and launch the built app on an iPad simulator with `--auto-run`. The app writes
the result to `Documents/ios-compat-actionsheet-observer-retention-results.txt` inside
the app container.

Verified on an iPad mini (A17 Pro) iOS 18.6 simulator:

- Completed-result control: `4/80` `ActionSheetArguments`, `1/80`
  `UIAlertController`, `4/80` compatibility `UIWindow`s, `4/80` button arrays,
  and `256.0 KiB` retained button-label payload.
- Current native-dismiss-without-result path: `80/80` `ActionSheetArguments`,
  `80/80` `UIAlertController`s, `80/80` compatibility `UIWindow`s, `80/80`
  button arrays, and `5.0 MiB` retained button-label payload.
- Managed heap delta: `5.5 MiB`.
