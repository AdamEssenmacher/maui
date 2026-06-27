#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;

namespace TableRootClearSectionRetentionLeakRepro;

internal static class ReproSession
{
	const int Attempts = 80;
	const int PayloadBytes = 1024 * 1024;

	static readonly BindableProperty PayloadProperty = BindableProperty.CreateAttached(
		"Payload",
		typeof(Payload),
		typeof(ReproSession),
		null);

	static readonly List<TableSection> RetainedRemovedSections = new();

	public static async Task<ReproReport> RunAsync()
	{
		await Task.Yield();

		RetainedRemovedSections.Clear();
		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			"control: remove sections individually before retaining them",
			useRootClear: false);

		var current = await RunScenarioAsync(
			"current: TableRoot.Clear leaves removed sections subscribed",
			useRootClear: true);

		ForceFullGc();
		GC.KeepAlive(RetainedRemovedSections);
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(string name, bool useRootClear)
	{
		var sectionRefs = new List<WeakReference<TableSection>>(Attempts);
		var rootRefs = new List<WeakReference<TableRoot>>(Attempts);
		var tableViewRefs = new List<WeakReference<TableView>>(Attempts);
		var payloadRefs = new List<PayloadWeakReference>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateClearedRoot(
				useRootClear,
				sectionRefs,
				rootRefs,
				tableViewRefs,
				payloadRefs,
				i);

			if (i % 10 == 0)
				await Task.Yield();
		}

		ForceFullGc();
		GC.KeepAlive(RetainedRemovedSections);

		var aliveSections = sectionRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveRoots = rootRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveTableViews = tableViewRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloads = payloadRefs.Count(static wr => wr.Payload.TryGetTarget(out _));
		var alivePayloadByteArrays = payloadRefs.Count(static wr => wr.Bytes.TryGetTarget(out _));

		return new RunStats(
			name,
			Attempts,
			aliveSections,
			aliveRoots,
			aliveTableViews,
			alivePayloads,
			alivePayloadByteArrays,
			(long)alivePayloadByteArrays * PayloadBytes);
	}

	static void CreateClearedRoot(
		bool useRootClear,
		List<WeakReference<TableSection>> sectionRefs,
		List<WeakReference<TableRoot>> rootRefs,
		List<WeakReference<TableView>> tableViewRefs,
		List<PayloadWeakReference> payloadRefs,
		int index)
	{
		var payload = new Payload(index, PayloadBytes);
		var section = new TableSection($"Account {index}")
		{
			new TextCell
			{
				Text = $"Preference group {index}",
				Detail = "Real apps often cache removed sections for later reuse."
			}
		};
		var root = new TableRoot($"Settings {index}") { section };
		var tableView = new TableView(root);
		tableView.SetValue(PayloadProperty, payload);

		sectionRefs.Add(new WeakReference<TableSection>(section));
		rootRefs.Add(new WeakReference<TableRoot>(root));
		tableViewRefs.Add(new WeakReference<TableView>(tableView));
		payloadRefs.Add(new PayloadWeakReference(
			new WeakReference<Payload>(payload),
			new WeakReference<byte[]>(payload.Bytes)));

		if (useRootClear)
		{
			root.Clear();
		}
		else
		{
			while (root.Count > 0)
				root.RemoveAt(root.Count - 1);
		}

		RetainedRemovedSections.Add(section);

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
	int AliveRemovedSections,
	int AliveRoots,
	int AliveTableViews,
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
		Control.AliveRemovedSections == Attempts &&
		Control.AliveRoots == 0 &&
		Control.AliveTableViews == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Current.AliveRemovedSections == Attempts &&
		Current.AliveRoots == Attempts &&
		Current.AliveTableViews == Attempts &&
		Current.AlivePayloads == Attempts &&
		Current.AlivePayloadByteArrays == Attempts;

	public string ToText()
	{
		var builder = new StringBuilder();
		builder.AppendLine("TableRootClearSectionRetentionLeakRepro");
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
		builder.AppendLine($"  retained removed sections: {stats.AliveRemovedSections}/{stats.Attempts}");
		builder.AppendLine($"  TableRoots alive after full GC: {stats.AliveRoots}/{stats.Attempts}");
		builder.AppendLine($"  TableViews alive after full GC: {stats.AliveTableViews}/{stats.Attempts}");
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
