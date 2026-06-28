# iOS Navigation Title Icon Image Retention Repro

This sample proves that iOS/Mac Catalyst compatibility `NavigationRenderer` can leave `NavigationPage.TitleIconImageSource` images assigned on retained native title containers. The repro creates page title containers through the renderer's normal title-icon path, retains only the native title container, and then runs the same non-disposing title-container disconnect path used for popped pages.

The control run clears the nested `UIImageView.Image` before disconnect. The current run leaves the assigned title icon image in place.

Run:

```sh
dotnet run --project src/Controls/samples/IosNavigationTitleIconImageRetentionRepro/IosNavigationTitleIconImageRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes the result to `/tmp/ios-navigation-titleicon-image-retention-results.txt`.
