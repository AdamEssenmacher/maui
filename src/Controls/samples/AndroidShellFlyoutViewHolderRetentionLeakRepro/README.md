# AndroidShellFlyoutViewHolderRetentionLeakRepro

This repro exercises the Android Shell flyout `ShellFlyoutRecyclerAdapter.ElementViewHolder` cleanup path.

The control path explicitly clears the view holder `Element` and disposes the holder. The current path models adapter disposal without clearing active holders, leaving retained Shell items subscribed to holder callbacks and logically parenting the flyout item template view.

Run on Android and read:

```sh
adb shell run-as com.microsoft.maui.androidshellflyoutviewholderretentionleakrepro cat files/autorun-results.txt
```
