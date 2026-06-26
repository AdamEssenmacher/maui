# WebAuthenticatorCompletedOptionsRetentionLeakRepro

This repro demonstrates that `WebAuthenticator.Default` retains the last completed request options.

`WebAuthenticatorImplementation.AuthenticateAsync` stores `currentOptions = webAuthenticatorOptions` on the singleton default implementation. Successful callback completion creates a `WebAuthenticatorResult` from `currentOptions.ResponseDecoder`, but the implementation does not clear `currentOptions` after the task completes. A custom decoder can therefore keep the last request graph alive until another authentication attempt overwrites it.

The repro runs the same completed callback flow twice:

- control: complete each authentication callback, then clear the singleton fields to model the expected cleanup
- current: complete each authentication callback and leave the singleton in the current MAUI state

## Mac Catalyst proof

Observed with the Mac Catalyst target:

```text
WebAuthenticatorCompletedOptionsRetentionLeakRepro
Attempts: 20
Payload per attempt: 8 MiB
Leak proved: True

Run: control: completed callbacks with singleton fields cleared
  tracked attempts: 20
  completed attempts: 20/20
  accepted callbacks: 20/20
  WebAuthenticatorOptions alive after full GC: 0/20
  response decoders alive after full GC: 0/20
  decoder payloads alive after full GC: 0/20
  retained payload bytes: 0 B (0.0%)

Run: current: completed callbacks retain last singleton options
  tracked attempts: 20
  completed attempts: 20/20
  accepted callbacks: 20/20
  WebAuthenticatorOptions alive after full GC: 1/20
  response decoders alive after full GC: 1/20
  decoder payloads alive after full GC: 1/20
  retained payload bytes: 8.0 MiB (5.0%)

Managed heap baseline: 11.7 MiB
Managed heap final: 19.8 MiB
Managed heap delta: 8.1 MiB
```

```sh
dotnet build src/Controls/samples/WebAuthenticatorCompletedOptionsRetentionLeakRepro/WebAuthenticatorCompletedOptionsRetentionLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/WebAuthenticatorCompletedOptionsRetentionLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/WebAuthenticatorCompletedOptionsRetentionLeakRepro.app --args --auto-run --results=/tmp/webauthenticatorcompletedoptionsretentionleakrepro-results.txt
cat /tmp/webauthenticatorcompletedoptionsretentionleakrepro-results.txt
```

## Android build check

The project also builds for Android and includes a matching `WebAuthenticatorCallbackActivity` for the completed callback scheme.

```sh
dotnet build src/Controls/samples/WebAuthenticatorCompletedOptionsRetentionLeakRepro/WebAuthenticatorCompletedOptionsRetentionLeakRepro.csproj -f net10.0-android -p:UseMaui=false -p:IncludeAndroidTargetFrameworks=true -p:EmbedAssembliesIntoApk=true
```
