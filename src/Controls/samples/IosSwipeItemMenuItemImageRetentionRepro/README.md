# iOS SwipeItemMenuItem Image Retention Repro

This sample checks whether iOS/Mac Catalyst `SwipeItemMenuItemHandler` disconnect leaves the assigned native button image alive. Each cycle loads a custom MAUI `ImageSource`, lets the handler assign a `UIImage` to the native `UIButton`, attaches a 1 MiB payload to that assigned image, disconnects the handler, and keeps the native button peer alive.

The control run explicitly clears the button image and resets `SourceLoader` before disconnect. The current run uses MAUI's disconnect behavior.

Run:

```sh
dotnet run --project src/Controls/samples/IosSwipeItemMenuItemImageRetentionRepro/IosSwipeItemMenuItemImageRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes the result to the temp file shown by `ReproSession.ResultsPath`.

Verified on Mac Catalyst:

```text
Control estimated assigned native image payload: 0.0 MiB
Current estimated assigned native image payload: 60.0 MiB
native peers with assigned UIImages: 240/240
service results created/disposed: 240/0
RESULT: PROVEN
```
