# iOS TabBarItem Title Retention Repro

This Mac Catalyst sample demonstrates that iOS tab bar item conversion leaves native `UITabBarItem.Title` assigned when native tab item peers survive through Objective-C reference counting.

The autorun scenario creates 1,024 cycles through two real MAUI paths:

- compatibility `TabbedRenderer`, which creates child page `UITabBarItem` instances from `Page.Title`
- `ShellSectionRenderer`, which creates Shell section `UITabBarItem` instances from `ShellSection.Title`

Each title uses a generated 2 KiB workflow/tab label. The repro retains only native `UITabBarItem` peers with Objective-C `retain`, disconnects the managed renderer/page/section graph, and compares current cleanup with a control path that explicitly clears `UITabBarItem.Title` before teardown.

Expected proof output:

```text
Tab title cycles per scenario: 1024
Payload per native tab title: 2 KiB
Native tab title slots per cycle: 2
Leak proved: True
Control native peers with assigned titles: 0/2048
Current native peers with assigned titles: 2048/2048
Current estimated retained native title MiB: 4.0
Alive pages/sections: <=3/2048
```

Run:

```bash
dotnet run --project src/Controls/samples/IosTabBarItemTitleRetentionRepro/IosTabBarItemTitleRetentionRepro.csproj \
  -f net10.0-maccatalyst \
  -p:UseMaui=false \
  -p:IncludeMacCatalystTargetFrameworks=true \
  -m:1 \
  -nr:false
```
