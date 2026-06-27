#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;

namespace TableViewCellParentRetentionLeakRepro;

internal static class ReproSession
{
	const int Attempts = 80;
	const int PayloadBytes = 1024 * 1024;

	static readonly List<TableView> LiveTableViews = new();

	public static async Task<ReproReport> RunAsync()
	{
		await Task.Yield();

		LiveTableViews.Clear();
		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			"control: clear removed cell Parent after section removal",
			clearRemovedCellParent: true);

		LiveTableViews.Clear();
		ForceFullGc();

		var current = await RunScenarioAsync(
			"current: removed cells keep TableView parent hooks",
			clearRemovedCellParent: false);

		ForceFullGc();
		GC.KeepAlive(LiveTableViews);
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(string name, bool clearRemovedCellParent)
	{
		var tableViewRefs = new List<WeakReference<TableView>>(Attempts);
		var cellRefs = new List<WeakReference<TextCell>>(Attempts);
		var payloadRefs = new List<PayloadWeakReference>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateAndRemoveCell(
				clearRemovedCellParent,
				tableViewRefs,
				cellRefs,
				payloadRefs,
				i);

			if (i % 10 == 0)
				await Task.Yield();
		}

		ForceFullGc();

		var aliveTableViews = tableViewRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveCells = cellRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveCellsWithParent = cellRefs.Count(static wr => wr.TryGetTarget(out var cell) && cell.Parent is TableView);
		var alivePayloads = payloadRefs.Count(static wr => wr.Payload.TryGetTarget(out _));
		var alivePayloadByteArrays = payloadRefs.Count(static wr => wr.Bytes.TryGetTarget(out _));

		GC.KeepAlive(LiveTableViews);

		return new RunStats(
			name,
			Attempts,
			aliveTableViews,
			aliveCells,
			aliveCellsWithParent,
			alivePayloads,
			alivePayloadByteArrays,
			(long)alivePayloadByteArrays * PayloadBytes);
	}

	static void CreateAndRemoveCell(
		bool clearRemovedCellParent,
		List<WeakReference<TableView>> tableViewRefs,
		List<WeakReference<TextCell>> cellRefs,
		List<PayloadWeakReference> payloadRefs,
		int index)
	{
		var payload = new Payload(index, PayloadBytes);
		var cell = new TextCell
		{
			Text = $"Invoice row {index}",
			Detail = "Real apps often remove or refresh TableView rows while the TableView remains on screen.",
			BindingContext = payload
		};
		var section = new TableSection($"Open invoices {index}") { cell };
		var root = new TableRoot($"Account {index}") { section };
		var tableView = new TableView(root);

		LiveTableViews.Add(tableView);
		tableViewRefs.Add(new WeakReference<TableView>(tableView));
		cellRefs.Add(new WeakReference<TextCell>(cell));
		payloadRefs.Add(new PayloadWeakReference(
			new WeakReference<Payload>(payload),
			new WeakReference<byte[]>(payload.Bytes)));

		section.RemoveAt(0);

		if (clearRemovedCellParent)
			cell.Parent = null;

		cell = null!;
		section = null!;
		root = null!;
		tableView = null!;
		payload = null!;
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
		}
	}

	sealed record PayloadWeakReference(WeakReference<Payload> Payload, WeakReference<byte[]> Bytes);

	sealed class Payload
	{
		public Payload(int id, int byteCount)
		{
			Id = id;
			Bytes = new byte[byteCount];
			for (var i = 0; i < Bytes.Length; i += 4096)
				Bytes[i] = (byte)((id + i) % 251);
			Bytes[^1] = (byte)((id + Bytes.Length) % 251);
		}

		public int Id { get; }

		public byte[] Bytes { get; }
	}
}

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveTableViews,
	int AliveRemovedCells,
	int AliveRemovedCellsWithParent,
	int AlivePayloads,
	int AlivePayloadByteArrays,
	long RetainedPayloadBytes);

public sealed record ReproReport(
	int Attempts,
	int PayloadBytes,
	RunStats Control,
	RunStats Current,
	long ManagedHeapBaseline,
	long ManagedHeapFinal)
{
	public bool LeakProved =>
		Control.AliveTableViews == Attempts &&
		Control.AliveRemovedCells == 0 &&
		Control.AliveRemovedCellsWithParent == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Current.AliveTableViews == Attempts &&
		Current.AliveRemovedCells == Attempts &&
		Current.AliveRemovedCellsWithParent == Attempts &&
		Current.AlivePayloads == Attempts &&
		Current.AlivePayloadByteArrays == Attempts;

	public string ToText()
	{
		var builder = new StringBuilder();
		builder.AppendLine("TableViewCellParentRetentionLeakRepro");
		builder.AppendLine($"Attempts: {Attempts}");
		builder.AppendLine($"Payload per attempt: {FormatBytes(PayloadBytes)}");
		builder.AppendLine($"Leak proved: {LeakProved}");
		builder.AppendLine();
		AppendRun(builder, Control);
		builder.AppendLine();
		AppendRun(builder, Current);
		builder.AppendLine();
		builder.AppendLine($"Managed heap baseline: {FormatBytes(ManagedHeapBaseline)}");
		builder.AppendLine($"Managed heap final: {FormatBytes(ManagedHeapFinal)}");
		builder.AppendLine($"Managed heap delta: {FormatBytes(ManagedHeapFinal - ManagedHeapBaseline)}");
		return builder.ToString();
	}

	void AppendRun(StringBuilder builder, RunStats stats)
	{
		builder.AppendLine($"Run: {stats.Name}");
		builder.AppendLine($"  live TableViews intentionally retained: {stats.AliveTableViews}/{stats.Attempts}");
		builder.AppendLine($"  removed cells alive after full GC: {stats.AliveRemovedCells}/{stats.Attempts}");
		builder.AppendLine($"  removed cells still reporting TableView Parent: {stats.AliveRemovedCellsWithParent}/{stats.Attempts}");
		builder.AppendLine($"  payloads alive after full GC: {stats.AlivePayloads}/{stats.Attempts}");
		builder.AppendLine($"  payload byte arrays alive after full GC: {stats.AlivePayloadByteArrays}/{stats.Attempts}");
		builder.AppendLine($"  retained payload bytes: {FormatBytes(stats.RetainedPayloadBytes)} ({stats.RetainedPayloadBytes * 100.0 / (PayloadBytes * stats.Attempts):0.0}%)");
	}

	static string FormatBytes(long bytes)
	{
		var sign = bytes < 0 ? "-" : "";
		var value = Math.Abs((double)bytes);
		if (value >= 1024 * 1024)
			return $"{sign}{value / 1024 / 1024:0.0} MiB";
		if (value >= 1024)
			return $"{sign}{value / 1024:0.0} KiB";
		return $"{bytes} B";
	}
}
