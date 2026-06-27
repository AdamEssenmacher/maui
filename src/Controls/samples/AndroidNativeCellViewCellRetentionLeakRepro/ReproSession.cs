#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Handlers;
using AView = Android.Views.View;

namespace AndroidNativeCellViewCellRetentionLeakRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveNativeViews,
	int AliveRenderers,
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
		Control.AliveCells == 0 &&
		Control.AlivePayloads == 0 &&
		Current.AliveCells == Attempts &&
		Current.AlivePayloads == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidNativeCellViewCellRetentionLeakRepro",
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
			$"  retained native cell views: {stats.Attempts}",
			$"  native views alive after full GC: {stats.AliveNativeViews}/{stats.Attempts}",
			$"  renderers alive after full GC: {stats.AliveRenderers}/{stats.Attempts}",
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

internal static class ReproSession
{
	const int Attempts = 80;
	const int PayloadBytes = 1024 * 1024;

	static readonly FieldInfo BaseCellViewCellField =
		typeof(BaseCellView).GetField("_cell", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(nameof(BaseCellView), "_cell");

	public static async Task<ReproReport> RunAsync(IMauiContext mauiContext)
	{
		await Task.Yield();

		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			mauiContext,
			"control: clear native BaseCellView._cell before renderer disconnect",
			clearNativeCellReference: true);

		var current = await RunScenarioAsync(
			mauiContext,
			"current: disconnect leaves native cell view retaining Cell",
			clearNativeCellReference: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(IMauiContext mauiContext, string name, bool clearNativeCellReference)
	{
		var retainedNativeViews = new List<AView>(Attempts);
		var nativeViewRefs = new List<WeakReference<AView>>(Attempts);
		var rendererRefs = new List<WeakReference<TextCellRenderer>>(Attempts);
		var cellRefs = new List<WeakReference<TextCell>>(Attempts);
		var payloadRefs = new List<WeakReference<Payload>>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDisconnectedCellRenderer(
				mauiContext,
				clearNativeCellReference,
				retainedNativeViews,
				nativeViewRefs,
				rendererRefs,
				cellRefs,
				payloadRefs,
				i);

			if (i % 10 == 0)
				await Task.Yield();
		}

		ForceFullGc();
		GC.KeepAlive(retainedNativeViews);

		var aliveNativeViews = nativeViewRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveRenderers = rendererRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveCells = cellRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloads = payloadRefs.Count(static wr => wr.TryGetTarget(out _));

		return new RunStats(
			name,
			Attempts,
			aliveNativeViews,
			aliveRenderers,
			aliveCells,
			alivePayloads,
			(long)alivePayloads * PayloadBytes);
	}

	static void CreateDisconnectedCellRenderer(
		IMauiContext mauiContext,
		bool clearNativeCellReference,
		List<AView> retainedNativeViews,
		List<WeakReference<AView>> nativeViewRefs,
		List<WeakReference<TextCellRenderer>> rendererRefs,
		List<WeakReference<TextCell>> cellRefs,
		List<WeakReference<Payload>> payloadRefs,
		int index)
	{
		var payload = new Payload(index, PayloadBytes);
		var cell = new TextCell
		{
			Text = $"Cached row {index}",
			Detail = "Large row binding payload",
			BindingContext = payload
		};

		payloadRefs.Add(new WeakReference<Payload>(payload));
		cellRefs.Add(new WeakReference<TextCell>(cell));

		var renderer = new TextCellRenderer();
		renderer.ParentView = new ContentView { FlowDirection = FlowDirection.LeftToRight };
		((IElementHandler)renderer).SetMauiContext(mauiContext);
		((IElementHandler)renderer).SetVirtualView(cell);
		rendererRefs.Add(new WeakReference<TextCellRenderer>(renderer));

		var nativeView = ((IElementHandler)renderer).PlatformView as AView
			?? throw new InvalidOperationException("Expected Android native cell view.");
		retainedNativeViews.Add(nativeView);
		nativeViewRefs.Add(new WeakReference<AView>(nativeView));

		if (clearNativeCellReference && nativeView is BaseCellView baseCellView)
			BaseCellViewCellField.SetValue(baseCellView, null);

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
