#nullable enable

#pragma warning disable CS0618
#pragma warning disable CA1416
#pragma warning disable CA1422

using System.Text;
using Foundation;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using UIKit;

namespace IosListViewCellTextRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 96;
	internal const int PayloadKiBPerText = 128;
	internal const int NativeCellsPerCycle = 2;
	internal const int TextSlotsPerCycle = 5;

	const long PayloadBytesPerText = PayloadKiBPerText * 1024L;

	static readonly List<RetainedSlot> RetainedSlots = new();
	static readonly List<object> RetainedNativeCells = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-listview-cell-text-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS ListView cell text retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear retained native cell text slots",
			clearNativeText: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: ListView cell renderers leave native text assigned",
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
		var retainedSlots = new List<RetainedSlot>(Cycles * TextSlotsPerCycle);
		var retainedCells = new List<object>(Cycles * NativeCellsPerCycle);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 16 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var cycleResult = await RunCycleAsync(i, clearNativeText);
			retainedSlots.AddRange(cycleResult.Slots);
			retainedCells.AddRange(cycleResult.NativeCells);
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

		var textCell = new TextCell
		{
			Text = CreateLargeText("TextCell primary text", cycle),
			Detail = CreateLargeText("TextCell detail text", cycle)
		};
		var textRenderer = new TextCellRenderer();
		var textNativeCell = (CellTableViewCell)textRenderer.GetCell(textCell, null, tableView);
		AssertPayloadText(textNativeCell.TextLabel?.Text, "TextCellRenderer did not assign payload-sized TextLabel.Text.");
		AssertPayloadText(textNativeCell.DetailTextLabel?.Text, "TextCellRenderer did not assign payload-sized DetailTextLabel.Text.");

		var entryCell = new EntryCell
		{
			Label = CreateLargeText("EntryCell label text", cycle),
			Text = CreateLargeText("EntryCell value text", cycle),
			Placeholder = CreateLargeText("EntryCell placeholder text", cycle)
		};
		var entryRenderer = new EntryCellRenderer();
		var entryNativeCell = (EntryCellRenderer.EntryCellTableViewCell)entryRenderer.GetCell(entryCell, null, tableView);
		AssertPayloadText(entryNativeCell.TextLabel?.Text, "EntryCellRenderer did not assign payload-sized label text.");
		AssertPayloadText(entryNativeCell.TextField.Text, "EntryCellRenderer did not assign payload-sized text-field text.");
		AssertPayloadText(entryNativeCell.TextField.Placeholder, "EntryCellRenderer did not assign payload-sized text-field placeholder.");

		textCell.Text = string.Empty;
		textCell.Detail = string.Empty;
		entryCell.Label = string.Empty;
		entryCell.Text = string.Empty;
		entryCell.Placeholder = string.Empty;

		var slots = new[]
		{
			RetainedSlot.FromLabel(SlotFamily.TextCellText, textNativeCell, textNativeCell.TextLabel),
			RetainedSlot.FromLabel(SlotFamily.TextCellDetail, textNativeCell, textNativeCell.DetailTextLabel),
			RetainedSlot.FromLabel(SlotFamily.EntryCellLabel, entryNativeCell, entryNativeCell.TextLabel),
			RetainedSlot.FromTextField(SlotFamily.EntryCellText, entryNativeCell, entryNativeCell.TextField, TextFieldSlot.Text),
			RetainedSlot.FromTextField(SlotFamily.EntryCellPlaceholder, entryNativeCell, entryNativeCell.TextField, TextFieldSlot.Placeholder)
		};

		if (clearNativeText)
		{
			foreach (var slot in slots)
				slot.Clear();
		}

		var tracked = TrackedCycle.Create(
			cycle,
			textCell,
			entryCell,
			textRenderer,
			entryRenderer,
			tableView);

		await DrainMainQueueAsync();

		var nativeCells = new object[] { textNativeCell, entryNativeCell };
		GC.KeepAlive(nativeCells);
		GC.KeepAlive(slots);

		return new CycleResult(slots, nativeCells, tracked);
	}

	static string CreateLargeText(string prefix, int cycle)
	{
		var header = $"{prefix} {cycle:000}. ";
		var sentence = "Archived customer note, event log, field report, localized label, and offline audit detail. ";
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

	static int CountAssignedPayloadTexts(RetainedSlot slot)
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

	internal enum SlotFamily
	{
		TextCellText,
		TextCellDetail,
		EntryCellLabel,
		EntryCellText,
		EntryCellPlaceholder
	}

	internal enum TextFieldSlot
	{
		Text,
		Placeholder
	}

	internal sealed record RetainedSlot(
		SlotFamily Family,
		object NativeCell,
		UILabel? Label,
		UITextField? TextField,
		TextFieldSlot TextFieldSlot)
	{
		public string? Text =>
			Label is not null
				? Label.Text
				: TextFieldSlot == TextFieldSlot.Text
					? TextField?.Text
					: TextField?.Placeholder;

		public void Clear()
		{
			if (Label is not null)
			{
				Label.Text = string.Empty;
			}
			else if (TextField is not null && TextFieldSlot == TextFieldSlot.Text)
			{
				TextField.Text = string.Empty;
			}
			else if (TextField is not null)
			{
				TextField.Placeholder = string.Empty;
			}
		}

		public static RetainedSlot FromLabel(SlotFamily family, object nativeCell, UILabel? label)
		{
			return new RetainedSlot(family, nativeCell, label, null, TextFieldSlot.Text);
		}

		public static RetainedSlot FromTextField(SlotFamily family, object nativeCell, UITextField textField, TextFieldSlot textFieldSlot)
		{
			return new RetainedSlot(family, nativeCell, null, textField, textFieldSlot);
		}
	}

	sealed record CycleResult(
		IReadOnlyList<RetainedSlot> Slots,
		IReadOnlyList<object> NativeCells,
		TrackedCycle Tracked);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<TextCell> TextCell,
		WeakReference<EntryCell> EntryCell,
		WeakReference<TextCellRenderer> TextRenderer,
		WeakReference<EntryCellRenderer> EntryRenderer,
		WeakReference<UITableView> TableView)
	{
		public static TrackedCycle Create(
			int cycle,
			TextCell textCell,
			EntryCell entryCell,
			TextCellRenderer textRenderer,
			EntryCellRenderer entryRenderer,
			UITableView tableView)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<TextCell>(textCell),
				new WeakReference<EntryCell>(entryCell),
				new WeakReference<TextCellRenderer>(textRenderer),
				new WeakReference<EntryCellRenderer>(entryRenderer),
				new WeakReference<UITableView>(tableView));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativeCells,
		int RetainedTextSlots,
		int AssignedPayloadTexts,
		int AssignedTextCellTextPayloads,
		int AssignedTextCellDetailPayloads,
		int AssignedEntryCellLabelPayloads,
		int AssignedEntryCellTextPayloads,
		int AssignedEntryCellPlaceholderPayloads,
		long EstimatedAssignedTextBytes,
		long EstimatedTextCellTextBytes,
		long EstimatedTextCellDetailBytes,
		long EstimatedEntryCellLabelBytes,
		long EstimatedEntryCellTextBytes,
		long EstimatedEntryCellPlaceholderBytes,
		int AliveTextCells,
		int AliveEntryCells,
		int AliveTextRenderers,
		int AliveEntryRenderers,
		int AliveTableViews)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedSlot> retainedSlots,
			IReadOnlyList<object> retainedCells,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var assignedPayloadTexts = 0;
			var assignedTextCellTextPayloads = 0;
			var assignedTextCellDetailPayloads = 0;
			var assignedEntryCellLabelPayloads = 0;
			var assignedEntryCellTextPayloads = 0;
			var assignedEntryCellPlaceholderPayloads = 0;
			long estimatedAssignedTextBytes = 0;
			long estimatedTextCellTextBytes = 0;
			long estimatedTextCellDetailBytes = 0;
			long estimatedEntryCellLabelBytes = 0;
			long estimatedEntryCellTextBytes = 0;
			long estimatedEntryCellPlaceholderBytes = 0;

			foreach (var slot in retainedSlots)
			{
				var assigned = CountAssignedPayloadTexts(slot);
				var estimated = EstimateAssignedTextBytes(slot);
				assignedPayloadTexts += assigned;
				estimatedAssignedTextBytes += estimated;

				switch (slot.Family)
				{
					case SlotFamily.TextCellText:
						assignedTextCellTextPayloads += assigned;
						estimatedTextCellTextBytes += estimated;
						break;
					case SlotFamily.TextCellDetail:
						assignedTextCellDetailPayloads += assigned;
						estimatedTextCellDetailBytes += estimated;
						break;
					case SlotFamily.EntryCellLabel:
						assignedEntryCellLabelPayloads += assigned;
						estimatedEntryCellLabelBytes += estimated;
						break;
					case SlotFamily.EntryCellText:
						assignedEntryCellTextPayloads += assigned;
						estimatedEntryCellTextBytes += estimated;
						break;
					case SlotFamily.EntryCellPlaceholder:
						assignedEntryCellPlaceholderPayloads += assigned;
						estimatedEntryCellPlaceholderBytes += estimated;
						break;
				}
			}

			var aliveTextCells = 0;
			var aliveEntryCells = 0;
			var aliveTextRenderers = 0;
			var aliveEntryRenderers = 0;
			var aliveTableViews = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.TextCell.TryGetTarget(out _))
					aliveTextCells++;

				if (cycle.EntryCell.TryGetTarget(out _))
					aliveEntryCells++;

				if (cycle.TextRenderer.TryGetTarget(out _))
					aliveTextRenderers++;

				if (cycle.EntryRenderer.TryGetTarget(out _))
					aliveEntryRenderers++;

				if (cycle.TableView.TryGetTarget(out _))
					aliveTableViews++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedCells.Count,
				retainedSlots.Count,
				assignedPayloadTexts,
				assignedTextCellTextPayloads,
				assignedTextCellDetailPayloads,
				assignedEntryCellLabelPayloads,
				assignedEntryCellTextPayloads,
				assignedEntryCellPlaceholderPayloads,
				estimatedAssignedTextBytes,
				estimatedTextCellTextBytes,
				estimatedTextCellDetailBytes,
				estimatedEntryCellLabelBytes,
				estimatedEntryCellTextBytes,
				estimatedEntryCellPlaceholderBytes,
				aliveTextCells,
				aliveEntryCells,
				aliveTextRenderers,
				aliveEntryRenderers,
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
				Control.RetainedTextSlots == expectedSlots &&
				Control.AssignedPayloadTexts == 0 &&
				Current.RetainedNativeCells == expectedCells &&
				Current.RetainedTextSlots == expectedSlots &&
				Current.AssignedPayloadTexts == expectedSlots &&
				Current.AssignedTextCellTextPayloads == Cycles &&
				Current.AssignedTextCellDetailPayloads == Cycles &&
				Current.AssignedEntryCellLabelPayloads == Cycles &&
				Current.AssignedEntryCellTextPayloads == Cycles &&
				Current.AssignedEntryCellPlaceholderPayloads == Cycles &&
				Current.EstimatedAssignedTextBytes >= expectedSlots * PayloadKiBPerText * 1024L * 0.95 &&
				Current.AliveTextCells <= 1 &&
				Current.AliveEntryCells <= 1 &&
				Current.AliveTextRenderers <= 1 &&
				Current.AliveEntryRenderers <= 1 &&
				Current.AliveTableViews <= 1;
		}
	}

	public string ToText()
	{
		var retainedMiB = Current.EstimatedAssignedTextBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedAssignedTextBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosListViewCellTextRetentionRepro",
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
			$"Control estimated assigned native cell text payload: {controlMiB:N1} MiB",
			$"Current estimated assigned native cell text payload: {retainedMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var nativeTextMiB = result.EstimatedAssignedTextBytes / 1024d / 1024d;
		var textCellTextMiB = result.EstimatedTextCellTextBytes / 1024d / 1024d;
		var textCellDetailMiB = result.EstimatedTextCellDetailBytes / 1024d / 1024d;
		var entryCellLabelMiB = result.EstimatedEntryCellLabelBytes / 1024d / 1024d;
		var entryCellTextMiB = result.EstimatedEntryCellTextBytes / 1024d / 1024d;
		var entryCellPlaceholderMiB = result.EstimatedEntryCellPlaceholderBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native cells: {result.RetainedNativeCells}/{result.TrackedCycles * 2}",
			$"  retained native text slots: {result.RetainedTextSlots}/{result.TrackedCycles * 5}",
			$"  assigned payload-sized text slots: {result.AssignedPayloadTexts}/{result.TrackedCycles * 5}",
			$"  assigned TextCell.Text payloads: {result.AssignedTextCellTextPayloads}/{result.TrackedCycles}",
			$"  assigned TextCell.Detail payloads: {result.AssignedTextCellDetailPayloads}/{result.TrackedCycles}",
			$"  assigned EntryCell.Label payloads: {result.AssignedEntryCellLabelPayloads}/{result.TrackedCycles}",
			$"  assigned EntryCell.Text payloads: {result.AssignedEntryCellTextPayloads}/{result.TrackedCycles}",
			$"  assigned EntryCell.Placeholder payloads: {result.AssignedEntryCellPlaceholderPayloads}/{result.TrackedCycles}",
			$"  estimated assigned native text bytes: {result.EstimatedAssignedTextBytes:N0}",
			$"  estimated assigned native text MiB: {nativeTextMiB:N1}",
			$"  estimated TextCell.Text MiB: {textCellTextMiB:N1}",
			$"  estimated TextCell.Detail MiB: {textCellDetailMiB:N1}",
			$"  estimated EntryCell.Label MiB: {entryCellLabelMiB:N1}",
			$"  estimated EntryCell.Text MiB: {entryCellTextMiB:N1}",
			$"  estimated EntryCell.Placeholder MiB: {entryCellPlaceholderMiB:N1}",
			$"  alive TextCells: {result.AliveTextCells}/{result.TrackedCycles}",
			$"  alive EntryCells: {result.AliveEntryCells}/{result.TrackedCycles}",
			$"  alive TextCellRenderers: {result.AliveTextRenderers}/{result.TrackedCycles}",
			$"  alive EntryCellRenderers: {result.AliveEntryRenderers}/{result.TrackedCycles}",
			$"  alive UITableViews: {result.AliveTableViews}/{result.TrackedCycles}");
	}
}
