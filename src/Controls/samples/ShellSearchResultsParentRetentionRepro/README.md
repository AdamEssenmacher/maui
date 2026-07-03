# ShellSearchResults Parent Retention Repro

This repro targets the iOS/Mac Catalyst compatibility `ShellSearchResultsRenderer`.

`ShellSearchResultsRenderer.GetCell()` creates each result item view, assigns its binding context, and then sets `view.Parent = _context.Shell`. The `UIContainerCell` overload used by this path has no Shell reference, so it cannot clear that parent/resource-listener edge when the native cell is later dropped. A live Shell can therefore retain abandoned search result item views and their binding-context payloads.

The app runs two scenarios:

- Control: generate result cells through the real renderer, then explicitly clear `cell.View.Parent` before dropping each cell.
- Current MAUI: generate the same cells and drop them without clearing `Parent`.

Each generated search result carries a 1 MiB payload to make the retained graph obvious. The expected current result is that the live Shell retains nearly all abandoned result views and payloads.

## Local Result

Mac Catalyst autorun on 2026-07-03:

```text
Result: PROVEN
Iterations: 96
Payload per generated search result: 1048576 bytes

Control (clear generated result view Parent before release):
  Created renderer cells: 96/96
  Alive result views: 0/96
  Alive payloads: 0/96
  Shell resource listeners: 2
  Alive payload bytes: 0

Current MAUI (renderer leaves generated result view Parent assigned):
  Created renderer cells: 96/96
  Alive result views: 96/96
  Alive payloads: 96/96
  Shell resource listeners: 98
  Alive payload bytes: 100663296

Severity signal: 96.0 MiB of abandoned search-result payload retained by a live Shell.
```
