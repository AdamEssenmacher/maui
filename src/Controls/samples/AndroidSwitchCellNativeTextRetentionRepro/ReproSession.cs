#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Android.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;

namespace AndroidSwitchCellNativeTextRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 512;
	const int TextPayloadChars = 16 * 1024;
	const int TextPayloadBytes = TextPayloadChars * sizeof(char);

	static readonly BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;
	static readonly FieldInfo CellField = typeof(BaseCellView).GetField("_cell", InstanceNonPublic)
		?? throw new MissingFieldException(typeof(BaseCellView).FullName, "_cell");
	static readonly FieldInfo MainTextField = typeof(BaseCellView).GetField("_mainTextText", InstanceNonPublic)
		?? throw new MissingFieldException(typeof(BaseCellView).FullName, "_mainTextText");
	static readonly FieldInfo MainTextViewField = typeof(BaseCellView).GetField("_mainText", InstanceNonPublic)
		?? throw new MissingFieldException(typeof(BaseCellView).FullName, "_mainText");

	static readonly List<SwitchCellView> RetainedNativeRows = new();

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		RetainedNativeRows.Clear();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			"control: clear native SwitchCell row text after disconnect",
			context,
			clearNativeText: true);

		var current = await RunScenarioAsync(
			"current: MAUI disconnect leaves native SwitchCell row text assigned",
			context,
			clearNativeText: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeRows);

		return new ReproReport(
			Cycles,
			TextPayloadChars,
			TextPayloadBytes,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearNativeText)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateCycle(context, i, tracked, clearNativeText);

			if (i % 32 == 0)
				await Task.Yield();
		}

		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	static void CreateCycle(
		IMauiContext context,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeText)
	{
		var textPayload = CreateTextPayload(cycle);
		var cell = new SwitchCell
		{
			Text = textPayload,
			On = cycle % 2 == 0,
			BindingContext = new object()
		};

		var renderer = new SwitchCellRenderer();
		renderer.ParentView = new Grid { FlowDirection = FlowDirection.LeftToRight };
		renderer.SetMauiContext(context);
		cell.Handler = renderer;
		renderer.SetVirtualView(cell);

		if (renderer.PlatformView is not SwitchCellView nativeRow)
			throw new InvalidOperationException($"Expected {nameof(SwitchCellView)}, got {renderer.PlatformView?.GetType().FullName ?? "<null>"}.");

		if (!ReferenceEquals(MainTextField.GetValue(nativeRow), textPayload))
			throw new InvalidOperationException("SwitchCellRenderer did not copy SwitchCell.Text into BaseCellView.MainText.");

		((IElementHandler)renderer).DisconnectHandler();

		ClearKnownCellRoots(nativeRow);

		if (clearNativeText)
			ClearNativeText(nativeRow);

		RetainedNativeRows.Add(nativeRow);
		tracked.Add(TrackedCycle.Create(cycle, nativeRow, cell, renderer, textPayload));
	}

	static string CreateTextPayload(int cycle)
	{
		var prefix = $"switch-row-{cycle:D4}:";
		return prefix + new string((char)('A' + cycle % 26), TextPayloadChars - prefix.Length);
	}

	static void ClearKnownCellRoots(SwitchCellView nativeRow)
	{
		CellField.SetValue(nativeRow, null);
		nativeRow.Cell = null!;
		nativeRow.ContentDescription = null;
	}

	static void ClearNativeText(BaseCellView nativeRow)
	{
		MainTextField.SetValue(nativeRow, null);

		if (MainTextViewField.GetValue(nativeRow) is TextView mainText)
			mainText.Text = string.Empty;
	}

	static bool HasPayloadMainTextField(BaseCellView nativeRow)
	{
		return MainTextField.GetValue(nativeRow) is string value && value.Length == TextPayloadChars;
	}

	static bool HasPayloadMainTextView(BaseCellView nativeRow)
	{
		return MainTextViewField.GetValue(nativeRow) is TextView mainText && mainText.Text?.Length == TextPayloadChars;
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Java.Lang.JavaSystem.Gc();
			Thread.Sleep(100);
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<SwitchCellView> NativeRow,
		WeakReference<SwitchCell> Cell,
		WeakReference<SwitchCellRenderer> Renderer,
		WeakReference<string> TextPayload)
	{
		public static TrackedCycle Create(
			int cycle,
			SwitchCellView nativeRow,
			SwitchCell cell,
			SwitchCellRenderer renderer,
			string textPayload)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<SwitchCellView>(nativeRow),
				new WeakReference<SwitchCell>(cell),
				new WeakReference<SwitchCellRenderer>(renderer),
				new WeakReference<string>(textPayload));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeRows,
		int AliveCells,
		int AliveRenderers,
		int AliveTextPayloads,
		int NativeRowsWithPayloadMainTextField,
		int NativeRowsWithPayloadMainTextView,
		long RetainedFieldPayloadBytes,
		long RetainedNativeTextPayloadBytes)
	{
		public long TotalRetainedPayloadBytes => RetainedFieldPayloadBytes + RetainedNativeTextPayloadBytes;

		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativeRows = 0;
			var aliveCells = 0;
			var aliveRenderers = 0;
			var aliveTextPayloads = 0;
			var nativeRowsWithPayloadMainTextField = 0;
			var nativeRowsWithPayloadMainTextView = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativeRow.TryGetTarget(out var nativeRow))
				{
					aliveNativeRows++;

					if (HasPayloadMainTextField(nativeRow))
						nativeRowsWithPayloadMainTextField++;

					if (HasPayloadMainTextView(nativeRow))
						nativeRowsWithPayloadMainTextView++;
				}

				if (cycle.Cell.TryGetTarget(out _))
					aliveCells++;

				if (cycle.Renderer.TryGetTarget(out _))
					aliveRenderers++;

				if (cycle.TextPayload.TryGetTarget(out _))
					aliveTextPayloads++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeRows,
				aliveCells,
				aliveRenderers,
				aliveTextPayloads,
				nativeRowsWithPayloadMainTextField,
				nativeRowsWithPayloadMainTextView,
				(long)nativeRowsWithPayloadMainTextField * TextPayloadBytes,
				(long)nativeRowsWithPayloadMainTextView * TextPayloadBytes);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int TextPayloadChars,
	int TextPayloadBytes,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.AliveNativeRows == Cycles &&
		Current.AliveNativeRows == Cycles &&
		Control.NativeRowsWithPayloadMainTextField == 0 &&
		Control.NativeRowsWithPayloadMainTextView == 0 &&
		Control.AliveTextPayloads == 0 &&
		Current.NativeRowsWithPayloadMainTextField == Cycles &&
		Current.NativeRowsWithPayloadMainTextView == Cycles &&
		Current.AliveTextPayloads == Cycles &&
		Control.AliveCells == 0 &&
		Current.AliveCells == 0 &&
		Control.AliveRenderers == 0 &&
		Current.AliveRenderers == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidSwitchCellNativeTextRetentionRepro",
			$"Cycles: {Cycles}",
			$"Text payload chars per cycle: {TextPayloadChars:N0}",
			$"Text payload bytes per slot: {TextPayloadBytes:N0}",
			"Known cell roots neutralized in both runs: BaseCellView._cell, SwitchCellView.Cell, ContentDescription",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained payload: {FormatBytes(Control.TotalRetainedPayloadBytes)}",
			$"Current retained payload: {FormatBytes(Current.TotalRetainedPayloadBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  alive native rows: {result.AliveNativeRows}/{result.TrackedCycles}",
			$"  alive cells: {result.AliveCells}/{result.TrackedCycles}",
			$"  alive renderers: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  alive text payload strings: {result.AliveTextPayloads}/{result.TrackedCycles}",
			$"  native rows with payload _mainTextText: {result.NativeRowsWithPayloadMainTextField}/{result.TrackedCycles}",
			$"  native rows with payload main TextView.Text: {result.NativeRowsWithPayloadMainTextView}/{result.TrackedCycles}",
			$"  retained field payload bytes: {result.RetainedFieldPayloadBytes:N0}",
			$"  retained native text payload bytes: {result.RetainedNativeTextPayloadBytes:N0}",
			$"  total retained payload bytes: {result.TotalRetainedPayloadBytes:N0}");
	}

	static string FormatBytes(long bytes)
	{
		if (bytes >= 1024 * 1024)
			return $"{bytes / 1024d / 1024d:N1} MiB";
		if (bytes >= 1024)
			return $"{bytes / 1024d:N1} KiB";
		return $"{bytes:N0} B";
	}
}
