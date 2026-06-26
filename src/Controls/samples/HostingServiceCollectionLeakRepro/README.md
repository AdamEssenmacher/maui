# HostingServiceCollectionLeakRepro

Mac Catalyst repro for static hosting caches retaining short-lived handler and
image-source service collections created by throwaway `MauiApp` hosts.

`RegisteredHandlerServiceTypeSet.s_instances` uses `IMauiHandlersCollection` as
a strong key and `ImageSourceToImageSourceServiceTypeMapping.s_instances` uses
`IImageSourceServiceCollection` as a strong key. Both collection types inherit
`IServiceCollection`, so keeping the collection key alive also keeps service
descriptors and any captured registration factory state alive after the host is
disposed.

## Run

```bash
dotnet build src/Controls/samples/HostingServiceCollectionLeakRepro/HostingServiceCollectionLeakRepro.csproj \
  -f net10.0-maccatalyst \
  -p:UseMaui=false

open -W artifacts/bin/HostingServiceCollectionLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-*/HostingServiceCollectionLeakRepro.app
cat "$HOME/Library/Containers/com.microsoft.maui.hostingservicecollectionleakrepro/Data/Library/autorun-results.txt"
```

The generated app executable can also be run directly:

```bash
artifacts/bin/HostingServiceCollectionLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/HostingServiceCollectionLeakRepro.app/Contents/MacOS/HostingServiceCollectionLeakRepro
cat "$HOME/Library/autorun-results.txt"
```

## Result

On Mac Catalyst, built from commit `a6d9e30a62`:

```text
RESULT: PROVEN
before: handler-collections=1, image-source-collections=1
unresolved-control: payloads=0/120, collections=0/120
after-control: handler-collections=1, image-source-collections=1
resolved-handler-collection: payloads=60/60, collections=60/60
after-handler: handler-collections=61, image-source-collections=1
handler-static-delta=60
resolved-image-source-collection: payloads=60/60, collections=60/60
after-image: handler-collections=61, image-source-collections=61
image-source-static-delta=60
payload-bytes-per-leak-scenario=62914560
app-data-directory=/Users/adam/Library
dotnet-version=10.0.7
```
