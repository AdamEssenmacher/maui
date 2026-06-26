# ResourcesProviderApplicationRetentionLeakRepro

This repro demonstrates that the iOS/Mac Catalyst compatibility `ResourcesProvider` can retain the last throwaway `Application` graph after its system resources are forced.

`DependencyService.Get<ISystemResourcesProvider>()` returns a global `ResourcesProvider`. `ResourcesProvider.GetSystemResources()` assigns a new `ResourceDictionary` to its `_dictionary` field. `Application.SystemResources` subscribes that dictionary's `ValuesChanged` event to `Application.OnParentResourcesChanged`. If a short-lived app host forces system resources and is then dropped, the global provider can retain the dictionary, which retains the app and any app-level payload until another system-resource dictionary replaces it.
