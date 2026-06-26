using System.Reflection;
using System.Runtime.CompilerServices;
using CoreGraphics;
using Foundation;
using Microsoft.Maui.Controls.Platform.Compatibility;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using UIKit;

namespace ShellFlyoutHeaderDisconnectLeakRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadMegabytesPerCycle = 1;
	internal const long PayloadSizeBytes = PayloadMegabytesPerCycle * 1024L * 1024L;

	static readonly Type HeaderContainerType =
		typeof(UIContainerView).Assembly.GetType("Microsoft.Maui.Controls.Platform.Compatibility.ShellFlyoutHeaderContainer")
		?? throw new MissingMemberException(typeof(UIContainerView).Assembly.FullName, "ShellFlyoutHeaderContainer");

	static readonly ConstructorInfo HeaderContainerConstructor =
		HeaderContainerType.GetConstructor(
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
			binder: null,
			types: new[] { typeof(View) },
			modifiers: null)
		?? throw new MissingMethodException(HeaderContainerType.FullName, ".ctor(View)");

	public static ReproReport Run(IMauiContext mauiContext)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = RunExplicitChildDisconnectControl(mauiContext);
		var leak = RunCurrentHeaderReplacement(mauiContext);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(mauiContext);

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
		var retainedRemovedHeaders = new List<PayloadFlyoutHeaderView>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateControlCycle(mauiContext, retainedRemovedHeaders, tracked, i);

		ForceFullGc();

		var result = ScenarioResult.From("control: retained removed flyout headers after explicit child DisconnectHandler", tracked);
		GC.KeepAlive(retainedRemovedHeaders);
		return result;
	}

	static ScenarioResult RunCurrentHeaderReplacement(IMauiContext mauiContext)
	{
		var retainedRemovedHeaders = new List<PayloadFlyoutHeaderView>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateLeakCycle(mauiContext, retainedRemovedHeaders, tracked, i);

		ForceFullGc();

		var result = ScenarioResult.From("current Shell flyout header replacement: dispose old header container only", tracked);
		GC.KeepAlive(retainedRemovedHeaders);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateControlCycle(
		IMauiContext mauiContext,
		List<PayloadFlyoutHeaderView> retainedRemovedHeaders,
		List<TrackedCycle> tracked,
		int cycle)
	{
		using var autoreleasePool = new NSAutoreleasePool();

		var header = new PayloadFlyoutHeaderView(cycle);
		retainedRemovedHeaders.Add(header);

		var handler = AttachPayloadHandler(mauiContext, header);
		var nativeView = (PayloadNativeHeaderView)handler.PlatformView!;
		var payload = handler.Payload;
		var container = CreateHeaderContainer(header);

		tracked.Add(TrackedCycle.Create(cycle, header, container, handler, nativeView, payload));

		header.Handler?.DisconnectHandler();
		DisposeHeaderContainer(container);

		container = null!;
		handler = null!;
		header = null!;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateLeakCycle(
		IMauiContext mauiContext,
		List<PayloadFlyoutHeaderView> retainedRemovedHeaders,
		List<TrackedCycle> tracked,
		int cycle)
	{
		using var autoreleasePool = new NSAutoreleasePool();

		var header = new PayloadFlyoutHeaderView(cycle);
		retainedRemovedHeaders.Add(header);

		var handler = AttachPayloadHandler(mauiContext, header);
		var nativeView = (PayloadNativeHeaderView)handler.PlatformView!;
		var payload = handler.Payload;
		var container = CreateHeaderContainer(header);

		tracked.Add(TrackedCycle.Create(cycle, header, container, handler, nativeView, payload));

		DisposeHeaderContainer(container);

		container = null!;
		handler = null!;
		header = null!;
	}

	static PayloadFlyoutHeaderViewHandler AttachPayloadHandler(IMauiContext mauiContext, PayloadFlyoutHeaderView view)
	{
		return (PayloadFlyoutHeaderViewHandler)view.ToHandler(mauiContext);
	}

	static UIContainerView CreateHeaderContainer(PayloadFlyoutHeaderView header)
	{
		var container = (UIContainerView)HeaderContainerConstructor.Invoke(new object[] { header });
		container.Frame = new CGRect(0, 0, 360, 96);
		container.SizeThatFits(new CGSize(360, 96));
		return container;
	}

	static void DisposeHeaderContainer(UIContainerView container)
	{
		container.RemoveFromSuperview();
		container.Dispose();
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

internal sealed class PayloadFlyoutHeaderView : View
{
	public PayloadFlyoutHeaderView(int cycle)
	{
		Cycle = cycle;
		HeightRequest = 96;
		WidthRequest = 360;
	}

	public int Cycle { get; }
}

internal sealed class PayloadFlyoutHeaderViewHandler : ViewHandler<PayloadFlyoutHeaderView, PayloadNativeHeaderView>
{
	public static readonly IPropertyMapper<PayloadFlyoutHeaderView, PayloadFlyoutHeaderViewHandler> Mapper =
		new PropertyMapper<PayloadFlyoutHeaderView, PayloadFlyoutHeaderViewHandler>(ViewHandler.ViewMapper);

	public PayloadFlyoutHeaderViewHandler() : base(Mapper)
	{
	}

	public LeakPayload Payload { get; private set; } = null!;

	protected override PayloadNativeHeaderView CreatePlatformView()
	{
		Payload = new LeakPayload(VirtualView.Cycle, ReproSession.PayloadSizeBytes);
		return new PayloadNativeHeaderView(VirtualView.Cycle);
	}
}

internal sealed class PayloadNativeHeaderView : UIView
{
	public PayloadNativeHeaderView(int cycle)
	{
		Cycle = cycle;
		Frame = new CGRect(0, 0, 360, 96);
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

		HeaderCards = Enumerable.Range(1, 32)
			.Select(index => new HeaderCard(
				$"HDR-{cycle + 1:000}-{index:000}",
				$"Cached Shell flyout header business metric {index}",
				index % 6 == 0 ? "Critical" : "Normal"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] DocumentBytes { get; }

	public IReadOnlyList<HeaderCard> HeaderCards { get; }
}

internal sealed record HeaderCard(string Id, string Summary, string Status);

internal sealed record TrackedCycle(
	int Cycle,
	WeakReference RetainedHeaderView,
	WeakReference HeaderContainer,
	WeakReference HeaderHandler,
	WeakReference NativeView,
	WeakReference Payload,
	long PayloadBytes)
{
	public static TrackedCycle Create(
		int cycle,
		PayloadFlyoutHeaderView retainedHeader,
		UIContainerView container,
		PayloadFlyoutHeaderViewHandler handler,
		PayloadNativeHeaderView nativeView,
		LeakPayload payload)
	{
		return new TrackedCycle(
			cycle,
			new WeakReference(retainedHeader),
			new WeakReference(container),
			new WeakReference(handler),
			new WeakReference(nativeView),
			new WeakReference(payload),
			payload.PayloadBytes);
	}
}

internal sealed record ScenarioResult(
	string Name,
	int TrackedCycles,
	int AliveRetainedHeaderViews,
	int AliveHeaderContainers,
	int AliveHeaderHandlers,
	int AliveNativeViews,
	int AlivePayloads,
	long RetainedPayloadBytes)
{
	public static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> cycles)
	{
		var aliveRetainedHeaders = 0;
		var aliveContainers = 0;
		var aliveHandlers = 0;
		var aliveNativeViews = 0;
		var alivePayloads = 0;
		long retainedPayloadBytes = 0;

		foreach (var cycle in cycles)
		{
			if (cycle.RetainedHeaderView.IsAlive)
				aliveRetainedHeaders++;

			if (cycle.HeaderContainer.IsAlive)
				aliveContainers++;

			if (cycle.HeaderHandler.IsAlive)
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
			aliveRetainedHeaders,
			aliveContainers,
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
		Control.AliveRetainedHeaderViews == Control.TrackedCycles &&
		Control.AliveHeaderHandlers == 0 &&
		Control.AlivePayloads == 0 &&
		Leak.AliveRetainedHeaderViews == Leak.TrackedCycles &&
		Leak.AliveHeaderHandlers == Leak.TrackedCycles &&
		Leak.AlivePayloads == Leak.TrackedCycles;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"Shell flyout header disconnect leak repro",
			$"Cycles: {Cycles}",
			$"Payload per removed flyout header handler: {PayloadMegabytesPerCycle} MiB",
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
		var expectedPayload = result.TrackedCycles == 0 ? 0 : result.TrackedCycles * ReproSession.PayloadSizeBytes;
		var retainedPercent = expectedPayload == 0 ? 0 : result.RetainedPayloadBytes * 100.0 / expectedPayload;

		return string.Join(Environment.NewLine,
			$"Scenario: {result.Name}",
			$"  Tracked cycles: {result.TrackedCycles}",
			$"  Retained removed header views alive: {result.AliveRetainedHeaderViews}/{result.TrackedCycles}",
			$"  Disposed header containers alive: {result.AliveHeaderContainers}/{result.TrackedCycles}",
			$"  Header handlers alive: {result.AliveHeaderHandlers}/{result.TrackedCycles}",
			$"  Native header views alive: {result.AliveNativeViews}/{result.TrackedCycles}",
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
