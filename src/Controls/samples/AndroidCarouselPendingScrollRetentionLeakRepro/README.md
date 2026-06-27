# Android CarouselView Pending Scroll Retention Repro

This sample proves that Android `MauiCarouselRecyclerView` can retain missing item payloads through `CarouselViewLoopManager._pendingScrollTo`.

The repro keeps `80` loop-manager instances alive. Each one receives a looped `CarouselView.ScrollTo(item, ...)` request for an item that is no longer in the carousel source before the carousel has initialized its first layout. The control path clears the loop manager pending-scroll queue after also clearing `_itemsSource`; the current path only clears `_itemsSource`.

Each missing item carries a touched `1 MiB` payload buffer, so full retention proves `80 MiB` of managed payload held only by queued `ScrollToRequestEventArgs` instances.
