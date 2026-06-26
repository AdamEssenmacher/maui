using System.Runtime.CompilerServices;
using CoreGraphics;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Handlers;
using UIKit;
using CompatListViewRenderer = Microsoft.Maui.Controls.Handlers.Compatibility.ListViewRenderer;

namespace ListViewCleanupHeaderFooterLeakRepro;

#pragma warning disable CS0618

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadMegabytesPerCycle = 1;
	internal const long PayloadSizeBytes = PayloadMegabytesPerCycle * 1024L * 1024L;
	public static ReproReport Run(IMauiContext mauiContext)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = RunExplicitChildDisconnectControl(mauiContext);
		var leak = RunListViewCleanupDisconnectGap(mauiContext);

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

	static ScenarioResult RunExplicitChildDisconnectControl(IMauiContext mauiContext)
	{
		var retainedListViews = new List<ListView>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateControlCycle(mauiContext, retainedListViews, tracked, i);

		ForceFullGc();

		var result = ScenarioResult.From("explicit child-handler disconnect before ListView cleanup", tracked);
		GC.KeepAlive(retainedListViews);
		return result;
	}

	static ScenarioResult RunListViewCleanupDisconnectGap(IMauiContext mauiContext)
	{
		var retainedListViews = new List<ListView>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateLeakCycle(mauiContext, retainedListViews, tracked, i);

		ForceFullGc();

		var result = ScenarioResult.From("ListViewRenderer.CleanUpResources without child-handler disconnect", tracked);
		GC.KeepAlive(retainedListViews);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateControlCycle(
		IMauiContext mauiContext,
		List<ListView> retainedListViews,
		List<TrackedCycle> tracked,
		int cycle)
	{
		var header = new PayloadHeaderView(cycle, "header");
		var footer = new PayloadHeaderView(cycle, "footer");
		var listView = CreateListView(header, footer);
		retainedListViews.Add(listView);

		var renderer = AttachListViewRenderer(mauiContext, listView);
		var headerHandler = (PayloadHeaderViewHandler)header.Handler!;
		var footerHandler = (PayloadHeaderViewHandler)footer.Handler!;

		tracked.Add(TrackedCycle.Create(cycle, header, headerHandler, (PayloadNativeView)headerHandler.PlatformView!, headerHandler.Payload));
		tracked.Add(TrackedCycle.Create(cycle, footer, footerHandler, (PayloadNativeView)footerHandler.PlatformView!, footerHandler.Payload));

		((IElementHandler)headerHandler).DisconnectHandler();
		((IElementHandler)footerHandler).DisconnectHandler();
		DisposeRenderer(renderer);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateLeakCycle(
		IMauiContext mauiContext,
		List<ListView> retainedListViews,
		List<TrackedCycle> tracked,
		int cycle)
	{
		var header = new PayloadHeaderView(cycle, "header");
		var footer = new PayloadHeaderView(cycle, "footer");
		var listView = CreateListView(header, footer);
		retainedListViews.Add(listView);

		var renderer = AttachListViewRenderer(mauiContext, listView);
		var headerHandler = (PayloadHeaderViewHandler)header.Handler!;
		var footerHandler = (PayloadHeaderViewHandler)footer.Handler!;

		tracked.Add(TrackedCycle.Create(cycle, header, headerHandler, (PayloadNativeView)headerHandler.PlatformView!, headerHandler.Payload));
		tracked.Add(TrackedCycle.Create(cycle, footer, footerHandler, (PayloadNativeView)footerHandler.PlatformView!, footerHandler.Payload));
		DisposeRenderer(renderer);
	}

	static ListView CreateListView(PayloadHeaderView header, PayloadHeaderView footer)
	{
		return new ListView
		{
			ItemsSource = Array.Empty<string>(),
			Header = header,
			Footer = footer,
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
	public PayloadHeaderView(int cycle, string role)
	{
		Cycle = cycle;
		Role = role;
		HeightRequest = 52;
		WidthRequest = 360;
	}

	public int Cycle { get; }

	public string Role { get; }
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
		return new PayloadNativeView(VirtualView.Cycle, VirtualView.Role);
	}
}

internal sealed class PayloadNativeView : UIView
{
	public PayloadNativeView(int cycle, string role)
	{
		Cycle = cycle;
		Role = role;
		Frame = new CGRect(0, 0, 360, 52);
	}

	public int Cycle { get; }

	public string Role { get; }
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
			"ListView cleanup header/footer disconnect leak repro",
			$"Cycles: {Cycles}",
			$"Payload per header/footer handler: {PayloadMegabytesPerCycle} MiB",
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
