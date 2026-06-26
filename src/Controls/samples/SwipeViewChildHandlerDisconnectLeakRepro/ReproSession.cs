using System.Runtime.CompilerServices;
using CoreGraphics;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using UIKit;

namespace SwipeViewChildHandlerDisconnectLeakRepro;

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
		var leak = RunCurrentSwipeContentReplacement(mauiContext);

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
		var retainedRemovedContent = new List<PayloadSwipeContentView>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateControlCycle(mauiContext, retainedRemovedContent, tracked, i);

		ForceFullGc();

		var result = ScenarioResult.From("control: retained removed swipe content after explicit child DisconnectHandler", tracked);
		GC.KeepAlive(retainedRemovedContent);
		return result;
	}

	static ScenarioResult RunCurrentSwipeContentReplacement(IMauiContext mauiContext)
	{
		var retainedRemovedContent = new List<PayloadSwipeContentView>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateLeakCycle(mauiContext, retainedRemovedContent, tracked, i);

		ForceFullGc();

		var result = ScenarioResult.From("current SwipeViewHandler: retained removed swipe content after Content = null", tracked);
		GC.KeepAlive(retainedRemovedContent);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateControlCycle(
		IMauiContext mauiContext,
		List<PayloadSwipeContentView> retainedRemovedContent,
		List<TrackedCycle> tracked,
		int cycle)
	{
		var removedContent = new PayloadSwipeContentView(cycle);
		retainedRemovedContent.Add(removedContent);

		var host = CreateHost(removedContent);
		var hostHandler = AttachHost(mauiContext, host);
		var handler = (PayloadSwipeContentViewHandler)removedContent.Handler!;
		var nativeView = (PayloadNativeView)handler.PlatformView!;
		var payload = handler.Payload;

		tracked.Add(TrackedCycle.Create(cycle, removedContent, handler, nativeView, payload));

		removedContent.Handler?.DisconnectHandler();
		host.Content = null;
		hostHandler.UpdateValue(nameof(ISwipeView.Content));
		hostHandler.DisconnectHandler();
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateLeakCycle(
		IMauiContext mauiContext,
		List<PayloadSwipeContentView> retainedRemovedContent,
		List<TrackedCycle> tracked,
		int cycle)
	{
		var removedContent = new PayloadSwipeContentView(cycle);
		retainedRemovedContent.Add(removedContent);

		var host = CreateHost(removedContent);
		var hostHandler = AttachHost(mauiContext, host);
		var handler = (PayloadSwipeContentViewHandler)removedContent.Handler!;
		var nativeView = (PayloadNativeView)handler.PlatformView!;
		var payload = handler.Payload;

		tracked.Add(TrackedCycle.Create(cycle, removedContent, handler, nativeView, payload));

		host.Content = null;
		hostHandler.UpdateValue(nameof(ISwipeView.Content));
		hostHandler.DisconnectHandler();
	}

	static Microsoft.Maui.Controls.SwipeView CreateHost(PayloadSwipeContentView content)
	{
		return new Microsoft.Maui.Controls.SwipeView
		{
			WidthRequest = 360,
			HeightRequest = 80,
			Content = content
		};
	}

	static IElementHandler AttachHost(IMauiContext mauiContext, Microsoft.Maui.Controls.SwipeView host)
	{
		var handler = host.ToHandler(mauiContext);
		if (handler.PlatformView is UIView view)
			view.Frame = new CGRect(0, 0, 360, 80);

		handler.UpdateValue(nameof(ISwipeView.Content));
		return handler;
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

internal sealed class PayloadSwipeContentView : View
{
	public PayloadSwipeContentView(int cycle)
	{
		Cycle = cycle;
		HeightRequest = 80;
		WidthRequest = 360;
	}

	public int Cycle { get; }
}

internal sealed class PayloadSwipeContentViewHandler : ViewHandler<PayloadSwipeContentView, PayloadNativeView>
{
	public static readonly IPropertyMapper<PayloadSwipeContentView, PayloadSwipeContentViewHandler> Mapper =
		new PropertyMapper<PayloadSwipeContentView, PayloadSwipeContentViewHandler>(ViewHandler.ViewMapper);

	public PayloadSwipeContentViewHandler() : base(Mapper)
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
		Frame = new CGRect(0, 0, 360, 80);
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

		CachedSwipeRows = Enumerable.Range(1, 24)
			.Select(index => new CachedSwipeRow(
				$"SWIPE-{cycle + 1:000}-{index:000}",
				$"Offline swipe content payload {index}",
				"Detached but cached for reuse"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] DocumentBytes { get; }

	public IReadOnlyList<CachedSwipeRow> CachedSwipeRows { get; }
}

internal sealed record CachedSwipeRow(string Id, string Summary, string Status);

internal sealed record TrackedCycle(
	int Cycle,
	WeakReference RetainedVirtualView,
	WeakReference ChildHandler,
	WeakReference NativeView,
	WeakReference Payload,
	long PayloadBytes)
{
	public static TrackedCycle Create(
		int cycle,
		PayloadSwipeContentView retainedView,
		PayloadSwipeContentViewHandler handler,
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
	int AliveChildHandlers,
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

			if (cycle.ChildHandler.IsAlive)
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
		Control.AliveChildHandlers == 0 &&
		Control.AlivePayloads == 0 &&
		Leak.AliveRetainedVirtualViews == Leak.TrackedCycles &&
		Leak.AliveChildHandlers == Leak.TrackedCycles &&
		Leak.AlivePayloads == Leak.TrackedCycles;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"SwipeView child handler disconnect leak repro",
			$"Cycles: {Cycles}",
			$"Payload per removed child handler: {PayloadMegabytesPerCycle} MiB",
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
			$"  Retained removed swipe content views alive: {result.AliveRetainedVirtualViews}/{result.TrackedCycles}",
			$"  Child handlers alive: {result.AliveChildHandlers}/{result.TrackedCycles}",
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
