#nullable enable

#pragma warning disable CS0618

using System.Text;
using Foundation;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using UIKit;

namespace IosListViewCellAccessibilityRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 96;
	internal const int PayloadKiBPerText = 256;
	internal const int NativeCellsPerCycle = 1;
	internal const int TextSlotsPerCycle = 2;

	const long PayloadBytesPerText = PayloadKiBPerText * 1024L;

	static readonly List<RetainedCell> RetainedCells = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-listview-cell-accessibility-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS ListView cell accessibility retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear retained native cell accessibility text slots",
			clearNativeText: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: CellRenderer leaves native accessibility text assigned",
			clearNativeText: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(context);
		GC.KeepAlive(RetainedCells);

		return new ReproReport(
			Cycles,
			PayloadKiBPerText,
			NativeCellsPerCycle,
			TextSlotsPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(string name, bool clearNativeText)
	{
		var retainedCells = new List<RetainedCell>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 16 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var cycleResult = await RunCycleAsync(i, clearNativeText);
			retainedCells.Add(cycleResult.Cell);
			tracked.Add(cycleResult.Tracked);
		}

		RetainedCells.AddRange(retainedCells);
		ForceFullGc();

		return ScenarioResult.From(name, retainedCells, tracked);
	}

	static async Task<CycleResult> RunCycleAsync(int cycle, bool clearNativeText)
	{
		var tableView = new UITableView();
		var textCell = new TextCell
		{
			Text = "row",
			Detail = "detail"
		};
		AutomationProperties.SetName(textCell, CreateLargeText("legacy TextCell automation name", cycle));
		AutomationProperties.SetHelpText(textCell, CreateLargeText("legacy TextCell automation help text", cycle));

		var renderer = new TextCellRenderer();
		var nativeCell = (CellTableViewCell)renderer.GetCell(textCell, null, tableView);

		AssertPayloadText(nativeCell.AccessibilityLabel, "CellRenderer did not assign payload-sized AccessibilityLabel.");
		AssertPayloadText(nativeCell.AccessibilityHint, "CellRenderer did not assign payload-sized AccessibilityHint.");

		AutomationProperties.SetName(textCell, string.Empty);
		AutomationProperties.SetHelpText(textCell, string.Empty);

		if (clearNativeText)
		{
			nativeCell.AccessibilityLabel = string.Empty;
			nativeCell.AccessibilityHint = string.Empty;
		}

		var tracked = TrackedCycle.Create(cycle, textCell, renderer, tableView);
		await DrainMainQueueAsync();

		GC.KeepAlive(nativeCell);

		return new CycleResult(new RetainedCell(nativeCell), tracked);
	}

	static string CreateLargeText(string prefix, int cycle)
	{
		var header = $"{prefix} {cycle:000}. ";
		var sentence = "Generated accessibility description with diagnostics, offline summary, and localized guidance. ";
		var targetChars = (int)(PayloadBytesPerText / 2);
		var builder = new StringBuilder(targetChars + sentence.Length);
		builder.Append(header);

		while (builder.Length < targetChars)
			builder.Append(sentence);

		if (builder.Length > targetChars)
			builder.Length = targetChars;

		return builder.ToString();
	}

	static void AssertPayloadText(string? text, string message)
	{
		if (EstimateTextBytes(text) < PayloadBytesPerText * 0.95)
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
		public int AssignedPayloadTexts =>
			Count(Cell.AccessibilityLabel) + Count(Cell.AccessibilityHint);

		public long EstimatedTextBytes =>
			Math.Min(EstimateTextBytes(Cell.AccessibilityLabel), PayloadBytesPerText) +
			Math.Min(EstimateTextBytes(Cell.AccessibilityHint), PayloadBytesPerText);

		public int AssignedLabelPayload => Count(Cell.AccessibilityLabel);
		public int AssignedHintPayload => Count(Cell.AccessibilityHint);

		static int Count(string? text)
		{
			return EstimateTextBytes(text) >= PayloadBytesPerText * 0.95 ? 1 : 0;
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
		int AssignedPayloadTexts,
		int AssignedLabelPayloads,
		int AssignedHintPayloads,
		long EstimatedAssignedTextBytes,
		int AliveTextCells,
		int AliveRenderers,
		int AliveTableViews)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedCell> retainedCells,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var assignedPayloadTexts = 0;
			var assignedLabelPayloads = 0;
			var assignedHintPayloads = 0;
			long estimatedAssignedTextBytes = 0;

			foreach (var retainedCell in retainedCells)
			{
				assignedPayloadTexts += retainedCell.AssignedPayloadTexts;
				assignedLabelPayloads += retainedCell.AssignedLabelPayload;
				assignedHintPayloads += retainedCell.AssignedHintPayload;
				estimatedAssignedTextBytes += retainedCell.EstimatedTextBytes;
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
				assignedPayloadTexts,
				assignedLabelPayloads,
				assignedHintPayloads,
				estimatedAssignedTextBytes,
				aliveTextCells,
				aliveRenderers,
				aliveTableViews);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadKiBPerText,
	int NativeCellsPerCycle,
	int TextSlotsPerCycle,
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
			var expectedSlots = Cycles * TextSlotsPerCycle;
			return
				Control.RetainedNativeCells == expectedCells &&
				Control.AssignedPayloadTexts == 0 &&
				Current.RetainedNativeCells == expectedCells &&
				Current.AssignedPayloadTexts == expectedSlots &&
				Current.AssignedLabelPayloads == Cycles &&
				Current.AssignedHintPayloads == Cycles &&
				Current.EstimatedAssignedTextBytes >= expectedSlots * PayloadKiBPerText * 1024L * 0.95 &&
				Current.AliveTextCells <= 1 &&
				Current.AliveRenderers <= 1 &&
				Current.AliveTableViews <= 1;
		}
	}

	public string ToText()
	{
		var retainedMiB = Current.EstimatedAssignedTextBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedAssignedTextBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosListViewCellAccessibilityRetentionRepro",
			$"Cycles: {Cycles}",
			$"Native cells per cycle: {NativeCellsPerCycle}",
			$"Text slots per cycle: {TextSlotsPerCycle}",
			$"Payload per native accessibility text slot: {PayloadKiBPerText} KiB",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated assigned native accessibility text payload: {controlMiB:N1} MiB",
			$"Current estimated assigned native accessibility text payload: {retainedMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var nativeTextMiB = result.EstimatedAssignedTextBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native cells: {result.RetainedNativeCells}/{result.TrackedCycles}",
			$"  assigned payload-sized accessibility text slots: {result.AssignedPayloadTexts}/{result.TrackedCycles * 2}",
			$"  assigned AccessibilityLabel payloads: {result.AssignedLabelPayloads}/{result.TrackedCycles}",
			$"  assigned AccessibilityHint payloads: {result.AssignedHintPayloads}/{result.TrackedCycles}",
			$"  estimated assigned native accessibility text bytes: {result.EstimatedAssignedTextBytes:N0}",
			$"  estimated assigned native accessibility text MiB: {nativeTextMiB:N1}",
			$"  alive TextCells: {result.AliveTextCells}/{result.TrackedCycles}",
			$"  alive TextCellRenderers: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  alive UITableViews: {result.AliveTableViews}/{result.TrackedCycles}");
	}
}
