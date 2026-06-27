#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Android.Content;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Handlers;
using AListView = Android.Widget.ListView;

namespace AndroidTableViewRendererAdapterRetentionLeakRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveRenderers,
	int AliveAdapters,
	int AliveTableViews,
	int AliveRoots,
	int AliveCells,
	int AlivePayloads,
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
		Control.AliveAdapters == 0 &&
		Control.AliveTableViews == 0 &&
		Control.AliveRoots == 0 &&
		Control.AlivePayloads == 0 &&
		Current.AliveAdapters == Attempts &&
		Current.AliveTableViews == Attempts &&
		Current.AliveRoots == Attempts &&
		Current.AlivePayloads == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidTableViewRendererAdapterRetentionLeakRepro",
			$"Attempts: {Attempts}",
			$"Payload per attempt: {PayloadBytes / 1024 / 1024} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Managed heap baseline: {FormatBytes(ManagedHeapBaseline)}",
			$"Managed heap final: {FormatBytes(ManagedHeapFinal)}",
			$"Managed heap delta: {FormatBytes(ManagedHeapFinal - ManagedHeapBaseline)}");
	}

	string Format(RunStats stats)
	{
		return string.Join(Environment.NewLine,
			$"Run: {stats.Name}",
			$"  retained native ListViews: {stats.Attempts}",
			$"  renderers alive after full GC: {stats.AliveRenderers}/{stats.Attempts}",
			$"  adapters alive after full GC: {stats.AliveAdapters}/{stats.Attempts}",
			$"  TableViews alive after full GC: {stats.AliveTableViews}/{stats.Attempts}",
			$"  TableRoots alive after full GC: {stats.AliveRoots}/{stats.Attempts}",
			$"  cells alive after full GC: {stats.AliveCells}/{stats.Attempts}",
			$"  payloads alive after full GC: {stats.AlivePayloads}/{stats.Attempts}",
			$"  retained payload bytes: {FormatBytes(stats.RetainedPayloadBytes)} ({stats.RetainedPayloadBytes * 100.0 / (PayloadBytes * stats.Attempts):0.0}%)");
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

internal sealed class CleanupCapableTableViewRenderer : TableViewRenderer
{
	static readonly FieldInfo AdapterField =
		typeof(TableViewRenderer).GetField("_adapter", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(nameof(TableViewRenderer), "_adapter");

	public CleanupCapableTableViewRenderer(Context context)
		: base(context)
	{
	}

	public object? CurrentAdapter => AdapterField.GetValue(this);

	public void RunAdapterCleanup()
	{
		if (Control is not null)
			Control.Adapter = null;

		if (AdapterField.GetValue(this) is IDisposable adapter)
			adapter.Dispose();

		AdapterField.SetValue(this, null);
	}
}

internal static class ReproSession
{
	const int Attempts = 80;
	const int PayloadBytes = 1024 * 1024;

	public static async Task<ReproReport> RunAsync(IMauiContext mauiContext)
	{
		await Task.Yield();

		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			mauiContext,
			"control: clear native adapter and dispose TableViewModelRenderer before disconnect",
			runAdapterCleanup: true);

		var current = await RunScenarioAsync(
			mauiContext,
			"current: disconnect leaves native ListView adapter retaining TableView",
			runAdapterCleanup: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(IMauiContext mauiContext, string name, bool runAdapterCleanup)
	{
		var retainedNativeListViews = new List<AListView>(Attempts);
		var rendererRefs = new List<WeakReference<CleanupCapableTableViewRenderer>>(Attempts);
		var adapterRefs = new List<WeakReference<object>>(Attempts);
		var tableViewRefs = new List<WeakReference<TableView>>(Attempts);
		var rootRefs = new List<WeakReference<TableRoot>>(Attempts);
		var cellRefs = new List<WeakReference<TextCell>>(Attempts);
		var payloadRefs = new List<WeakReference<Payload>>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDisconnectedTableViewRenderer(
				mauiContext,
				runAdapterCleanup,
				retainedNativeListViews,
				rendererRefs,
				adapterRefs,
				tableViewRefs,
				rootRefs,
				cellRefs,
				payloadRefs,
				i);

			if (i % 10 == 0)
				await Task.Yield();
		}

		ForceFullGc();
		GC.KeepAlive(retainedNativeListViews);

		var aliveRenderers = rendererRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveAdapters = adapterRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveTableViews = tableViewRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveRoots = rootRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveCells = cellRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloads = payloadRefs.Count(static wr => wr.TryGetTarget(out _));

		return new RunStats(
			name,
			Attempts,
			aliveRenderers,
			aliveAdapters,
			aliveTableViews,
			aliveRoots,
			aliveCells,
			alivePayloads,
			(long)alivePayloads * PayloadBytes);
	}

	static void CreateDisconnectedTableViewRenderer(
		IMauiContext mauiContext,
		bool runAdapterCleanup,
		List<AListView> retainedNativeListViews,
		List<WeakReference<CleanupCapableTableViewRenderer>> rendererRefs,
		List<WeakReference<object>> adapterRefs,
		List<WeakReference<TableView>> tableViewRefs,
		List<WeakReference<TableRoot>> rootRefs,
		List<WeakReference<TextCell>> cellRefs,
		List<WeakReference<Payload>> payloadRefs,
		int index)
	{
		var payload = new Payload(index, PayloadBytes);
		var cell = new TextCell
		{
			Text = $"Stored settings row {index}",
			Detail = "Real apps often bind table cells to view-model payloads.",
			BindingContext = payload
		};
		var section = new TableSection($"Account {index}") { cell };
		var root = new TableRoot($"Settings {index}") { section };
		var tableView = new TableView(root)
		{
			Intent = TableIntent.Settings
		};

		payloadRefs.Add(new WeakReference<Payload>(payload));
		cellRefs.Add(new WeakReference<TextCell>(cell));
		rootRefs.Add(new WeakReference<TableRoot>(root));
		tableViewRefs.Add(new WeakReference<TableView>(tableView));

		var context = mauiContext.Context ?? throw new InvalidOperationException("Android context is not available.");
		var renderer = new CleanupCapableTableViewRenderer(context);
		((IElementHandler)renderer).SetMauiContext(mauiContext);
		((IElementHandler)renderer).SetVirtualView(tableView);
		rendererRefs.Add(new WeakReference<CleanupCapableTableViewRenderer>(renderer));

		if (renderer.CurrentAdapter is { } adapter)
			adapterRefs.Add(new WeakReference<object>(adapter));

		var nativeListView = renderer.Control ?? throw new InvalidOperationException("Expected native ListView.");
		retainedNativeListViews.Add(nativeListView);

		if (runAdapterCleanup)
			renderer.RunAdapterCleanup();

		((IElementHandler)renderer).DisconnectHandler();
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

	sealed class Payload
	{
		public Payload(int id, int byteCount)
		{
			Id = id;
			Bytes = new byte[byteCount];
			Bytes[0] = (byte)(id % 251);
			Bytes[^1] = (byte)((id + 1) % 251);
		}

		public int Id { get; }

		public byte[] Bytes { get; }
	}
}
