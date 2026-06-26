# AndroidToolbarHandlerNavigationListenerLeakRepro

This repro exercises the Android `ToolbarHandler` navigation-click listener cleanup path.

The control path explicitly clears the native `MaterialToolbar` navigation click listener and the handler's retained drawer field before disconnecting. The current path disconnects the handler without clearing `SetNavigationOnClickListener`, leaving retained native toolbars able to root the old handler and drawer view tree.

Run on Android and read:

```sh
adb shell run-as com.microsoft.maui.androidtoolbarhandlernavigationlistenerleakrepro cat files/autorun-results.txt
```
