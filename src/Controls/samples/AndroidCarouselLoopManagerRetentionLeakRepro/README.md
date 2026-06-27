# AndroidCarouselLoopManagerRetentionLeakRepro

This repro proves that Android `MauiCarouselRecyclerView` can retain disconnected `CarouselView` item-source graphs through `CarouselViewLoopManager`.

`MauiCarouselRecyclerView.TearDownOldElement()` clears the base recycler state but does not clear `_carouselViewLoopManager`. The loop manager keeps the current `IItemsViewSource`, and observable item sources keep the old `CarouselView` as their container. A retained native recycler view can therefore keep the old `CarouselView` and its binding payload alive after handler disconnect.
