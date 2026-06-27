# AndroidToolbarMenuItemListenerRetentionLeakRepro

This repro exercises Android toolbar menu item listener cleanup.

The control path clears native `IMenuItem` click listeners and menu entries before disconnecting the toolbar handler. The current path disconnects the handler without clearing the native toolbar menu. If the native `MaterialToolbar` remains rooted, its menu items keep `GenericMenuClickListener` instances that strongly capture the original `ToolbarItem` activation delegate and payload command.

Run on Android and read:

```sh
adb shell run-as com.microsoft.maui.androidtoolbarmenuitemlistenerretentionleakrepro cat files/autorun-results.txt
```
