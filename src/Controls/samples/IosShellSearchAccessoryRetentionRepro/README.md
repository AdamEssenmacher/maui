# iOS Shell SearchHandler Accessory Retention Repro

This repro exercises the compatibility iOS `SearchHandlerAppearanceTracker` numeric-keyboard accessory path.

`SearchHandlerAppearanceTracker.UpdateKeyboard()` assigns a `UIToolbar` with a `UIBarButtonItem` callback to `UISearchBar.InputAccessoryView` for phone numeric and telephone keyboards. `Dispose()` removes search-handler and search-bar events, but it does not clear the retained accessory view or toolbar item. If native `UISearchBar` peers survive detach, the accessory toolbar can keep the disposed tracker alive; the tracker keeps its `IFontManager`, and MAUI's default iOS `FontManager` stores the app `IServiceProvider`.

The app runs a control scenario that clears `InputAccessoryView` and toolbar items before disposal, then a current-MAUI scenario that leaves them assigned. It writes the autorun report to the app documents directory and stdout.
