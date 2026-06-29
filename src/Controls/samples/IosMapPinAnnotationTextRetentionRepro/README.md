# iOS MapPin Annotation Text Retention Repro

This Mac Catalyst sample proves that retained `MKPointAnnotation` peers keep
`MapPinHandler` label and address text payloads assigned after handler
disconnect.

Static path:

- `MapPinHandler.iOS.MapLabel()` copies `IMapPin.Label` into
  `MKPointAnnotation.Title`.
- `MapPinHandler.iOS.MapAddress()` copies `IMapPin.Address` into
  `MKPointAnnotation.Subtitle`.
- `MapPinHandler` has no iOS disconnect cleanup, so the old native annotation
  keeps those strings while the native annotation peer is retained by MapKit or
  delayed native cleanup.

The repro uses 512 generated map pins with 32 KiB label and address strings.
That represents dense operational maps where pin callouts include imported
customer, routing, or site-instruction summaries. The current MAUI scenario
therefore retains 32 MiB of native annotation text after the managed pins and
handlers are collectible.

Run:

```bash
dotnet build src/Controls/samples/IosMapPinAnnotationTextRetentionRepro/IosMapPinAnnotationTextRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
artifacts/bin/IosMapPinAnnotationTextRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/IosMapPinAnnotationTextRetentionRepro.app/Contents/MacOS/IosMapPinAnnotationTextRetentionRepro
```

The app writes the result to
`/tmp/ios-mappin-annotation-text-retention-results.txt`.
