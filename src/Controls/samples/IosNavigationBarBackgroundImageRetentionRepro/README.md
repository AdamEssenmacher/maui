# iOS Navigation Bar Background Image Retention Repro

This sample demonstrates that iOS/Mac Catalyst `NavigationRenderer` leaves the
generated native navigation bar background image assigned after teardown when
`NavigationPage.BarBackground` is a brush.

The control run forces the same `UpdateBarBackground()` path, then clears the
`UINavigationBar` background image slots before retaining the native navigation
bar. The current MAUI run tears down the compatibility renderer but leaves the
native `UINavigationBarAppearance.BackgroundImage` values assigned.

Run:

```bash
dotnet run --project src/Controls/samples/IosNavigationBarBackgroundImageRetentionRepro/IosNavigationBarBackgroundImageRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The result is written to `/tmp/ios-navigationbar-background-image-retention-results.txt`.
