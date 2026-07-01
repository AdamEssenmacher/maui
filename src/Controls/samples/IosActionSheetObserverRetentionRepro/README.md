# iOS ActionSheet Observer Retention Repro

This sample checks the iPad `DisplayActionSheet` popover orientation observer path.

`AlertManager.iOS.PresentPopUp(...)` registers a `UIDevice.OrientationDidChangeNotification` observer for iPad action sheets and removes that observer only from the `ActionSheetArguments.Result.Task` continuation. The repro compares:

- a control path that completes `ActionSheetArguments.Result` before dismissing the native action sheet
- the current native-dismiss path where the `UIAlertController` is dismissed without completing the MAUI result task

The current path should leave the notification observer registered. That observer captures the `UIAlertController`, which keeps its actions and generated button labels alive.
