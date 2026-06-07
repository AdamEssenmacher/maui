# Issue #29255 KeepScrollOffset Android Repro

This branch overrides the Controls TestCases HostApp main page with a minimal `CollectionView` repro for the Android regression introduced by PR #29255.

## Scenario

The page performs this sequence automatically on load:

1. Set `CollectionView.ItemsUpdatingScrollMode = ItemsUpdatingScrollMode.KeepScrollOffset`.
2. Replace `ItemsSource` with a new observable collection containing `B item 01`, `B item 02`, etc.
3. Scroll to index `0` with `ScrollToPosition.Start` and `animate: false`.
4. Insert `NEW 1` at index `0`.

Expected behavior:

`NEW 1` is visible as the first row because the absolute scroll offset remains fixed.

Observed Android behavior with PR #29255 applied:

`B item 01` remains the first visible row and `NEW 1` is hidden above the viewport.

The same repro shows `NEW 1` visible on iOS and on Android before PR #29255.

## Build

Android:

```bash
dotnet build src/Controls/tests/TestCases.HostApp/Controls.TestCases.HostApp.csproj -f net10.0-android -c Debug -p:EmbedAssembliesIntoApk=true
```

iOS simulator:

```bash
dotnet build src/Controls/tests/TestCases.HostApp/Controls.TestCases.HostApp.csproj -f net10.0-ios -c Debug -p:RuntimeIdentifier=iossimulator-arm64 -p:CodesignRequireProvisioningProfile=false
```

## Repro Files

- `Issue29255E2EPage.cs` contains the minimal MAUI `CollectionView` repro.
- `MauiProgram.OverrideMainPage.cs` makes the HostApp launch directly to the repro page.
