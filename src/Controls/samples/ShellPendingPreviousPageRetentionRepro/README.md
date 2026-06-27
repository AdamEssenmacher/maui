# Shell pending previous-page retention repro

This Mac Catalyst repro targets the deferred `Shell.NavigatedTo` path added for `dotnet/maui#29428` / `dotnet/maui#30757`.

When `Shell.SendNavigated()` reaches a destination page before that page has fired `Loaded`, `PropagateSendNavigatedTo()` stores the previous page in `_pendingPreviousPage` and subscribes to the destination page's `Loaded` event. If another navigation starts before that destination fires `Loaded`, `SendNavigating()` removes the `Loaded` handler but does not clear `_pendingPreviousPage`.

The sample creates that state deterministically, then compares:

- control: clear `_pendingPreviousPage` when navigating away before `Loaded`
- current: run the current `SendNavigating()` cleanup path and leave `_pendingPreviousPage` assigned

The previous page owns a 48 MiB payload through its binding context. The result file is written to `Path.GetTempPath()/shell-pending-previous-page-retention-results.txt`.
