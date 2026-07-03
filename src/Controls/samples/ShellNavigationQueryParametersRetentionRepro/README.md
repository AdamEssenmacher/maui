# ShellNavigationQueryParameters Retention Repro

This Mac Catalyst repro checks whether `ShellNavigationQueryParameters` payloads are cleared after delivery for every Shell route page type.

The app runs two scenarios. Both push 96 routed pages and keep those pages alive in the Shell navigation stack. Each navigation passes a new 1 MiB object through `Shell.GoToAsync(..., ShellNavigationQueryParameters)`, and each target page records only scalar facts about the payload. The control route uses a `ContentPage`, which triggers `ShellRouteParameters.ResetToQueryParameters()`. The current-MAUI route uses a `TabbedPage`, which is a real renderable `Page` type but does not pass the `content is ContentPage` reset gate in `ShellContent.ApplyQueryAttributes`.

A leak is proven when the `ContentPage` route pages remain alive while their single-use payloads collect, but the `TabbedPage` route pages retain most payloads through the attached Shell query dictionary. This is a follow-up variant of dotnet/maui#10294: the fixed single-use parameter path still depends on the target page being a `ContentPage`.

Run:

```bash
dotnet build src/Controls/samples/ShellNavigationQueryParametersRetentionRepro/ShellNavigationQueryParametersRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -p:EnableMauiAssetProcessing=false -p:EnableMauiImageProcessing=false -p:EnableMauiSplashScreenProcessing=false -m:1 -nr:false -v:minimal
open -W "artifacts/bin/ShellNavigationQueryParametersRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/Shell Query Parameters Retention.app" --args --results=/tmp/shell-queryparameters-retention-results.txt
```
