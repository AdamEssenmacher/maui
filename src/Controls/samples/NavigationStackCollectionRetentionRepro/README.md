# NavigationStack Collection Retention Repro

This sample proves that retaining `NavigationPage.Navigation.NavigationStack` can retain discarded `NavigationPage` owners.

The app creates many transient `NavigationPage` owners with realistic binding-context payloads, obtains the public read-only `NavigationStack` wrapper, removes stack pages one by one through the underlying `InternalChildren` list so the retained stack handle is empty, and then keeps only the `NavigationStack` handles in an app cache.

The control run clears the retained `InternalChildren.CollectionChanged` owner handler fields by reflection while keeping the same empty stack handles alive. Current MAUI keeps those handlers intact.
