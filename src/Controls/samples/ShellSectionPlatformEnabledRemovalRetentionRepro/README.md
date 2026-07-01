# ShellSection Platform-Enabled Removal Retention Repro

This sample proves that removing a `ShellContent` from `ShellSection.Items` while the content page is still platform-enabled can keep the old `ShellSection` graph alive.

`ShellSection.OnChildRemoved()` defers `base.OnChildRemoved()` by subscribing a local callback to the removed page's `PlatformEnabledChanged` event. If app or helper code keeps the removed `ShellContent` handle and the page never becomes platform-disabled, the callback remains attached and captures the removed `ShellContent` plus the old `ShellSection`.

The autorun creates 80 sections. Each section has one removed `ShellContent` and one unrelated sibling `ShellContent` with a 1 MiB payload. Both scenarios retain only the removed `ShellContent` handles:

- Control: explicitly sets the removed page `IsPlatformEnabled = false` after removal, completing the delayed cleanup.
- Current MAUI: leaves the delayed callback pending.

Result from this workspace:

```text
RESULT: PROVEN
explicit platform disable cleanup retained sibling payload bytes: 0
current MAUI retained sibling payload bytes: 83,886,080
```

Build and run:

```bash
dotnet build src/Controls/samples/ShellSectionPlatformEnabledRemovalRetentionRepro/ShellSectionPlatformEnabledRemovalRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
open -W artifacts/bin/ShellSectionPlatformEnabledRemovalRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/ShellSectionPlatformEnabledRemovalRetentionRepro.app
cat /tmp/shellsection-platformenabled-removal-retention-results.txt
```
