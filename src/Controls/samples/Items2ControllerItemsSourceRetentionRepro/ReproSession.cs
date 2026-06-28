using System.Collections.ObjectModel;
using System.Reflection;
using Foundation;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Items2;
using UIKit;

namespace Items2ControllerItemsSourceRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadMegabytesPerCycle = 1;

	static readonly List<IReadOnlyList<ReorderableItemsViewController2<ReorderableItemsView>>> RetainedControllerPeers = new();

	static readonly Type ItemsSourceFactoryType = typeof(CollectionViewHandler2).Assembly.GetType(
		"Microsoft.Maui.Controls.Handlers.Items.ItemsSourceFactory",
		throwOnError: true)!;

	static readonly MethodInfo CreateItemsSourceMethod = ItemsSourceFactoryType.GetMethod(
		"Create",
		BindingFlags.Public | BindingFlags.Static)!;

	static readonly PropertyInfo ControllerItemsSourceProperty =
		typeof(ItemsViewController2<ReorderableItemsView>).GetProperty(
			"ItemsSource",
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

	public static readonly string ResultsPath =
		Path.Combine(Path.GetTempPath(), "items2-controller-itemssource-retention-results.txt");

	public static ReproReport Run()
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = RunScenario("control: dispose controller and clear stale ItemsSource property", clearItemsSourceAfterDispose: true);
		var current = RunScenario("current: dispose controller with ItemsSource property still assigned", clearItemsSourceAfterDispose: false);

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

	static ScenarioResult RunScenario(string name, bool clearItemsSourceAfterDispose)
	{
		var tracking = RunScenarioCore(clearItemsSourceAfterDispose);
		RetainedControllerPeers.Add(tracking.Controllers);

		ForceFullGc();

		return ScenarioResult.From(name, tracking.Controllers, tracking.TrackedCycles);
	}

	static ScenarioTracking RunScenarioCore(bool clearItemsSourceAfterDispose)
	{
		var controllers = new List<ReorderableItemsViewController2<ReorderableItemsView>>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateDisposedControllerCycle(i, controllers, tracked, clearItemsSourceAfterDispose);
		}

		return new ScenarioTracking(controllers, tracked);
	}

	static void CreateDisposedControllerCycle(
		int cycle,
		List<ReorderableItemsViewController2<ReorderableItemsView>> controllers,
		List<TrackedCycle> tracked,
		bool clearItemsSourceAfterDispose)
	{
		using var pool = new NSAutoreleasePool();
		var payload = new LeakPayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
		var itemSource = new ObservableCollection<RowItem>
		{
			new(cycle, payload)
		};
		var itemsView = new CollectionView
		{
			ItemsSource = itemSource,
			ItemsLayout = new GridItemsLayout(2, ItemsLayoutOrientation.Vertical)
		};

		var platformLayout = new UICollectionViewFlowLayout();
		var controller = new ReorderableItemsViewController2<ReorderableItemsView>(itemsView, platformLayout);
		var sourceWrapper = CreateItemsSource(itemSource, controller);
		ControllerItemsSourceProperty.SetValue(controller, sourceWrapper);

		controller.Dispose();

		if (clearItemsSourceAfterDispose)
			ControllerItemsSourceProperty.SetValue(controller, null);

		controllers.Add(controller);
		tracked.Add(TrackedCycle.Create(cycle, controller, itemsView, sourceWrapper, itemSource, payload));
	}

	static object CreateItemsSource(IEnumerable<RowItem> itemSource, ReorderableItemsViewController2<ReorderableItemsView> controller)
	{
		return CreateItemsSourceMethod.Invoke(null, new object[] { itemSource, controller })!;
	}

	static object? GetControllerItemsSource(ReorderableItemsViewController2<ReorderableItemsView> controller)
	{
		return ControllerItemsSourceProperty.GetValue(controller);
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

	internal sealed class RowItem
	{
		public RowItem(int cycle, LeakPayload payload)
		{
			Cycle = cycle;
			Payload = payload;
		}

		public int Cycle { get; }

		public LeakPayload Payload { get; }
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

			Rows = Enumerable.Range(1, 24)
				.Select(index => new RowState(
					$"row-{cycle + 1:000}-{index:000}",
					$"Offline item payload {index}",
					$"Filter, image, and selection state {cycle + 1}.{index}"))
				.ToArray();
		}

		public int Cycle { get; }

		public long PayloadBytes { get; }

		public byte[] SessionBytes { get; }

		public IReadOnlyList<RowState> Rows { get; }
	}

	internal sealed record RowState(string Id, string Title, string UiState);

	internal sealed record ScenarioTracking(
		IReadOnlyList<ReorderableItemsViewController2<ReorderableItemsView>> Controllers,
		IReadOnlyList<TrackedCycle> TrackedCycles);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference Controller,
		WeakReference ItemsView,
		WeakReference SourceWrapper,
		WeakReference ItemSource,
		WeakReference Payload,
		long PayloadBytes)
	{
		public static TrackedCycle Create(
			int cycle,
			ReorderableItemsViewController2<ReorderableItemsView> controller,
			CollectionView itemsView,
			object sourceWrapper,
			ObservableCollection<RowItem> itemSource,
			LeakPayload payload)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference(controller),
				new WeakReference(itemsView),
				new WeakReference(sourceWrapper),
				new WeakReference(itemSource),
				new WeakReference(payload),
				payload.PayloadBytes);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int RetainedControllerPeers,
		int TrackedCycles,
		int ControllersWithItemsSourceAssigned,
		int AliveControllers,
		int AliveItemsViews,
		int AliveSourceWrappers,
		int AliveItemSources,
		int AlivePayloads,
		long RetainedPayloadBytes)
	{
		public static ScenarioResult From(
			string name,
			IReadOnlyList<ReorderableItemsViewController2<ReorderableItemsView>> controllers,
			IReadOnlyList<TrackedCycle> cycles)
		{
			var controllersWithItemsSourceAssigned = 0;

			foreach (var controller in controllers)
			{
				if (GetControllerItemsSource(controller) is not null)
					controllersWithItemsSourceAssigned++;
			}

			var aliveControllers = 0;
			var aliveItemsViews = 0;
			var aliveSourceWrappers = 0;
			var aliveItemSources = 0;
			var alivePayloads = 0;
			long retainedPayloadBytes = 0;

			foreach (var cycle in cycles)
			{
				if (cycle.Controller.IsAlive)
					aliveControllers++;
				if (cycle.ItemsView.IsAlive)
					aliveItemsViews++;
				if (cycle.SourceWrapper.IsAlive)
					aliveSourceWrappers++;
				if (cycle.ItemSource.IsAlive)
					aliveItemSources++;
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
				controllersWithItemsSourceAssigned,
				aliveControllers,
				aliveItemsViews,
				aliveSourceWrappers,
				aliveItemSources,
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
		public bool Proven =>
			Control.RetainedControllerPeers == Cycles &&
			Control.AliveControllers == Cycles &&
			Control.ControllersWithItemsSourceAssigned == 0 &&
			Control.AliveItemsViews == 0 &&
			Control.AliveSourceWrappers == 0 &&
			Control.AliveItemSources == 0 &&
			Control.AlivePayloads == 0 &&
			Current.RetainedControllerPeers == Cycles &&
			Current.AliveControllers == Cycles &&
			Current.ControllersWithItemsSourceAssigned == Cycles &&
			Current.AliveItemsViews == 0 &&
			Current.AliveSourceWrappers == Cycles &&
			Current.AliveItemSources == Cycles &&
			Current.AlivePayloads == Cycles;

		public string ToText()
		{
			return string.Join(Environment.NewLine, new[]
			{
				"Items2 controller ItemsSource retention repro",
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
				$"  controllersWithItemsSourceAssigned={result.ControllersWithItemsSourceAssigned}/{result.TrackedCycles}",
				$"  aliveControllers={result.AliveControllers}/{result.TrackedCycles}",
				$"  aliveItemsViews={result.AliveItemsViews}/{result.TrackedCycles}",
				$"  aliveSourceWrappers={result.AliveSourceWrappers}/{result.TrackedCycles}",
				$"  aliveItemSources={result.AliveItemSources}/{result.TrackedCycles}",
				$"  alivePayloads={result.AlivePayloads}/{result.TrackedCycles}",
				$"  retainedPayloadBytes={result.RetainedPayloadBytes}",
				$"  retainedPayloadMiB={result.RetainedPayloadBytes / 1024d / 1024d:F1}",
			});
		}
	}
}
