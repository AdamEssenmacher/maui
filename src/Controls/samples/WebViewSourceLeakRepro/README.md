# WebViewSourceLeakRepro

This repro targets the shared `WebView.Source` path where `WebView` subscribes to
`WebViewSource.SourceChanged` with a strong event handler. A long-lived
`HtmlWebViewSource` can retain every closed `WebView` that was assigned to it.

The same retention path applies to `UrlWebViewSource`; this sample uses
`HtmlWebViewSource` so the repro is deterministic and does not depend on network
connectivity.

## Run

Build from the repo root:

```bash
dotnet build Microsoft.Maui.BuildTasks.slnf
dotnet build src/Controls/samples/WebViewSourceLeakRepro/WebViewSourceLeakRepro.csproj -f net10.0-android
dotnet build src/Controls/samples/WebViewSourceLeakRepro/WebViewSourceLeakRepro.csproj -f net10.0-maccatalyst
dotnet build src/Controls/samples/WebViewSourceLeakRepro/WebViewSourceLeakRepro.csproj -f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64
```

Run Mac Catalyst locally:

```bash
dotnet run --project src/Controls/samples/WebViewSourceLeakRepro/WebViewSourceLeakRepro.csproj -f net10.0-maccatalyst
```

Run the three scenarios automatically on Mac Catalyst and write a result file:

```bash
WEBVIEW_SOURCE_LEAK_REPRO_AUTORUN=1 \
WEBVIEW_SOURCE_LEAK_REPRO_RESULTS=/private/tmp/webviewsourceleakrepro-results.txt \
dotnet run --project src/Controls/samples/WebViewSourceLeakRepro/WebViewSourceLeakRepro.csproj -f net10.0-maccatalyst -- --auto-run --results=/private/tmp/webviewsourceleakrepro-results.txt
```

Run the three scenarios automatically on Android:

```bash
dotnet run --project src/Controls/samples/WebViewSourceLeakRepro/WebViewSourceLeakRepro.csproj -f net10.0-android -p:WebViewSourceLeakReproAutoRun=true
adb shell run-as com.microsoft.maui.webviewsourceleakrepro find . -name autorun-results.txt -print -exec cat {} \;
```

## What to Check

Use the default settings first:

- Pages/run: `40`
- Payload MB/page: `3`
- HTML KB: `192`
- Dwell ms/page: `100`

Run these scenarios:

1. `Run control`
   - Pushes and pops the same Shell pages, but each `WebView` gets a fresh `HtmlWebViewSource`.
   - After full GC, alive pages, `WebView`s, and payload view models should stay near zero.

2. `Run shared source`
   - All pages use the same long-lived `HtmlWebViewSource`, matching a shared XAML resource, singleton service, or long-lived view-model property.
   - On an unpatched build, alive pages, `WebView`s, and payload view models should grow with the page count after full GC.
   - `Payload retained by alive view models` is the clearest real-world impact number. With defaults, an unpatched build retains about `120 MB` of view-model payload, plus the retained `WebView`s and native WebView state.

3. `Run mitigation`
   - Uses the same shared source, but sets `WebView.Source = null` when each page disappears.
   - Counts should return close to the control run. This demonstrates that the shared `WebViewSource.SourceChanged` event is the retention root.

The app forces full GC before measurements so retained weak references are meaningful. It also reports managed heap, GC heap, resident memory, and working-set deltas after collection.

## Observed Mac Catalyst Run

On an unpatched local build, the default autorun produced:

```text
Run: control: fresh HtmlWebViewSource per page
Weak refs still alive after full GC:
  pages: 0/40
  WebViews: 0/40
  payload view models: 0/40
Payload retained by alive view models: 0 B (0.0% of allocated payload)

Run: leaky shared HtmlWebViewSource
Weak refs still alive after full GC:
  pages: 0/40
  WebViews: 40/40
  payload view models: 40/40
Payload retained by alive view models: 120.0 MB (100.0% of allocated payload)

Run: mitigation: clear shared source
Weak refs still alive after full GC:
  pages: 0/40
  WebViews: 0/40
  payload view models: 0/40
Payload retained by alive view models: 0 B (0.0% of allocated payload)
```

## Observed Android Run

On an unpatched local `Pixel_9a` emulator, the default autorun produced:

```text
Run: control: fresh HtmlWebViewSource per page
Weak refs still alive after full GC:
  pages: 1/40
  WebViews: 1/40
  payload view models: 1/40
Payload retained by alive view models: 3.0 MB (2.5% of allocated payload)

Run: leaky shared HtmlWebViewSource
Weak refs still alive after full GC:
  pages: 0/40
  WebViews: 40/40
  payload view models: 40/40
Payload retained by alive view models: 120.0 MB (100.0% of allocated payload)

Run: mitigation: clear shared source
Weak refs still alive after full GC:
  pages: 0/40
  WebViews: 0/40
  payload view models: 0/40
Payload retained by alive view models: 0 B (0.0% of allocated payload)
```
