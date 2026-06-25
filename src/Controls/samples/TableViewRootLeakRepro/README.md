# TableViewRootLeakRepro

This repro tests whether a long-lived `TableRoot` can retain closed `TableView`
instances. `TableView.Root` subscribes to `TableRoot.SectionCollectionChanged`
and `TableRoot.PropertyChanged`, and those subscriptions are removed only when
`Root` is replaced.

## Run

```bash
dotnet build src/Controls/samples/TableViewRootLeakRepro/TableViewRootLeakRepro.csproj -f net10.0-maccatalyst
TABLEVIEW_ROOT_LEAK_REPRO_AUTORUN=1 \
TABLEVIEW_ROOT_LEAK_REPRO_RESULTS=/private/tmp/tableviewrootleakrepro-results.txt \
artifacts/bin/TableViewRootLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/TableViewRootLeakRepro.app/Contents/MacOS/TableViewRootLeakRepro --auto-run
```

Default settings:

- Pages/run: `40`
- Payload MB/page: `3`
- Dwell ms/page: `25`

The control run gives every page a fresh `TableRoot`. The shared-root run uses
one long-lived `TableRoot`. The mitigation run uses the same shared root but
sets `TableView.Root = null` when the page disappears.

## Observed Mac Catalyst Run

On an unpatched local build, the default autorun produced:

```text
Run: control: fresh TableRoot per TableView
Weak refs still alive after full GC:
  pages: 0/40
  TableViews: 0/40
  payload view models: 0/40
Payload retained by alive view models: 0 B (0.0% of allocated payload)

Run: leaky shared TableRoot
Weak refs still alive after full GC:
  pages: 0/40
  TableViews: 40/40
  payload view models: 40/40
Payload retained by alive view models: 120.0 MB (100.0% of allocated payload)

Run: mitigation: clear shared TableView.Root
Weak refs still alive after full GC:
  pages: 0/40
  TableViews: 0/40
  payload view models: 0/40
Payload retained by alive view models: 0 B (0.0% of allocated payload)
```
