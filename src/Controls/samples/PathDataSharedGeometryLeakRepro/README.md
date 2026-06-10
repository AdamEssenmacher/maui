# PathDataSharedGeometryLeakRepro

This sample demonstrates the direct shared `Path.Data` and `Path.RenderTransform` leak path:

```text
Application.Resources shared PathGeometry / ScaleTransform
  -> Path.Data / Path.RenderTransform strong event subscription
  -> Path
  -> dashboard page / BindingContext payload
```

The default autorun uses realistic dashboard values:

- 50 pushed and popped pages
- 24 paths per page
- 4 MB cached payload per page
- about 200 MB of direct payload if all dashboard view models are retained

The three scenarios are:

- Control: every `Path` gets fresh page-local `PathGeometry` and `ScaleTransform` instances.
- Leaky: every `Path` uses the same app-level `PathGeometry` and `ScaleTransform` resources.
- Mitigation: the same shared resources are used, then `Path.Data` and `Path.RenderTransform` are cleared in `OnDisappearing`.

## iOS Simulator

```sh
dotnet build Microsoft.Maui.BuildTasks.slnf
dotnet build src/Controls/samples/PathDataSharedGeometryLeakRepro/PathDataSharedGeometryLeakRepro.csproj -f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64
xcrun simctl install 4668BAB0-590F-491C-8EF7-41B8044F15A7 artifacts/bin/PathDataSharedGeometryLeakRepro/Debug/net10.0-ios/iossimulator-arm64/PathDataSharedGeometryLeakRepro.app
xcrun simctl launch --terminate-running-process 4668BAB0-590F-491C-8EF7-41B8044F15A7 com.microsoft.maui.pathdatasharedgeometryleakrepro --auto-run --target=PathDataSharedGeometry
APP_CONTAINER=$(xcrun simctl get_app_container 4668BAB0-590F-491C-8EF7-41B8044F15A7 com.microsoft.maui.pathdatasharedgeometryleakrepro data)
cat "$APP_CONTAINER/Library/PathDataSharedGeometryLeakRepro/autorun-results.txt"
```
