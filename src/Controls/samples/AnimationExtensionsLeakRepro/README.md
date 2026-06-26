# AnimationExtensionsLeakRepro

Mac Catalyst repro for `AnimationExtensions.s_animations` retaining completed
animations after the weak `AnimatableKey` target is collected before the
animation finishes.

`AnimatableKey.GetHashCode()` depends on the weak target while the target is
alive, but falls back to the animation handle after that target is collected.
That mutates the dictionary key hash after insertion. When `Tweener.Finished`
later calls `s_animations.TryGetValue(tweener.Handle, out var info)`, the lookup
can miss the original bucket, so the static entry is never removed.

## Run

```bash
dotnet build src/Controls/samples/AnimationExtensionsLeakRepro/AnimationExtensionsLeakRepro.csproj \
  -f net10.0-maccatalyst \
  -p:UseMaui=false

open -W artifacts/bin/AnimationExtensionsLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-*/AnimationExtensionsLeakRepro.app
cat "$HOME/Library/Containers/com.microsoft.maui.animationextensionsleakrepro/Data/Library/autorun-results.txt"
```

## Result

On Mac Catalyst, built from commit `a6d9e30a62`:

```text
RESULT: PROVEN
before: static-animations=0, static-tweeners=0
owner-alive-control: payloads=0/80, probes=0/80, managers=0/80, tickers=0/80, probes-before-finish=80/80, static-animations=0, static-tweeners=0
owner-collected-before-finish: payloads=80/80, probes=0/80, managers=80/80, tickers=80/80, probes-before-finish=0/80, static-animations=80, static-tweeners=0
after: static-animations=80, static-tweeners=0
payload-bytes-per-scenario=83886080
app-data-directory=/Users/adam/Library/Containers/com.microsoft.maui.animationextensionsleakrepro/Data/Library
dotnet-version=10.0.7
```
