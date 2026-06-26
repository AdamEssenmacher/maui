using System.Runtime.CompilerServices;
using CoreGraphics;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Handlers;
using UIKit;
using CompatListViewRenderer = Microsoft.Maui.Controls.Handlers.Compatibility.ListViewRenderer;

namespace ListViewHeaderDisconnectLeakRepro;

#pragma warning disable CS0618

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadMegabytesPerCycle = 1;
	internal const long PayloadSizeBytes = PayloadMegabytesPerCycle * 1024L * 1024L;
	const string HeaderElementPropertyName = "HeaderElement";
	const string FooterElementPropertyName = "FooterElement";

	public static ReproReport Run(IMauiContext mauiContext)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = RunFooterDisconnectControl(mauiContext);
		var leak = RunHeaderDisconnectGap(mauiContext);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		return new ReproReport(
			Cycles,
			PayloadMegabytesPerCycle,
			baselineBytes,
			finalBytes,
			control,
			leak);
	}

	static ScenarioResult RunFooterDisconnectControl(IMauiContext mauiContext)
	{
		var retainedVirtualViews = new List<PayloadHeaderView>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateFooterControlCycle(mauiContext, retainedVirtualViews, tracked, i);

		ForceFullGc();

		var result = ScenarioResult.From("footer removed through ListViewRenderer.UpdateFooter", tracked);
		GC.KeepAlive(retainedVirtualViews);
		return result;
	}

	static ScenarioResult RunHeaderDisconnectGap(IMauiContext mauiContext)
	{
		var retainedVirtualViews = new List<PayloadHeaderView>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateHeaderLeakCycle(mauiContext, retainedVirtualViews, tracked, i);

		ForceFullGc();

		var result = ScenarioResult.From("header removed through ListViewRenderer.UpdateHeader", tracked);
		GC.KeepAlive(retainedVirtualViews);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateFooterControlCycle(
		IMauiContext mauiContext,
		List<PayloadHeaderView> retainedVirtualViews,
		List<TrackedCycle> tracked,
		int cycle)
	{
		var retainedView = new PayloadHeaderView(cycle);
		retainedVirtualViews.Add(retainedView);

		var listView = CreateListView();
		listView.Footer = retainedView;

		var renderer = AttachListViewRenderer(mauiContext, listView);
		var handler = (PayloadHeaderViewHandler)retainedView.Handler!;
		var nativeView = (PayloadNativeView)handler.PlatformView!;
		var payload = handler.Payload;

		tracked.Add(TrackedCycle.Create(cycle, retainedView, handler, nativeView, payload));

		listView.Footer = null;
		((IElementHandler)renderer).UpdateValue(FooterElementPropertyName);
		DisposeRenderer(renderer);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateHeaderLeakCycle(
		IMauiContext mauiContext,
		List<PayloadHeaderView> retainedVirtualViews,
		List<TrackedCycle> tracked,
		int cycle)
	{
		var retainedView = new PayloadHeaderView(cycle);
		retainedVirtualViews.Add(retainedView);

		var listView = CreateListView();
		listView.Header = retainedView;

		var renderer = AttachListViewRenderer(mauiContext, listView);
		var handler = (PayloadHeaderViewHandler)retainedView.Handler!;
		var nativeView = (PayloadNativeView)handler.PlatformView!;
		var payload = handler.Payload;

		tracked.Add(TrackedCycle.Create(cycle, retainedView, handler, nativeView, payload));

		listView.Header = null;
		((IElementHandler)renderer).UpdateValue(HeaderElementPropertyName);
		DisposeRenderer(renderer);
	}

	static ListView CreateListView()
	{
		return new ListView
		{
			ItemsSource = Array.Empty<string>(),
			RowHeight = 44,
			WidthRequest = 360,
			HeightRequest = 500
		};
	}

	static CompatListViewRenderer AttachListViewRenderer(IMauiContext mauiContext, ListView listView)
	{
		var renderer = new CompatListViewRenderer
		{
			Frame = new CGRect(0, 0, 360, 500)
		};

		var handler = (IElementHandler)renderer;
		handler.SetMauiContext(mauiContext);
		handler.SetVirtualView(listView);
		renderer.LayoutSubviews();
		return renderer;
	}

	static void DisposeRenderer(CompatListViewRenderer renderer)
	{
		((IElementHandler)renderer).DisconnectHandler();
		renderer.Dispose();
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
}

internal sealed class PayloadHeaderView : View
{
	public PayloadHeaderView(int cycle)
	{
		Cycle = cycle;
		HeightRequest = 52;
		WidthRequest = 360;
	}

	public int Cycle { get; }
}

internal sealed class PayloadHeaderViewHandler : ViewHandler<PayloadHeaderView, PayloadNativeView>
{
	public static readonly IPropertyMapper<PayloadHeaderView, PayloadHeaderViewHandler> Mapper =
		new PropertyMapper<PayloadHeaderView, PayloadHeaderViewHandler>(ViewHandler.ViewMapper);

	public PayloadHeaderViewHandler() : base(Mapper)
	{
	}

	public LeakPayload Payload { get; private set; } = null!;

	protected override PayloadNativeView CreatePlatformView()
	{
		Payload = new LeakPayload(VirtualView.Cycle, ReproSession.PayloadSizeBytes);
		return new PayloadNativeView(VirtualView.Cycle);
	}
}

internal sealed class PayloadNativeView : UIView
{
	public PayloadNativeView(int cycle)
	{
		Cycle = cycle;
		Frame = new CGRect(0, 0, 360, 52);
	}

	public int Cycle { get; }
}

internal sealed class LeakPayload
{
	public LeakPayload(int cycle, long payloadBytes)
	{
		Cycle = cycle;
		PayloadBytes = payloadBytes;
		DocumentBytes = new byte[payloadBytes];

		for (var i = 0; i < DocumentBytes.Length; i += 4096)
			DocumentBytes[i] = (byte)(cycle + i);

		CachedRows = Enumerable.Range(1, 40)
			.Select(index => new CachedRow(
				$"ROW-{cycle + 1:000}-{index:000}",
				$"Offline ListView header payload {index}",
				"Ready for reuse"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] DocumentBytes { get; }

	public IReadOnlyList<CachedRow> CachedRows { get; }
}

internal sealed record CachedRow(string Id, string Summary, string Status);

internal sealed record TrackedCycle(
	int Cycle,
	WeakReference RetainedVirtualView,
	WeakReference HeaderOrFooterHandler,
	WeakReference NativeView,
	WeakReference Payload,
	long PayloadBytes)
{
	public static TrackedCycle Create(
		int cycle,
		PayloadHeaderView retainedView,
		PayloadHeaderViewHandler handler,
		PayloadNativeView nativeView,
		LeakPayload payload)
	{
		return new TrackedCycle(
			cycle,
			new WeakReference(retainedView),
			new WeakReference(handler),
			new WeakReference(nativeView),
			new WeakReference(payload),
			payload.PayloadBytes);
	}
}

internal sealed record ScenarioResult(
	string Name,
	int TrackedCycles,
	int AliveRetainedVirtualViews,
	int AliveHandlers,
	int AliveNativeViews,
	int AlivePayloads,
	long RetainedPayloadBytes)
{
	public static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> cycles)
	{
		var aliveRetainedViews = 0;
		var aliveHandlers = 0;
		var aliveNativeViews = 0;
		var alivePayloads = 0;
		long retainedPayloadBytes = 0;

		foreach (var cycle in cycles)
		{
			if (cycle.RetainedVirtualView.IsAlive)
				aliveRetainedViews++;

			if (cycle.HeaderOrFooterHandler.IsAlive)
				aliveHandlers++;

			if (cycle.NativeView.IsAlive)
				aliveNativeViews++;

			if (cycle.Payload.IsAlive)
			{
				alivePayloads++;
				retainedPayloadBytes += cycle.PayloadBytes;
			}
		}

		return new ScenarioResult(
			name,
			cycles.Count,
			aliveRetainedViews,
			aliveHandlers,
			aliveNativeViews,
			alivePayloads,
			retainedPayloadBytes);
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadMegabytesPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ScenarioResult Control,
	ScenarioResult Leak)
{
	public bool LeakProved =>
		Control.AliveRetainedVirtualViews == Control.TrackedCycles &&
		Control.AliveHandlers == 0 &&
		Control.AlivePayloads == 0 &&
		Leak.AliveRetainedVirtualViews == Leak.TrackedCycles &&
		Leak.AliveHandlers == Leak.TrackedCycles &&
		Leak.AlivePayloads == Leak.TrackedCycles;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"ListView header disconnect leak repro",
			$"Cycles: {Cycles}",
			$"Payload per native header view: {PayloadMegabytesPerCycle} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			FormatScenario(Control),
			string.Empty,
			FormatScenario(Leak),
			string.Empty,
			$"Managed heap baseline: {FormatBytes(BaselineManagedBytes)}",
			$"Managed heap final: {FormatBytes(FinalManagedBytes)}",
			$"Managed heap delta: {FormatBytes(FinalManagedBytes - BaselineManagedBytes)}");
	}

	static string FormatScenario(ScenarioResult result)
	{
		var expectedPayload = result.TrackedCycles == 0 ? 0 : result.TrackedCycles * 1024L * 1024L;
		var retainedPercent = expectedPayload == 0 ? 0 : result.RetainedPayloadBytes * 100.0 / expectedPayload;

		return string.Join(Environment.NewLine,
			$"Scenario: {result.Name}",
			$"  Tracked cycles: {result.TrackedCycles}",
			$"  Retained virtual header/footer views alive: {result.AliveRetainedVirtualViews}/{result.TrackedCycles}",
			$"  Header/footer handlers alive: {result.AliveHandlers}/{result.TrackedCycles}",
			$"  Native payload views alive: {result.AliveNativeViews}/{result.TrackedCycles}",
			$"  Payloads alive: {result.AlivePayloads}/{result.TrackedCycles}",
			$"  Retained payload bytes: {FormatBytes(result.RetainedPayloadBytes)} ({retainedPercent:0.0}%)");
	}

	static string FormatBytes(long bytes)
	{
		var sign = bytes < 0 ? "-" : string.Empty;
		var value = Math.Abs(bytes);

		if (value >= 1024L * 1024L * 1024L)
			return $"{sign}{value / 1024d / 1024d / 1024d:0.0} GiB";

		if (value >= 1024L * 1024L)
			return $"{sign}{value / 1024d / 1024d:0.0} MiB";

		if (value >= 1024L)
			return $"{sign}{value / 1024d:0.0} KiB";

		return $"{sign}{value} B";
	}
}

#pragma warning restore CS0618
