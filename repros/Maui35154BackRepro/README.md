# Maui35154BackRepro

Minimal Android repro for the root-page back interception regression introduced by dotnet/maui#35154.

The app uses a root `ContentPage` with no `NavigationPage`, modal stack, or Shell stack. The page overrides `OnBackButtonPressed()`, writes `HANDLED` to `FileSystem.AppDataDirectory/back-result.txt`, updates the visible label, and returns `true`.

Verified on Android API 30. This specifically demonstrates the legacy `Activity.OnBackPressed()` path that #35154 removed.

Expected behavior before dotnet/maui#35154:

1. App launch writes `STARTED`.
2. Android Back invokes `RootBackInterceptPage.OnBackButtonPressed()`.
3. The result file changes to `HANDLED`.
4. The app remains foregrounded.

Regressed behavior after dotnet/maui#35154:

1. App launch writes `STARTED`.
2. Android Back does not invoke the root page override.
3. The result file remains `STARTED`.
4. Android performs default back behavior and backgrounds/exits the app.

Use `/private/tmp/maui35154-back-repro/run-one.sh` from either temporary worktree:

```bash
/private/tmp/maui35154-back-repro/run-one.sh /private/tmp/maui35154-before
/private/tmp/maui35154-back-repro/run-one.sh /private/tmp/maui35154-after
```

See `/private/tmp/maui35154-back-repro/RESULTS.md` for captured before/after output.
