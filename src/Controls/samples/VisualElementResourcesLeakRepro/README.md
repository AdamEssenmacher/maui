# VisualElement.Resources shared ResourceDictionary leak repro

This sample demonstrates that assigning a long-lived `ResourceDictionary` directly to many short-lived `VisualElement.Resources` properties can retain those elements.

`VisualElement.Resources` subscribes to the assigned dictionary with `((IResourceDictionary)_resources).ValuesChanged += OnResourcesChanged`. `ValuesChanged` is a normal strong event. If the assigned dictionary is app-level or otherwise long-lived, the shared dictionary retains every short-lived page that assigned it.

The app runs three scenarios:

1. Control: every page receives a fresh `ResourceDictionary`.
2. Leak: every page assigns the same shared `ResourceDictionary` to `Resources`.
3. Mitigation: every page assigns the shared dictionary, then replaces `Resources` on disappearance.

## Mac Catalyst autorun

```bash
dotnet build src/Controls/samples/VisualElementResourcesLeakRepro/VisualElementResourcesLeakRepro.csproj -f net10.0-maccatalyst
VISUAL_ELEMENT_RESOURCES_LEAK_REPRO_AUTORUN=1 \
VISUAL_ELEMENT_RESOURCES_LEAK_REPRO_RESULTS=/private/tmp/visualelementresourcesleakrepro-results.txt \
artifacts/bin/VisualElementResourcesLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/VisualElementResourcesLeakRepro.app/Contents/MacOS/VisualElementResourcesLeakRepro \
  --auto-run \
  --results=/private/tmp/visualelementresourcesleakrepro-results.txt
```

If the sandbox blocks `/private/tmp`, the app writes the report to:

`~/Library/Containers/com.microsoft.maui.visualelementresourcesleakrepro/Data/Documents/VisualElementResourcesLeakRepro/autorun-results.txt`

## Observed result

Mac Catalyst autorun on 2026-06-25 used 60 pages/run, 120 shared resources, and 2 MB payload/page.

Control with fresh `Resources` dictionaries:

- Retained pages: `0/60`
- Retained root layouts: `0/60`
- Retained payload view models: `0/60`

Shared `VisualElement.Resources` dictionary:

- Retained pages: `60/60`
- Retained root layouts: `60/60`
- Retained payload view models: `60/60`
- Retained payload: `120.0 MB`
- Managed heap delta after GC: `128.0 MB`
- GC heap delta after GC: `129.3 MB`

Mitigation replacing `Resources` on page disappearance:

- Retained pages: `0/60`
- Retained root layouts: `0/60`
- Retained payload view models: `0/60`
