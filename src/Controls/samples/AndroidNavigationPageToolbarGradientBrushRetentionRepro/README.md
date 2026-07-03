# Android NavigationPage Toolbar GradientBrush Retention Repro

This repro demonstrates that an Android `NavigationPage` whose `BarBackground` is a long-lived shared `GradientBrush` can retain removed navigation pages through the internal `NavigationPageToolbar`.

`Toolbar.Android.UpdateBarBackground()` subscribes `NavigationPageToolbar.OnBarBackgroundChanged` to the shared brush and assigns the brush parent. `NavigationPageToolbar.Disconnect()` clears toolbar tracker/event state when the `NavigationPage` leaves the window, but it does not clear `Toolbar._currentBarBackground` or detach the brush event. A shared app/resource brush can therefore keep each removed toolbar alive, and the toolbar keeps the old `NavigationPage`, root page, and page view model payload graph alive.

The app autoruns two scenarios:

- Control: clear the internal toolbar `BarBackground` before replacing `Window.Page`.
- Current: replace `Window.Page` without clearing the shared brush subscription.

Each attempt uses a realistic 1 MiB page view-model payload. A proven run retains `0/64` payloads in the control path and `64/64` payloads in current MAUI.
