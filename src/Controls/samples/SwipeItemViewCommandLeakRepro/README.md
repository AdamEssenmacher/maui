# SwipeItemView Command Leak Repro

This sample demonstrates a retention leak caused by `SwipeItemView.Command` subscribing directly to a long-lived `ICommand.CanExecuteChanged` event.

This is intentionally separate from the tracked `SwipeItems` replacement/reuse issue. This repro does not depend on reusing or replacing a `SwipeItems` collection. Each page creates fresh `SwipeView`, `SwipeItems`, and `SwipeItemView` instances, then navigates away. The retained root is:

```text
shared ICommand
  -> CanExecuteChanged
  -> SwipeItemView.OnCommandCanExecuteChanged
  -> SwipeItemView
  -> CommandParameter / Content / BindingContext
  -> page row state and payload
```

## Why this shape is realistic

Many apps keep workflow commands in a shell, service, toolbar model, or app-level view model. A swipe row can naturally pass the row view model as the command parameter. If a page contains customer/order/route rows with non-trivial state, each leaked `SwipeItemView` can retain the page-local row object and its object graph after the page is popped.

The default run uses:

- 25 pushed pages
- 40 swipe rows per page
- 128 KB of page-local row payload per row
- 1,000 total rows
- 125 MB of payload pressure

Those values are deliberately ordinary for an operational app with repeated list navigation, cached row details, thumbnails, decoded metadata, or offline sync state.

## Running

From the repo root:

```bash
dotnet run --project src/Controls/samples/SwipeItemViewCommandLeakRepro/SwipeItemViewCommandLeakRepro.csproj -f net10.0-maccatalyst
```

or run the Android/iOS targets:

```bash
dotnet run --project src/Controls/samples/SwipeItemViewCommandLeakRepro/SwipeItemViewCommandLeakRepro.csproj -f net10.0-android
dotnet run --project src/Controls/samples/SwipeItemViewCommandLeakRepro/SwipeItemViewCommandLeakRepro.csproj -f net10.0-ios
```

## Expected result

Run `Run leaky SwipeItemView` with the defaults. After the navigation loop completes and the sample forces full collections, the final output should show roughly:

- `Command subscribers`: 1,000
- `Alive swipe action elements`: 1,000
- `Alive row view models`: 1,000
- `Retained row payload`: 125 MB

Then run `Run control SwipeItem`. It uses the same long-lived command and row command parameter, but uses plain `SwipeItem.Command`. The sample raises `CanExecuteChanged` while sampling so weak command subscriptions can clean themselves up. After forced collection, the retained counts should fall near zero.

Then run `Run mitigation`. It uses `SwipeItemView.Command`, but clears `Command`, `CommandParameter`, `Content`, and `BindingContext` from each `SwipeItemView` during page disappearance. After forced collection, the retained counts should also fall near zero.

The difference between the leaky run and the two controls proves that the retained root is the direct `SwipeItemView.Command` event subscription.
