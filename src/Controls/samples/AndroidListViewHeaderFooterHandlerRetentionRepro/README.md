# AndroidListViewHeaderFooterHandlerRetentionRepro

This repro exercises Android compatibility `ListViewRenderer` disconnect cleanup.

`ListViewRenderer.UpdateHeader()` and `UpdateFooter()` create native handlers for
visual header/footer views and store those handlers in the native
`ListViewRenderer.Container.Child` slot. The normal disconnect path can clear
the renderer, adapter, scroll listener, and refresh listener while the native
header/footer containers still point at the header/footer handlers. If those
native containers remain rooted by Android `ListView` header/footer storage, each
handler can retain the header/footer view and its binding-context payload.

The control path explicitly clears the native header/footer container children
before disconnect. The current path uses the normal disconnect behavior after
neutralizing the known adapter, scroll-listener, and refresh-listener roots in
both runs. Each run creates 80 `ListView` instances with a 512 KiB payload on the
header and another 512 KiB payload on the footer, so full retention in the
current path demonstrates roughly 80 MiB of retained managed payload.

Run on Android and read:

```sh
adb shell run-as com.microsoft.maui.androidlistviewheaderfooterhandlerretentionrepro cat files/autorun-results.txt
```
