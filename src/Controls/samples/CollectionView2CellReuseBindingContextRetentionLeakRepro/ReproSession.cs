using System.Reflection;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using CoreGraphics;
using Microsoft.Maui.Controls.Handlers.Items2;
using Microsoft.Maui.Handlers;
using Foundation;
using UIKit;

namespace CollectionView2CellReuseBindingContextRetentionLeakRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadMegabytesPerCycle = 1;
	internal const long PayloadSizeBytes = PayloadMegabytesPerCycle * 1024L * 1024L;

	static readonly MethodInfo UnbindMethod =
		typeof(TemplatedCell2).GetMethod("Unbind", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(TemplatedCell2).FullName, "Unbind");

	static readonly MethodInfo CellDisplayingEndedMethod =
		typeof(ItemsViewController2<CollectionView>).GetMethod("CellDisplayingEndedFromDelegate", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(ItemsViewController2<CollectionView>).FullName, "CellDisplayingEndedFromDelegate");

	public static ReproReport Run(IMauiContext mauiContext)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = RunExplicitUnbindControl(mauiContext);
		var current = RunPrepareForReuseCurrentBehavior(mauiContext);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		return new ReproReport(
			Cycles,
			PayloadMegabytesPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static ScenarioResult RunExplicitUnbindControl(IMauiContext mauiContext)
	{
		var reusePool = new List<TemplatedCell2>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateControlCycle(mauiContext, reusePool, tracked, i);

		ForceFullGc();

		var result = ScenarioResult.From("explicit TemplatedCell2.Unbind after source clear", tracked);
		GC.KeepAlive(reusePool);
		return result;
	}

	static ScenarioResult RunPrepareForReuseCurrentBehavior(IMauiContext mauiContext)
	{
		var reusePool = new List<TemplatedCell2>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateLeakCycle(mauiContext, reusePool, tracked, i);

		ForceFullGc();

		var result = ScenarioResult.From("CellDisplayingEnded keeps a now-cleared item BindingContext attached", tracked);
		GC.KeepAlive(reusePool);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateControlCycle(
		IMauiContext mauiContext,
		List<TemplatedCell2> reusePool,
		List<TrackedCycle> tracked,
		int cycle)
	{
		var itemsView = CreateItemsView(mauiContext);
		var payload = new LeakPayload(cycle, PayloadSizeBytes);
		var source = new ObservableCollection<LeakPayload> { payload };
		itemsView.ItemsSource = source;
		var controller = CreateController(itemsView);
		var cell = CreateBoundCell(payload, itemsView);

		tracked.Add(TrackedCycle.Create(cycle, payload));

		NotifyDisplayEndedWhileItemStillExists(controller, cell);
		source.Clear();
		controller.UpdateItemsSource();
		UnbindMethod.Invoke(cell, Array.Empty<object>());
		reusePool.Add(cell);
		GC.KeepAlive(controller);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateLeakCycle(
		IMauiContext mauiContext,
		List<TemplatedCell2> reusePool,
		List<TrackedCycle> tracked,
		int cycle)
	{
		var itemsView = CreateItemsView(mauiContext);
		var payload = new LeakPayload(cycle, PayloadSizeBytes);
		var source = new ObservableCollection<LeakPayload> { payload };
		itemsView.ItemsSource = source;
		var controller = CreateController(itemsView);
		var cell = CreateBoundCell(payload, itemsView);

		tracked.Add(TrackedCycle.Create(cycle, payload));

		NotifyDisplayEndedWhileItemStillExists(controller, cell);
		source.Clear();
		controller.UpdateItemsSource();
		reusePool.Add(cell);
		GC.KeepAlive(controller);
	}

	static TemplatedCell2 CreateBoundCell(LeakPayload payload, CollectionView itemsView)
	{
		var cell = new TemplatedCell2(new CGRect(0, 0, 360, 64));
		var template = new DataTemplate(() => new PayloadItemView());
		cell.Bind(template, payload, itemsView);
		return cell;
	}

	static SelectableItemsViewController2<CollectionView> CreateController(CollectionView itemsView)
	{
		var layout = new UICollectionViewFlowLayout();
		var controller = new SelectableItemsViewController2<CollectionView>(itemsView, layout);
		controller.LoadView();
		controller.ViewDidLoad();
		return controller;
	}

	static void NotifyDisplayEndedWhileItemStillExists(SelectableItemsViewController2<CollectionView> controller, TemplatedCell2 cell)
	{
		CellDisplayingEndedMethod.Invoke(controller, new object[] { cell, NSIndexPath.FromItemSection(0, 0) });
	}

	static CollectionView CreateItemsView(IMauiContext mauiContext)
	{
		var itemsView = new CollectionView
		{
			WidthRequest = 360,
			HeightRequest = 640,
			ItemsSource = Array.Empty<LeakPayload>()
		};

		var handler = new CollectionViewContextHandler();
		((IElementHandler)handler).SetMauiContext(mauiContext);
		((IElementHandler)handler).SetVirtualView(itemsView);
		return itemsView;
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 6; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(25);
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
		return new UIView(new CGRect(0, 0, 360, 640));
	}
}

internal sealed class PayloadItemView : View
{
	public PayloadItemView()
	{
		HeightRequest = 64;
		WidthRequest = 360;
	}
}

internal sealed class PayloadItemViewHandler : ViewHandler<PayloadItemView, PayloadNativeView>
{
	public static readonly IPropertyMapper<PayloadItemView, PayloadItemViewHandler> Mapper =
		new PropertyMapper<PayloadItemView, PayloadItemViewHandler>(ViewHandler.ViewMapper);

	public PayloadItemViewHandler() : base(Mapper)
	{
	}

	protected override PayloadNativeView CreatePlatformView()
	{
		return new PayloadNativeView();
	}
}

internal sealed class PayloadNativeView : UIView
{
	public PayloadNativeView()
	{
		Frame = new CGRect(0, 0, 360, 64);
	}
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

		CachedRows = Enumerable.Range(1, 50)
			.Select(index => new CachedRow(
				$"ITEM-{cycle + 1:000}-{index:000}",
				$"Offline item payload {index}",
				"Ready"))
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
	WeakReference Payload,
	long PayloadBytes)
{
	public static TrackedCycle Create(int cycle, LeakPayload payload)
	{
		return new TrackedCycle(
			cycle,
			new WeakReference(payload),
			payload.PayloadBytes);
	}
}

internal sealed record ScenarioResult(
	string Name,
	int TrackedCycles,
	int AlivePayloads,
	long RetainedPayloadBytes)
{
	public static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> cycles)
	{
		var alivePayloads = 0;
		long retainedPayloadBytes = 0;

		foreach (var cycle in cycles)
		{
			if (cycle.Payload.IsAlive)
			{
				alivePayloads++;
				retainedPayloadBytes += cycle.PayloadBytes;
			}
		}

		return new ScenarioResult(
			name,
			cycles.Count,
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
	ScenarioResult Current)
{
	public bool LeakProved =>
		Control.AlivePayloads == 0 &&
		Current.AlivePayloads == Current.TrackedCycles;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"CollectionView2 cell reuse BindingContext retention repro",
			$"Cycles: {Cycles}",
			$"Payload per recycled item BindingContext: {PayloadMegabytesPerCycle} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			FormatScenario(Control),
			string.Empty,
			FormatScenario(Current),
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
			$"  Tracked recycled cells: {result.TrackedCycles}",
			$"  Retained item BindingContexts: {result.AlivePayloads}/{result.TrackedCycles}",
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
