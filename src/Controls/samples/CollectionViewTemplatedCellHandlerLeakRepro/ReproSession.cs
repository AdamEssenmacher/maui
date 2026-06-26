using System.Reflection;
using System.Runtime.CompilerServices;
using CoreGraphics;
using Microsoft.Maui.Controls.Handlers.Items;
using Microsoft.Maui.Handlers;
using UIKit;

namespace CollectionViewTemplatedCellHandlerLeakRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadMegabytesPerCycle = 1;
	internal const long PayloadSizeBytes = PayloadMegabytesPerCycle * 1024L * 1024L;

	static readonly ConstructorInfo CellConstructor =
		typeof(CarouselTemplatedCell).GetConstructor(
			BindingFlags.Instance | BindingFlags.NonPublic,
			binder: null,
			new[] { typeof(CGRect) },
			modifiers: null)
		?? throw new MissingMethodException(typeof(CarouselTemplatedCell).FullName, ".ctor(CGRect)");

	public static ReproReport Run(IMauiContext mauiContext)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = RunExplicitDisconnectControl(mauiContext);
		var leak = RunTemplatedCellReplacementGap(mauiContext);

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

	static ScenarioResult RunExplicitDisconnectControl(IMauiContext mauiContext)
	{
		var retainedItemsViews = new List<CollectionView>();
		var retainedOldViews = new List<PayloadItemView>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateControlCycle(mauiContext, retainedItemsViews, retainedOldViews, tracked, i);

		ForceFullGc();

		var result = ScenarioResult.From("explicit old item handler disconnect before template replacement", tracked);
		GC.KeepAlive(retainedItemsViews);
		GC.KeepAlive(retainedOldViews);
		return result;
	}

	static ScenarioResult RunTemplatedCellReplacementGap(IMauiContext mauiContext)
	{
		var retainedItemsViews = new List<CollectionView>();
		var retainedOldViews = new List<PayloadItemView>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateLeakCycle(mauiContext, retainedItemsViews, retainedOldViews, tracked, i);

		ForceFullGc();

		var result = ScenarioResult.From("TemplatedCell item-template replacement without old-handler disconnect", tracked);
		GC.KeepAlive(retainedItemsViews);
		GC.KeepAlive(retainedOldViews);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateControlCycle(
		IMauiContext mauiContext,
		List<CollectionView> retainedItemsViews,
		List<PayloadItemView> retainedOldViews,
		List<TrackedCycle> tracked,
		int cycle)
	{
		var itemsView = CreateItemsView(mauiContext);
		retainedItemsViews.Add(itemsView);

		var cell = CreateCell();
		var oldTemplate = CreateTemplate(cycle, "old item", view => retainedOldViews.Add(view));
		var newTemplate = CreateTemplate(cycle, "new item", _ => { });

		cell.Bind(oldTemplate, new ItemModel(cycle, "old"), itemsView);
		var oldView = retainedOldViews[^1];
		var oldHandler = (PayloadItemViewHandler)oldView.Handler!;

		tracked.Add(TrackedCycle.Create(cycle, oldView, oldHandler, (PayloadNativeView)oldHandler.PlatformView!, oldHandler.Payload));

		((IElementHandler)oldHandler).DisconnectHandler();
		cell.Bind(newTemplate, new ItemModel(cycle, "new"), itemsView);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateLeakCycle(
		IMauiContext mauiContext,
		List<CollectionView> retainedItemsViews,
		List<PayloadItemView> retainedOldViews,
		List<TrackedCycle> tracked,
		int cycle)
	{
		var itemsView = CreateItemsView(mauiContext);
		retainedItemsViews.Add(itemsView);

		var cell = CreateCell();
		var oldTemplate = CreateTemplate(cycle, "old item", view => retainedOldViews.Add(view));
		var newTemplate = CreateTemplate(cycle, "new item", _ => { });

		cell.Bind(oldTemplate, new ItemModel(cycle, "old"), itemsView);
		var oldView = retainedOldViews[^1];
		var oldHandler = (PayloadItemViewHandler)oldView.Handler!;

		tracked.Add(TrackedCycle.Create(cycle, oldView, oldHandler, (PayloadNativeView)oldHandler.PlatformView!, oldHandler.Payload));

		cell.Bind(newTemplate, new ItemModel(cycle, "new"), itemsView);
	}

	static TemplatedCell CreateCell()
	{
		return (TemplatedCell)CellConstructor.Invoke(new object[] { new CGRect(0, 0, 360, 56) });
	}

	static DataTemplate CreateTemplate(int cycle, string role, Action<PayloadItemView> capture)
	{
		return new DataTemplate(() =>
		{
			var view = new PayloadItemView(cycle, role);
			capture(view);
			return view;
		});
	}

	static CollectionView CreateItemsView(IMauiContext mauiContext)
	{
		var itemsView = new CollectionView
		{
			WidthRequest = 360,
			HeightRequest = 600,
			ItemsSource = Array.Empty<string>()
		};

		var handler = new CollectionViewContextHandler();
		((IElementHandler)handler).SetMauiContext(mauiContext);
		((IElementHandler)handler).SetVirtualView(itemsView);
		return itemsView;
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

internal sealed class CollectionViewContextHandler : ViewHandler<CollectionView, UIView>
{
	public static readonly IPropertyMapper<CollectionView, CollectionViewContextHandler> Mapper =
		new PropertyMapper<CollectionView, CollectionViewContextHandler>(ViewHandler.ViewMapper);

	public CollectionViewContextHandler() : base(Mapper)
	{
	}

	protected override UIView CreatePlatformView()
	{
		return new UIView(new CGRect(0, 0, 360, 600));
	}
}

internal sealed class PayloadItemView : View
{
	public PayloadItemView(int cycle, string role)
	{
		Cycle = cycle;
		Role = role;
		HeightRequest = 56;
		WidthRequest = 360;
	}

	public int Cycle { get; }

	public string Role { get; }
}

internal sealed class PayloadItemViewHandler : ViewHandler<PayloadItemView, PayloadNativeView>
{
	public static readonly IPropertyMapper<PayloadItemView, PayloadItemViewHandler> Mapper =
		new PropertyMapper<PayloadItemView, PayloadItemViewHandler>(ViewHandler.ViewMapper);

	public PayloadItemViewHandler() : base(Mapper)
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
		Frame = new CGRect(0, 0, 360, 56);
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
				$"Offline CollectionView item payload {index}",
				"Ready for reuse"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] DocumentBytes { get; }

	public IReadOnlyList<CachedRow> CachedRows { get; }
}

internal sealed record CachedRow(string Id, string Summary, string Status);

internal sealed record ItemModel(int Cycle, string Name);

internal sealed record TrackedCycle(
	int Cycle,
	WeakReference RetainedOldView,
	WeakReference OldHandler,
	WeakReference NativeView,
	WeakReference Payload,
	long PayloadBytes)
{
	public static TrackedCycle Create(
		int cycle,
		PayloadItemView retainedView,
		PayloadItemViewHandler handler,
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
	int AliveRetainedOldViews,
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
			if (cycle.RetainedOldView.IsAlive)
				aliveRetainedViews++;

			if (cycle.OldHandler.IsAlive)
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
		Control.AliveRetainedOldViews == Control.TrackedCycles &&
		Control.AliveHandlers == 0 &&
		Control.AlivePayloads == 0 &&
		Leak.AliveRetainedOldViews == Leak.TrackedCycles &&
		Leak.AliveHandlers == Leak.TrackedCycles &&
		Leak.AlivePayloads == Leak.TrackedCycles;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"CollectionView TemplatedCell handler leak repro",
			$"Cycles: {Cycles}",
			$"Payload per removed item handler: {PayloadMegabytesPerCycle} MiB",
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
			$"  Tracked removed item views: {result.TrackedCycles}",
			$"  Retained removed item views alive: {result.AliveRetainedOldViews}/{result.TrackedCycles}",
			$"  Removed item handlers alive: {result.AliveHandlers}/{result.TrackedCycles}",
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
