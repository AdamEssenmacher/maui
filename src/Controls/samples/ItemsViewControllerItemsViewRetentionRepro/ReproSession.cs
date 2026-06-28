using System.Runtime.CompilerServices;
using Foundation;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.iOS;

namespace ItemsViewControllerItemsViewRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadMegabytesPerCycle = 1;

	static readonly List<IReadOnlyList<ItemsViewController<GroupableItemsView>>> RetainedNativeControllerPeers = new();

	public static readonly string ResultsPath =
		Path.Combine(Path.GetTempPath(), "itemsviewcontroller-itemsview-retention-results.txt");

	public static ReproReport Run()
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = RunScenario("control: dispose controller and clear stale ItemsView", clearItemsViewAfterDispose: true);
		var current = RunScenario("current: dispose controller with ItemsView still assigned", clearItemsViewAfterDispose: false);

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

	static ScenarioResult RunScenario(string name, bool clearItemsViewAfterDispose)
	{
		var tracking = RunScenarioCore(clearItemsViewAfterDispose);
		RetainedNativeControllerPeers.Add(tracking.Controllers);

		ForceFullGc();

		return ScenarioResult.From(name, tracking.Controllers, tracking.TrackedCycles);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static ScenarioTracking RunScenarioCore(bool clearItemsViewAfterDispose)
	{
		var controllers = new List<ItemsViewController<GroupableItemsView>>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateDisposedControllerCycle(i, controllers, tracked, clearItemsViewAfterDispose);
		}

		return new ScenarioTracking(controllers, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateDisposedControllerCycle(
		int cycle,
		List<ItemsViewController<GroupableItemsView>> controllers,
		List<TrackedCycle> tracked,
		bool clearItemsViewAfterDispose)
	{
		using var pool = new NSAutoreleasePool();
		var payload = new LeakPayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
		var collectionView = new PayloadCollectionView(cycle, payload)
		{
			ItemsLayout = LinearItemsLayout.Vertical
		};

#pragma warning disable CS0618
		var renderer = new CollectionViewRenderer();
#pragma warning restore CS0618
		renderer.SetElement(collectionView);

		var controller = (ItemsViewController<GroupableItemsView>)renderer.ViewController;
		renderer.Dispose();

		if (clearItemsViewAfterDispose)
			ItemsViewField(controller) = null!;

		controllers.Add(controller);
		tracked.Add(TrackedCycle.Create(cycle, controller, collectionView, payload));
	}

	[UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<ItemsView>k__BackingField")]
	static extern ref GroupableItemsView ItemsViewField(ItemsViewController<GroupableItemsView> controller);

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

sealed class PayloadCollectionView : CollectionView
{
	public PayloadCollectionView(int cycle, LeakPayload payload)
	{
		Cycle = cycle;
		BindingContext = payload;
		AutomationId = $"itemsview-controller-payload-{cycle + 1}";
		EmptyView = $"Orders workspace {cycle + 1}";
	}

	public int Cycle { get; }
}

internal sealed class LeakPayload
{
	public LeakPayload(int cycle, long payloadBytes)
	{
		Cycle = cycle;
		PayloadBytes = payloadBytes;
		SessionBytes = new byte[payloadBytes];

		for (var i = 0; i < SessionBytes.Length; i += 4096)
			SessionBytes[i] = (byte)(cycle + i);

		OpenOrders = Enumerable.Range(1, 24)
			.Select(index => new OrderWorkspace(
				$"ORD-{cycle + 1:000}-{index:000}",
				$"Customer order {index}",
				$"Filter, sort, and realized layout state {cycle + 1}.{index}"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] SessionBytes { get; }

	public IReadOnlyList<OrderWorkspace> OpenOrders { get; }
}

internal sealed record OrderWorkspace(string Id, string Title, string UiState);

internal sealed record ScenarioTracking(
	IReadOnlyList<ItemsViewController<GroupableItemsView>> Controllers,
	IReadOnlyList<TrackedCycle> TrackedCycles);

internal sealed record TrackedCycle(
	int Cycle,
	WeakReference Controller,
	WeakReference CollectionView,
	WeakReference Payload,
	long PayloadBytes)
{
	public static TrackedCycle Create(
		int cycle,
		ItemsViewController<GroupableItemsView> controller,
		PayloadCollectionView collectionView,
		LeakPayload payload)
	{
		return new TrackedCycle(
			cycle,
			new WeakReference(controller),
			new WeakReference(collectionView),
			new WeakReference(payload),
			payload.PayloadBytes);
	}
}

internal sealed record ScenarioResult(
	string Name,
	int RetainedControllerPeers,
	int TrackedCycles,
	int ControllersWithItemsViewAssigned,
	int AliveControllers,
	int AliveCollectionViews,
	int AlivePayloads,
	long RetainedPayloadBytes)
{
	public static ScenarioResult From(
		string name,
		IReadOnlyList<ItemsViewController<GroupableItemsView>> controllers,
		IReadOnlyList<TrackedCycle> cycles)
	{
		var controllersWithItemsViewAssigned = 0;
		foreach (var controller in controllers)
		{
			if (ItemsViewField(controller) is not null)
				controllersWithItemsViewAssigned++;
		}

		var aliveControllers = 0;
		var aliveCollectionViews = 0;
		var alivePayloads = 0;
		long retainedPayloadBytes = 0;

		foreach (var cycle in cycles)
		{
			if (cycle.Controller.IsAlive)
				aliveControllers++;
			if (cycle.CollectionView.IsAlive)
				aliveCollectionViews++;
			if (cycle.Payload.IsAlive)
			{
				alivePayloads++;
				retainedPayloadBytes += cycle.PayloadBytes;
			}
		}

		return new ScenarioResult(
			name,
			controllers.Count,
			cycles.Count,
			controllersWithItemsViewAssigned,
			aliveControllers,
			aliveCollectionViews,
			alivePayloads,
			retainedPayloadBytes);
	}

	[UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<ItemsView>k__BackingField")]
	static extern ref GroupableItemsView ItemsViewField(ItemsViewController<GroupableItemsView> controller);
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadMegabytesPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ScenarioResult Control,
	ScenarioResult Current)
{
	public bool Proven =>
		Control.RetainedControllerPeers == Cycles &&
		Control.AliveControllers == Cycles &&
		Control.ControllersWithItemsViewAssigned == 0 &&
		Control.AliveCollectionViews == 0 &&
		Control.AlivePayloads == 0 &&
		Current.RetainedControllerPeers == Cycles &&
		Current.AliveControllers == Cycles &&
		Current.ControllersWithItemsViewAssigned == Cycles &&
		Current.AliveCollectionViews == Cycles &&
		Current.AlivePayloads == Cycles;

	public string ToText()
	{
		return string.Join(Environment.NewLine, new[]
		{
			"ItemsViewController ItemsView retention repro",
			$"RESULT: {(Proven ? "PROVEN" : "NOT PROVEN")}",
			$"cycles={Cycles}",
			$"payloadMegabytesPerCycle={PayloadMegabytesPerCycle}",
			$"baselineManagedBytes={BaselineManagedBytes}",
			$"finalManagedBytes={FinalManagedBytes}",
			Format(Control),
			Format(Current),
		});
	}

	static string Format(ScenarioResult result)
	{
		return string.Join(Environment.NewLine, new[]
		{
			$"scenario={result.Name}",
			$"  retainedControllerPeers={result.RetainedControllerPeers}",
			$"  trackedCycles={result.TrackedCycles}",
			$"  controllersWithItemsViewAssigned={result.ControllersWithItemsViewAssigned}/{result.TrackedCycles}",
			$"  aliveControllers={result.AliveControllers}/{result.TrackedCycles}",
			$"  aliveCollectionViews={result.AliveCollectionViews}/{result.TrackedCycles}",
			$"  alivePayloads={result.AlivePayloads}/{result.TrackedCycles}",
			$"  retainedPayloadBytes={result.RetainedPayloadBytes}",
			$"  retainedPayloadMiB={result.RetainedPayloadBytes / 1024d / 1024d:F1}",
		});
	}
}
