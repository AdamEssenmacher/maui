# iOS ShellSection Root Header Title Retention Repro

This sample proves whether iOS/Mac Catalyst compatibility `ShellSectionRootHeader` leaves native collection header-cell label text assigned on retained native `ShellSectionHeaderCell` peers. Each cycle creates a Shell section with a generated `ShellContent.Title`, routes it through the real `ShellSectionRootHeader.GetCell()` path, clears the managed `ShellContent.Title` after native assignment, keeps only the produced native header cell alive, and counts payload-sized native label text slots after the Shell, section, content, and header controller are released.

The control run clears the native header cell `UILabel.Text` before retaining the cell. The current run uses MAUI's header-cell assignment as-is.

Run:

```sh
dotnet run --project src/Controls/samples/IosShellSectionRootHeaderTitleRetentionRepro/IosShellSectionRootHeaderTitleRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes the result to `/tmp/ios-shellsection-rootheader-title-retention-results.txt`.

Verified result:

```text
Cycles: 96
Payload per native title: 256 KiB
Leak proved: True

control:
  retained native header cells: 96/96
  assigned payload-sized titles: 0/96
  alive headers/shells/sections/contents/pages: 0/0/0/0/0

current:
  retained native header cells: 96/96
  assigned payload-sized titles: 96/96
  estimated assigned native title MiB: 24.0
  alive headers/shells/sections/contents/pages: 0/0/0/0/0

RESULT: PROVEN
```
