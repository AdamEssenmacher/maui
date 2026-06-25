# ResourceDictionary MergedDictionaries leak repro

This sample demonstrates that adding a long-lived `ResourceDictionary` to many short-lived page `ResourceDictionary.MergedDictionaries` collections can retain the page dictionaries and their owning pages.

`ResourceDictionary.MergedDictionaries_CollectionChanged` subscribes to each merged dictionary with `rd.ValuesChanged += Item_ValuesChanged`. `ValuesChanged` is a normal strong event. If the merged dictionary is app-level or otherwise long-lived, the shared dictionary retains every short-lived page dictionary that merged it. A page resource dictionary also subscribes back to its page through `VisualElement.Resources`, so the page and its binding context can stay alive.

The app runs three scenarios:

1. Control: every page merges a fresh `ResourceDictionary`.
2. Leak: every page merges the same shared `ResourceDictionary`.
3. Mitigation: every page merges the shared dictionary, then clears `MergedDictionaries` on disappearance.

## Mac Catalyst autorun

```bash
dotnet build src/Controls/samples/ResourceDictionaryMergedDictionariesLeakRepro/ResourceDictionaryMergedDictionariesLeakRepro.csproj -f net10.0-maccatalyst
RESOURCE_DICTIONARY_MERGED_DICTIONARIES_LEAK_REPRO_AUTORUN=1 \
RESOURCE_DICTIONARY_MERGED_DICTIONARIES_LEAK_REPRO_RESULTS=/private/tmp/resourcedictionarymergeddictionariesleakrepro-results.txt \
artifacts/bin/ResourceDictionaryMergedDictionariesLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/ResourceDictionaryMergedDictionariesLeakRepro.app/Contents/MacOS/ResourceDictionaryMergedDictionariesLeakRepro \
  --auto-run \
  --results=/private/tmp/resourcedictionarymergeddictionariesleakrepro-results.txt
```

If the sandbox blocks `/private/tmp`, the app writes the report to:

`~/Library/Containers/com.microsoft.maui.resourcedictionarymergeddictionariesleakrepro/Data/Documents/ResourceDictionaryMergedDictionariesLeakRepro/autorun-results.txt`

## Observed result

Mac Catalyst autorun on 2026-06-25 used 60 pages/run, 80 shared resources, 20 page resources, and 2 MB payload/page.

Control with fresh merged dictionaries:

- Retained pages: `0/60`
- Retained page `ResourceDictionary` instances: `0/60`
- Retained root layouts: `0/60`
- Retained payload view models: `0/60`

Shared merged `ResourceDictionary`:

- Retained pages: `60/60`
- Retained page `ResourceDictionary` instances: `60/60`
- Retained root layouts: `60/60`
- Retained payload view models: `60/60`
- Retained payload: `120.0 MB`
- Managed heap delta after GC: `128.3 MB`
- GC heap delta after GC: `129.3 MB`

Mitigation clearing `MergedDictionaries` on page disappearance:

- Retained pages: `0/60`
- Retained page `ResourceDictionary` instances: `0/60`
- Retained root layouts: `0/60`
- Retained payload view models: `0/60`
