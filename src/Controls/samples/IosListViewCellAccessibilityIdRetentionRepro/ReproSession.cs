#nullable enable

#pragma warning disable CS0618

using System.Text;
using Foundation;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using UIKit;

namespace IosListViewCellAccessibilityIdRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 1024;
	internal const int PayloadKiBPerIdentifier = 16;
	internal const int NativeCellsPerCycle = 1;
	internal const int IdentifierSlotsPerCycle = 1;

	const long PayloadBytesPerIdentifier = PayloadKiBPerIdentifier * 1024L;

	static readonly List<RetainedCell> RetainedCells = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-listview-cell-accessibilityid-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS ListView cell accessibility identifier retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear retained native cell accessibility identifier",
			clearNativeIdentifier: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: TextCellRenderer leaves native accessibility identifier assigned",
			clearNativeIdentifier: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(context);
		GC.KeepAlive(RetainedCells);

		return new ReproReport(
			Cycles,
			PayloadKiBPerIdentifier,
			NativeCellsPerCycle,
			IdentifierSlotsPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(string name, bool clearNativeIdentifier)
	{
		var retainedCells = new List<RetainedCell>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 16 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var cycleResult = await RunCycleAsync(i, clearNativeIdentifier);
			retainedCells.Add(cycleResult.Cell);
			tracked.Add(cycleResult.Tracked);
		}

		RetainedCells.AddRange(retainedCells);
		ForceFullGc();

		return ScenarioResult.From(name, retainedCells, tracked);
	}

	static async Task<CycleResult> RunCycleAsync(int cycle, bool clearNativeIdentifier)
	{
		var tableView = new UITableView();
		var textCell = new TextCell
		{
			Text = "row",
			Detail = "detail",
			AutomationId = CreateIdentifierPayload(cycle)
		};

		var renderer = new TextCellRenderer();
		var nativeCell = (CellTableViewCell)renderer.GetCell(textCell, null, tableView);

		AssertPayloadIdentifier(nativeCell.AccessibilityIdentifier, "TextCellRenderer did not assign payload-sized AccessibilityIdentifier.");

		textCell.ClearValue(Element.AutomationIdProperty);

		if (clearNativeIdentifier)
		{
			nativeCell.AccessibilityIdentifier = string.Empty;
		}

		var tracked = TrackedCycle.Create(cycle, textCell, renderer, tableView);
		await DrainMainQueueAsync();

		GC.KeepAlive(nativeCell);

		return new CycleResult(new RetainedCell(nativeCell), tracked);
	}

	static string CreateIdentifierPayload(int cycle)
	{
		var header = $"legacy-listview-textcell-{cycle:0000}-diagnostic-route-";
		var sentence = "generated-row-automation-id-workflow-context-command-confirmation-";
		var targetChars = (int)(PayloadBytesPerIdentifier / 2);
		var builder = new StringBuilder(targetChars + sentence.Length);
		builder.Append(header);

		while (builder.Length < targetChars)
			builder.Append(sentence);

		if (builder.Length > targetChars)
			builder.Length = targetChars;

		return builder.ToString();
	}

	static void AssertPayloadIdentifier(string? text, string message)
	{
		if (EstimateTextBytes(text) < PayloadBytesPerIdentifier * 0.95)
			throw new InvalidOperationException(message);
	}

	static async Task DrainMainQueueAsync()
	{
		await Task.Delay(30);
		NSRunLoop.Main.RunUntil(NSDate.FromTimeIntervalSinceNow(0.01));
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			using var pool = new NSAutoreleasePool();
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(75);
		}
	}

	static void WriteProgress(string message)
	{
		try
		{
			File.WriteAllText(ResultsPath, message + Environment.NewLine);
		}
		catch
		{
			// Progress output is diagnostic only; the final report write remains authoritative.
		}
	}

	static long EstimateTextBytes(string? text)
	{
		return string.IsNullOrEmpty(text) ? 0 : text.Length * 2L;
	}

	internal sealed record RetainedCell(CellTableViewCell Cell)
	{
		public int AssignedPayloadIdentifiers =>
			Count(Cell.AccessibilityIdentifier);

		public long EstimatedIdentifierBytes =>
			Math.Min(EstimateTextBytes(Cell.AccessibilityIdentifier), PayloadBytesPerIdentifier);

		static int Count(string? text)
		{
			return EstimateTextBytes(text) >= PayloadBytesPerIdentifier * 0.95 ? 1 : 0;
		}
	}

	sealed record CycleResult(RetainedCell Cell, TrackedCycle Tracked);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<TextCell> TextCell,
		WeakReference<TextCellRenderer> Renderer,
		WeakReference<UITableView> TableView)
	{
		public static TrackedCycle Create(
			int cycle,
			TextCell textCell,
			TextCellRenderer renderer,
			UITableView tableView)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<TextCell>(textCell),
				new WeakReference<TextCellRenderer>(renderer),
				new WeakReference<UITableView>(tableView));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativeCells,
		int AssignedPayloadIdentifiers,
		long EstimatedAssignedIdentifierBytes,
		int AliveTextCells,
		int AliveRenderers,
		int AliveTableViews)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedCell> retainedCells,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var assignedPayloadIdentifiers = 0;
			long estimatedAssignedIdentifierBytes = 0;

			foreach (var retainedCell in retainedCells)
			{
				assignedPayloadIdentifiers += retainedCell.AssignedPayloadIdentifiers;
				estimatedAssignedIdentifierBytes += retainedCell.EstimatedIdentifierBytes;
			}

			var aliveTextCells = 0;
			var aliveRenderers = 0;
			var aliveTableViews = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.TextCell.TryGetTarget(out _))
					aliveTextCells++;

				if (cycle.Renderer.TryGetTarget(out _))
					aliveRenderers++;

				if (cycle.TableView.TryGetTarget(out _))
					aliveTableViews++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedCells.Count,
				assignedPayloadIdentifiers,
				estimatedAssignedIdentifierBytes,
				aliveTextCells,
				aliveRenderers,
				aliveTableViews);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadKiBPerIdentifier,
	int NativeCellsPerCycle,
	int IdentifierSlotsPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved
	{
		get
		{
			var expectedCells = Cycles * NativeCellsPerCycle;
			var expectedSlots = Cycles * IdentifierSlotsPerCycle;
			return
				Control.RetainedNativeCells == expectedCells &&
				Control.AssignedPayloadIdentifiers == 0 &&
				Current.RetainedNativeCells == expectedCells &&
				Current.AssignedPayloadIdentifiers == expectedSlots &&
				Current.EstimatedAssignedIdentifierBytes >= expectedSlots * PayloadKiBPerIdentifier * 1024L * 0.95 &&
				Current.AliveTextCells <= 1 &&
				Current.AliveRenderers <= 1 &&
				Current.AliveTableViews <= 1;
		}
	}

	public string ToText()
	{
		var retainedMiB = Current.EstimatedAssignedIdentifierBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedAssignedIdentifierBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosListViewCellAccessibilityIdRetentionRepro",
			$"Cycles: {Cycles}",
			$"Native cells per cycle: {NativeCellsPerCycle}",
			$"Accessibility identifier slots per cycle: {IdentifierSlotsPerCycle}",
			$"Payload per native accessibility identifier slot: {PayloadKiBPerIdentifier} KiB",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated assigned native accessibility identifier payload: {controlMiB:N1} MiB",
			$"Current estimated assigned native accessibility identifier payload: {retainedMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var nativeIdentifierMiB = result.EstimatedAssignedIdentifierBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native cells: {result.RetainedNativeCells}/{result.TrackedCycles}",
			$"  assigned payload-sized accessibility identifier slots: {result.AssignedPayloadIdentifiers}/{result.TrackedCycles * ReproSession.IdentifierSlotsPerCycle}",
			$"  estimated assigned native accessibility identifier bytes: {result.EstimatedAssignedIdentifierBytes:N0}",
			$"  estimated assigned native accessibility identifier MiB: {nativeIdentifierMiB:N1}",
			$"  alive TextCells: {result.AliveTextCells}/{result.TrackedCycles}",
			$"  alive TextCellRenderers: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  alive UITableViews: {result.AliveTableViews}/{result.TrackedCycles}");
	}
}
