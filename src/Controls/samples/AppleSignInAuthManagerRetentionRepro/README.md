# AppleSignIn AuthManager Retention Repro

This repro models the Apple Sign-In lifecycle edge in `AppleSignInAuthenticatorImplementation`.
The implementation stores the current `AuthManager` in an instance field before the native request
starts, but never clears that field after completion or error. `AuthManager` strongly stores the
presentation `UIWindow`, so the static `AppleSignInAuthenticator.Default` singleton can retain a
closed secondary/sign-in window graph until the next Apple Sign-In request or process exit.

Run on Mac Catalyst:

```sh
dotnet run --project src/Controls/samples/AppleSignInAuthManagerRetentionRepro/AppleSignInAuthManagerRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes its autorun report under `Path.GetTempPath()`:

```text
applesignin-authmanager-retention-results.txt
```
