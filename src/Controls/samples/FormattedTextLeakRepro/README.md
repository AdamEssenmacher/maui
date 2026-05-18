# FormattedTextLeakRepro

This repro targets `Label.FormattedText` when multiple transient labels use a long-lived
`FormattedString` from `Application.Resources`.

The sample models a realistic checkout/account review workflow. Each pushed navigation page
contains account cards with a shared rich disclosure label, similar to terms, financing, and
privacy snippets that product teams often centralize in app resources. Each disclosure label
has a row view model with a moderate payload to represent cached account data, validation
state, draft form data, decoded images, or other row-level state a real app would keep on a
view model.

## Run

Build from the repo root:

```bash
dotnet build Microsoft.Maui.BuildTasks.slnf
dotnet build src/Controls/samples/FormattedTextLeakRepro/FormattedTextLeakRepro.csproj -f net10.0-android
dotnet build src/Controls/samples/FormattedTextLeakRepro/FormattedTextLeakRepro.csproj -f net10.0-maccatalyst
dotnet build src/Controls/samples/FormattedTextLeakRepro/FormattedTextLeakRepro.csproj -f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64
```

Run Mac Catalyst locally:

```bash
dotnet run --project src/Controls/samples/FormattedTextLeakRepro/FormattedTextLeakRepro.csproj -f net10.0-maccatalyst
```

## What to Check

Use the default settings first:

- Pages/run: `30`
- Disclosures/page: `24`
- Payload KB/disclosure: `160`
- Dwell ms/page: `25`

Run these scenarios:

1. `Run inline control`
   - Creates the same rich text content, but each label receives its own `FormattedString`.
   - After full GC, alive `disclosure labels` and `row view models` should stay near zero.

2. `Run shared resource`
   - Every transient label uses one of three `FormattedString` instances rooted in
     `Application.Resources`.
   - On an unpatched build, the app-rooted formatted strings retain each label through
     `PropertyChanging`, `PropertyChanged`, and `SpansCollectionChanged` subscriptions.
   - Each retained label keeps its row view model alive through `BindingContext`.
   - With defaults, the retained row payload can approach `112 MB`, before counting retained
     labels, spans, gestures, native views, or app-specific view-model graphs.

3. `Run mitigation`
   - Uses the same shared app resources, but sets `Label.FormattedText = null` in
     `OnDisappearing`.
   - Counts should return close to the inline control run. This demonstrates that detaching
     the event subscriptions is sufficient to release the labels and row view models.

The app forces full GC before measurements so retained weak references are meaningful. It also
reports managed heap, GC heap, resident memory, and working-set deltas after collection.

On Android, the runner also forces Java peer cleanup and flushes Android's last-popped-page
navigation retention with an untracked blank page before the final snapshot. That keeps the
inline control at `0/720` retained labels while leaving the shared `FormattedString`
subscriptions intact, so the final shared-resource count is not polluted by unrelated Android
navigation cleanup timing.

## Retention Chain

```text
Application.Resources
  -> shared FormattedString
  -> PropertyChanging / PropertyChanged / SpansCollectionChanged invocation lists
  -> Label event handlers
  -> Label.BindingContext
  -> row view model payload
```

The important real-world impact is not only the label object. The label is a root into the
data context that was attached to the row or page when the transient UI was created.
