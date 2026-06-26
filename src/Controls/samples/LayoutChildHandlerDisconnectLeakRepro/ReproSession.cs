using System.Runtime.CompilerServices;
using CoreGraphics;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using UIKit;

namespace LayoutChildHandlerDisconnectLeakRepro;

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
		var leak = RunCurrentLayoutChildRemoval(mauiContext);

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
		var retainedRemovedChildren = new List<PayloadLayoutChildView>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateControlCycle(mauiContext, retainedRemovedChildren, tracked, i);

		ForceFullGc();

		var result = ScenarioResult.From("control: retained removed layout children after Children.Remove plus explicit child DisconnectHandler", tracked);
		GC.KeepAlive(retainedRemovedChildren);
		return result;
	}

	static ScenarioResult RunCurrentLayoutChildRemoval(IMauiContext mauiContext)
	{
		var retainedRemovedChildren = new List<PayloadLayoutChildView>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateLeakCycle(mauiContext, retainedRemovedChildren, tracked, i);

		ForceFullGc();

		var result = ScenarioResult.From("current LayoutHandler: retained removed layout children after Children.Remove", tracked);
		GC.KeepAlive(retainedRemovedChildren);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateControlCycle(
		IMauiContext mauiContext,
		List<PayloadLayoutChildView> retainedRemovedChildren,
		List<TrackedCycle> tracked,
		int cycle)
	{
		var removedChild = new PayloadLayoutChildView(cycle);
		retainedRemovedChildren.Add(removedChild);

		var host = CreateHost(removedChild);
		var hostHandler = AttachHost(mauiContext, host);
		var handler = (PayloadLayoutChildViewHandler)removedChild.Handler!;
		var nativeView = (PayloadNativeView)handler.PlatformView!;
		var payload = handler.Payload;

		tracked.Add(TrackedCycle.Create(cycle, removedChild, handler, nativeView, payload));

		host.Children.Remove(removedChild);
		removedChild.Handler?.DisconnectHandler();
		hostHandler.DisconnectHandler();
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateLeakCycle(
		IMauiContext mauiContext,
		List<PayloadLayoutChildView> retainedRemovedChildren,
		List<TrackedCycle> tracked,
		int cycle)
	{
		var removedChild = new PayloadLayoutChildView(cycle);
		retainedRemovedChildren.Add(removedChild);

		var host = CreateHost(removedChild);
		var hostHandler = AttachHost(mauiContext, host);
		var handler = (PayloadLayoutChildViewHandler)removedChild.Handler!;
		var nativeView = (PayloadNativeView)handler.PlatformView!;
		var payload = handler.Payload;

		tracked.Add(TrackedCycle.Create(cycle, removedChild, handler, nativeView, payload));

		host.Children.Remove(removedChild);
		hostHandler.DisconnectHandler();
	}

	static VerticalStackLayout CreateHost(PayloadLayoutChildView child)
	{
		return new VerticalStackLayout
		{
			WidthRequest = 360,
			HeightRequest = 80,
			Children =
			{
				child
			}
		};
	}

	static IElementHandler AttachHost(IMauiContext mauiContext, VerticalStackLayout host)
	{
		var handler = host.ToHandler(mauiContext);
		if (handler.PlatformView is UIView view)
			view.Frame = new CGRect(0, 0, 360, 80);

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

internal sealed class PayloadLayoutChildView : View
{
	public PayloadLayoutChildView(int cycle)
	{
		Cycle = cycle;
		HeightRequest = 80;
		WidthRequest = 360;
	}

	public int Cycle { get; }
}

internal sealed class PayloadLayoutChildViewHandler : ViewHandler<PayloadLayoutChildView, PayloadNativeView>
{
	public static readonly IPropertyMapper<PayloadLayoutChildView, PayloadLayoutChildViewHandler> Mapper =
		new PropertyMapper<PayloadLayoutChildView, PayloadLayoutChildViewHandler>(ViewHandler.ViewMapper);

	public PayloadLayoutChildViewHandler() : base(Mapper)
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

		CachedCards = Enumerable.Range(1, 24)
			.Select(index => new CachedCard(
				$"CARD-{cycle + 1:000}-{index:000}",
				$"Offline Layout child payload {index}",
				"Detached but cached for reuse"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] DocumentBytes { get; }

	public IReadOnlyList<CachedCard> CachedCards { get; }
}

internal sealed record CachedCard(string Id, string Summary, string Status);

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
		PayloadLayoutChildView retainedView,
		PayloadLayoutChildViewHandler handler,
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
			"Layout child handler disconnect leak repro",
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
			$"  Retained removed layout children alive: {result.AliveRetainedVirtualViews}/{result.TrackedCycles}",
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
