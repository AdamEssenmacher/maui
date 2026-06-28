# iOS Shell SearchHandler Text Retention Repro

This sample proves whether iOS/Mac Catalyst compatibility `ShellPageRendererTracker` leaves assigned native `UISearchBar` query and placeholder text state, plus a retained `SearchButtonClicked` handler, on retained native Shell search-bar peers. Each cycle creates a `Shell` page with a `SearchHandler.Query` and `SearchHandler.Placeholder`, routes it through the real Shell tracker search-controller attach/detach path, keeps only the native `UISearchBar` alive, and counts payload-sized native text slots after the Shell, page, search handler, tracker, and handlers are released.

The control run clears `UISearchBar.Text`, `UISearchBar.Placeholder`, and internal text-field placeholder state, then removes the `SearchButtonClicked` handler that current MAUI misses. The current run uses MAUI's Shell tracker detach cleanup.

Verified Mac Catalyst result:

- Control retained 96/96 native search bars, 0/192 payload-sized text slots, 0 B of native search text, and 0/96 trackers.
- Current MAUI retained 96/96 native search bars, 192/192 payload-sized text slots, 50,331,648 B (48.0 MiB) of native search text, and 96/96 trackers.
- Shells, pages, `SearchHandler`s, shell handlers, and page handlers collected in the current run.

Run:

```sh
dotnet run --project src/Controls/samples/IosShellSearchHandlerTextRetentionRepro/IosShellSearchHandlerTextRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes the result to `/tmp/ios-shell-searchhandler-text-retention-results.txt`.
