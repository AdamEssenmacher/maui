# MenuFlyout static menu leak repro

This repro targets the iOS/Mac Catalyst static menu-command store used by `MenuFlyoutItemHandler`.

`KeyboardAcceleratorExtensions.CreateMenuItem` assigns every non-context `IMenuFlyoutItem` to `MenuFlyoutItemHandler.menus[index]`. The dictionary is static and strong, and the normal per-item handler disconnect path does not remove the entry. A later app menu rebuild calls `MenuFlyoutItemHandler.Reset()`, but items created between rebuilds can remain rooted indefinitely.

The autorun compares:

- unconverted short-lived `MenuFlyoutItem` instances with one MiB payloads
- short-lived `MenuFlyoutItem` instances converted through `MenuFlyoutItemHandler.SetVirtualView`
- an explicit static reset control

Expected proven result:

- control retains `0/80` payloads and items
- converted menu-command run retains `80/80` payloads and items
- static `menus` count grows by `80`
- explicit reset returns static count to `0`
