# DragDropInteractionRetentionLeakRepro

This repro demonstrates an iOS/Mac Catalyst drag/drop retention path in `GesturePlatformManager`.

When `LoadRecognizers()` reuses an existing `UIDragInteraction`, it clears `_interactions` but does not add the reused interaction back to that tracking list. A later `GesturePlatformManager.Dispose()` therefore fails to remove the native interaction. If a drag start has populated `DragAndDropDelegate._platformDragStartingEventArgs`, the native interaction can retain the delegate, platform drag args, and app payload captured by drag-start customization callbacks.

The autorun compares the current path with a control cleanup path that removes the reused native drag/drop interactions and clears the retained drag-start args before disposal.
