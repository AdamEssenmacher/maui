# Android WebView RefreshView Repro

This standalone MAUI app demonstrates the Android regression where a downward pull that starts inside an internally scrollable element in a `WebView` incorrectly triggers the parent `RefreshView`.

## Build

From the MAUI repository root:

```bash
./build.sh -restore
./.dotnet/dotnet build Microsoft.Maui.BuildTasks.slnf
./.dotnet/dotnet build repros/AndroidWebViewRefreshRepro/AndroidWebViewRefreshRepro.csproj -f net10.0-android
```

## Run

```bash
./.dotnet/dotnet build repros/AndroidWebViewRefreshRepro/AndroidWebViewRefreshRepro.csproj -f net10.0-android -t:Run
```

## Repro Steps

1. Launch the app on an Android emulator or device.
2. Confirm the label reads `Refresh count: 0`.
3. Pull down starting inside the WebView's inner scroll list.
4. Expected behavior: the inner list scrolls upward and the label remains `Refresh count: 0`.
5. Regressed behavior on `inflight/current`: the parent `RefreshView` refreshes and the label changes to `Refresh count: 1`.

Use `Reset` to set the counter back to `0` and restore the inner list to its pre-scrolled state.
