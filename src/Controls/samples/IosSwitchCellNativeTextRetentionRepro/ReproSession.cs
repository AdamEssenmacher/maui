#nullable enable

#pragma warning disable CS0618
#pragma warning disable CA1416
#pragma warning disable CA1422

using System.Text;
using Foundation;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using UIKit;

namespace IosSwitchCellNativeTextRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 512;
	internal const int PayloadKiBPerText = 64;
	internal const int NativeCellsPerCycle = 1;
	internal const int TextSlotsPerCycle = 1;

	const long PayloadBytesPerText = PayloadKiBPerText * 1024L;

	static readonly List<RetainedSlot> RetainedSlots = new();
	static readonly List<object> RetainedNativeCells = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-switchcell-native-text-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS SwitchCell native text retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear retained native SwitchCell label text",
			clearNativeText: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: SwitchCellRenderer leaves native label text assigned",
			clearNativeText: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(context);
		GC.KeepAlive(RetainedSlots);
		GC.KeepAlive(RetainedNativeCells);

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
		var retainedSlots = new List<RetainedSlot>(Cycles);
		var retainedCells = new List<object>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 64 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var cycleResult = await RunCycleAsync(i, clearNativeText);
			retainedSlots.Add(cycleResult.Slot);
			retainedCells.Add(cycleResult.NativeCell);
			tracked.Add(cycleResult.Tracked);
		}

		RetainedSlots.AddRange(retainedSlots);
		RetainedNativeCells.AddRange(retainedCells);
		ForceFullGc();

		return ScenarioResult.From(name, retainedSlots, retainedCells, tracked);
	}

	static async Task<CycleResult> RunCycleAsync(int cycle, bool clearNativeText)
	{
		var tableView = new UITableView();
		var parentListView = new ListView
		{
			FlowDirection = FlowDirection.LeftToRight
		};

		var switchCell = new SwitchCell
		{
			Text = CreateLargeText("SwitchCell label text", cycle),
			On = (cycle & 1) == 0
		};

		switchCell.Parent = parentListView;

		var renderer = new SwitchCellRenderer();
		var nativeCell = (CellTableViewCell)renderer.GetCell(switchCell, null, tableView);
		AssertPayloadText(nativeCell.TextLabel?.Text, "SwitchCellRenderer did not assign payload-sized TextLabel.Text.");

		switchCell.Text = string.Empty;
		switchCell.Parent = null;
		nativeCell.Cell = null;

		var slot = new RetainedSlot(nativeCell, nativeCell.TextLabel);

		if (clearNativeText)
			slot.Clear();

		var tracked = TrackedCycle.Create(cycle, switchCell, renderer, tableView, parentListView);

		await DrainMainQueueAsync();

		GC.KeepAlive(nativeCell);
		GC.KeepAlive(slot);

		return new CycleResult(slot, nativeCell, tracked);
	}

	static string CreateLargeText(string prefix, int cycle)
	{
		var header = $"{prefix} {cycle:0000}. ";
		var sentence = "Imported settings row, compliance note, localized preference label, offline audit state, and diagnostic metadata. ";
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

	static int CountAssignedPayloadText(RetainedSlot slot)
	{
		return EstimateTextBytes(slot.Text) >= PayloadBytesPerText * 0.95 ? 1 : 0;
	}

	static long EstimateAssignedTextBytes(RetainedSlot slot)
	{
		return Math.Min(EstimateTextBytes(slot.Text), PayloadBytesPerText);
	}

	static long EstimateTextBytes(string? text)
	{
		return string.IsNullOrEmpty(text) ? 0 : text.Length * 2L;
	}

	internal sealed record RetainedSlot(object NativeCell, UILabel? Label)
	{
		public string? Text => Label?.Text;

		public void Clear()
		{
			if (Label is not null)
				Label.Text = string.Empty;
		}
	}

	sealed record CycleResult(
		RetainedSlot Slot,
		object NativeCell,
		TrackedCycle Tracked);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<SwitchCell> SwitchCell,
		WeakReference<SwitchCellRenderer> Renderer,
		WeakReference<UITableView> TableView,
		WeakReference<ListView> ParentListView)
	{
		public static TrackedCycle Create(
			int cycle,
			SwitchCell switchCell,
			SwitchCellRenderer renderer,
			UITableView tableView,
			ListView parentListView)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<SwitchCell>(switchCell),
				new WeakReference<SwitchCellRenderer>(renderer),
				new WeakReference<UITableView>(tableView),
				new WeakReference<ListView>(parentListView));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativeCells,
		int RetainedTextSlots,
		int AssignedPayloadTexts,
		long EstimatedAssignedTextBytes,
		int AliveSwitchCells,
		int AliveRenderers,
		int AliveTableViews,
		int AliveParentListViews)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedSlot> retainedSlots,
			IReadOnlyList<object> retainedCells,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var assignedPayloadTexts = 0;
			long estimatedAssignedTextBytes = 0;

			foreach (var slot in retainedSlots)
			{
				assignedPayloadTexts += CountAssignedPayloadText(slot);
				estimatedAssignedTextBytes += EstimateAssignedTextBytes(slot);
			}

			var aliveSwitchCells = 0;
			var aliveRenderers = 0;
			var aliveTableViews = 0;
			var aliveParentListViews = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.SwitchCell.TryGetTarget(out _))
					aliveSwitchCells++;

				if (cycle.Renderer.TryGetTarget(out _))
					aliveRenderers++;

				if (cycle.TableView.TryGetTarget(out _))
					aliveTableViews++;

				if (cycle.ParentListView.TryGetTarget(out _))
					aliveParentListViews++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedCells.Count,
				retainedSlots.Count,
				assignedPayloadTexts,
				estimatedAssignedTextBytes,
				aliveSwitchCells,
				aliveRenderers,
				aliveTableViews,
				aliveParentListViews);
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
				Control.RetainedTextSlots == expectedSlots &&
				Control.AssignedPayloadTexts == 0 &&
				Current.RetainedNativeCells == expectedCells &&
				Current.RetainedTextSlots == expectedSlots &&
				Current.AssignedPayloadTexts == expectedSlots &&
				Current.EstimatedAssignedTextBytes >= expectedSlots * PayloadKiBPerText * 1024L * 0.95 &&
				Current.AliveSwitchCells <= 1 &&
				Current.AliveRenderers <= 1 &&
				Current.AliveTableViews <= 1 &&
				Current.AliveParentListViews <= 1;
		}
	}

	public string ToText()
	{
		var retainedMiB = Current.EstimatedAssignedTextBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedAssignedTextBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosSwitchCellNativeTextRetentionRepro",
			$"Cycles: {Cycles}",
			$"Native cells per cycle: {NativeCellsPerCycle}",
			$"Text slots per cycle: {TextSlotsPerCycle}",
			$"Payload per native text slot: {PayloadKiBPerText} KiB",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated assigned native SwitchCell text payload: {controlMiB:N1} MiB",
			$"Current estimated assigned native SwitchCell text payload: {retainedMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var nativeTextMiB = result.EstimatedAssignedTextBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native cells: {result.RetainedNativeCells}/{result.TrackedCycles}",
			$"  retained native text slots: {result.RetainedTextSlots}/{result.TrackedCycles}",
			$"  assigned payload-sized text slots: {result.AssignedPayloadTexts}/{result.TrackedCycles}",
			$"  estimated assigned native text bytes: {result.EstimatedAssignedTextBytes:N0}",
			$"  estimated assigned native text MiB: {nativeTextMiB:N1}",
			$"  alive SwitchCells: {result.AliveSwitchCells}/{result.TrackedCycles}",
			$"  alive SwitchCellRenderers: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  alive UITableViews: {result.AliveTableViews}/{result.TrackedCycles}",
			$"  alive parent ListViews: {result.AliveParentListViews}/{result.TrackedCycles}");
	}
}
