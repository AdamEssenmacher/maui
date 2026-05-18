# BorderDashArrayLeakRepro

This repro targets the `Border.StrokeDashArray` retention path on Android, iOS, and Mac Catalyst.
`Border.StrokeDashPattern` subscribes to the current `DoubleCollection.CollectionChanged` when the
platform handler reads the dash pattern, but the subscription is not torn down when the `Border` is
discarded. If the dash array is an app resource, that resource can retain every realized dashed
`Border`.

The sample uses a realistic CollectionView card page:

- A single `DoubleCollection` lives in `Application.Resources`.
- Each leaky card assigns that shared resource to `Border.StrokeDashArray`.
- Each card has a normal page-level tap handler, as XAML event handlers commonly do.
- Retaining one card `Border` is therefore enough to retain the page, its `CollectionView`, the page
  view model, and all item view models for that page.

## Run

Build from the repo root:

```bash
dotnet build Microsoft.Maui.BuildTasks.slnf
dotnet build src/Controls/samples/BorderDashArrayLeakRepro/BorderDashArrayLeakRepro.csproj -f net10.0-android
dotnet build src/Controls/samples/BorderDashArrayLeakRepro/BorderDashArrayLeakRepro.csproj -f net10.0-maccatalyst
dotnet build src/Controls/samples/BorderDashArrayLeakRepro/BorderDashArrayLeakRepro.csproj -f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64
```

Run Mac Catalyst locally:

```bash
dotnet run --project src/Controls/samples/BorderDashArrayLeakRepro/BorderDashArrayLeakRepro.csproj -f net10.0-maccatalyst
```

Run the automated device proof on Mac Catalyst:

```bash
BORDER_DASH_PROOF=1 \
BORDER_DASH_PROOF_OUTPUT=/tmp/border-dash-array-device-proof.txt \
dotnet run --project src/Controls/samples/BorderDashArrayLeakRepro/BorderDashArrayLeakRepro.csproj -f net10.0-maccatalyst
```

Run the automated device proof on an installed iOS simulator app:

```bash
xcrun simctl launch --terminate-running-process booted com.microsoft.maui.borderdasharrayleakrepro --device-proof
find "$(xcrun simctl get_app_container booted com.microsoft.maui.borderdasharrayleakrepro data)" -name border-dash-array-device-proof.txt -print -exec cat {} \;
```

Run the automated device proof on an installed Android app:

```bash
adb shell am start -S -n com.microsoft.maui.borderdasharrayleakrepro/com.microsoft.maui.borderdasharrayleakrepro.MainActivity --ez BORDER_DASH_PROOF true
adb shell run-as com.microsoft.maui.borderdasharrayleakrepro cat files/border-dash-array-device-proof.txt
```

The proof writes `border-dash-array-device-proof.txt` under the app data directory.

## What to Check

Use the default settings first:

- Pages/run: `20`
- Cards/page: `64`
- Item payload KB/card: `96`
- Page payload MB/page: `3`
- Dwell ms/page: `100`

These defaults model a business dashboard with cached page state and medium card payloads. They
allocate about `9 MB` of managed payload per page, or about `180 MB` over the default run.

Run these scenarios:

1. `Run control`
   - Uses the same pages, CollectionViews, view models, event handlers, and payloads, but uses solid
     borders.
   - After full GC, alive pages, CollectionViews, view models, and card Borders should stay near zero.

2. `Run shared resource leak`
   - Uses the same pages and cards, but every card uses the shared `Application.Resources`
     `DoubleCollection` for `Border.StrokeDashArray`.
   - On an unpatched build, alive realized card `Border`s should grow after full GC.
   - Because each card has a normal page-level tap handler, those retained Borders keep pages,
     CollectionViews, page view models, and all item view models alive.
   - `Payload definitely retained through alive pages` is the clearest severity number. With
     defaults, an unpatched build can retain about `180 MB` of managed payload, plus the retained pages,
     CollectionViews, handlers, and native view state.

3. `Run per-border mitigation`
   - Uses the same dashed UI and event handlers, but gives each `Border` its own `DoubleCollection`.
   - Counts should return close to the control run because the dash collection dies with its owning
     Border. This demonstrates that the long-lived app-resource collection is the retention root.

The app forces full GC before measurements so retained weak references are meaningful. It also
reports managed heap, GC heap, resident memory, and working-set deltas after collection.
