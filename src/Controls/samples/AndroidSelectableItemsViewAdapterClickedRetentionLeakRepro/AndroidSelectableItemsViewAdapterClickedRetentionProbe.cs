using System.Reflection;
using System.Runtime.CompilerServices;
using Android.Views;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Items;
using AView = Android.Views.View;

namespace AndroidSelectableItemsViewAdapterClickedRetentionLeakRepro;

static class AndroidSelectableItemsViewAdapterClickedRetentionProbe
{
	const int Iterations = 80;
	const int BindsPerHolder = 12;
	const int PayloadBytes = 1024 * 1024;

	static readonly FieldInfo CurrentViewHoldersField =
		typeof(SelectableItemsViewAdapter<CollectionView, IItemsViewSource>).GetField("_currentViewHolders", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new InvalidOperationException("Could not find SelectableItemsViewAdapter._currentViewHolders.");

	static readonly FieldInfo ClickedEventField =
		typeof(SelectableViewHolder).GetField("Clicked", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new InvalidOperationException("Could not find SelectableViewHolder.Clicked backing field.");

	public static ProbeResult Run()
	{
		var controlRefs = new List<ScenarioRefs>(Iterations);
		var currentRefs = new List<ScenarioRefs>(Iterations);
		var controlHolderRoots = new List<ProbeSelectableViewHolder>(Iterations);
		var currentHolderRoots = new List<ProbeSelectableViewHolder>(Iterations);
		var controlStaleHolderEntries = 0;
		var currentStaleHolderEntries = 0;
		var controlStaleEventHandlers = 0;
		var currentStaleEventHandlers = 0;

		for (var i = 0; i < Iterations; i++)
		{
			var scenario = CreateControlScenario(i);
			controlRefs.Add(scenario.Refs);
			controlHolderRoots.Add(scenario.HolderRoot);
			controlStaleHolderEntries += scenario.StaleHolderEntries;
			controlStaleEventHandlers += scenario.StaleEventHandlers;
		}

		for (var i = 0; i < Iterations; i++)
		{
			var scenario = CreateCurrentScenario(i + Iterations);
			currentRefs.Add(scenario.Refs);
			currentHolderRoots.Add(scenario.HolderRoot);
			currentStaleHolderEntries += scenario.StaleHolderEntries;
			currentStaleEventHandlers += scenario.StaleEventHandlers;
		}

		ForceCollect();
		GC.KeepAlive(controlHolderRoots);
		GC.KeepAlive(currentHolderRoots);

		return new ProbeResult(
			Iterations,
			BindsPerHolder,
			PayloadBytes,
			controlStaleHolderEntries,
			currentStaleHolderEntries,
			controlStaleEventHandlers,
			currentStaleEventHandlers,
			CountAlive(controlRefs, static r => r.Adapter),
			CountAlive(controlRefs, static r => r.ItemsView),
			CountAlive(controlRefs, static r => r.Payload),
			CountAlive(currentRefs, static r => r.Adapter),
			CountAlive(currentRefs, static r => r.ItemsView),
			CountAlive(currentRefs, static r => r.Payload),
			GC.GetTotalMemory(forceFullCollection: true));
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static Scenario CreateControlScenario(int id)
	{
		var holder = new ProbeSelectableViewHolder(new AView(Android.App.Application.Context), isSelectionEnabled: true);
		var payload = new Payload(id, PayloadBytes);
		var itemsView = CreateCollectionView(payload);
		var adapter = new ProbeAdapter(itemsView);

		for (var i = 0; i < BindsPerHolder; i++)
			adapter.OnBindViewHolder(holder, 0);

		for (var i = 0; i < BindsPerHolder; i++)
			adapter.OnViewRecycled(holder);

		var refs = CreateRefs(adapter, itemsView, payload);
		var staleHolderEntries = GetCurrentViewHolderCount(adapter);
		var staleEventHandlers = GetClickedHandlerCount(holder);

		adapter.Dispose();

		return new Scenario(refs, holder, staleHolderEntries, staleEventHandlers);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static Scenario CreateCurrentScenario(int id)
	{
		var holder = new ProbeSelectableViewHolder(new AView(Android.App.Application.Context), isSelectionEnabled: true);
		var payload = new Payload(id, PayloadBytes);
		var itemsView = CreateCollectionView(payload);
		var adapter = new ProbeAdapter(itemsView);

		for (var i = 0; i < BindsPerHolder; i++)
			adapter.OnBindViewHolder(holder, 0);

		adapter.OnViewRecycled(holder);

		var refs = CreateRefs(adapter, itemsView, payload);
		var staleHolderEntries = GetCurrentViewHolderCount(adapter);
		var staleEventHandlers = GetClickedHandlerCount(holder);

		adapter.Dispose();

		return new Scenario(refs, holder, staleHolderEntries, staleEventHandlers);
	}

	static CollectionView CreateCollectionView(Payload payload) =>
		new()
		{
			BindingContext = payload,
			ItemsSource = new[] { payload },
			SelectionMode = SelectionMode.Single
		};

	static ScenarioRefs CreateRefs(ProbeAdapter adapter, CollectionView itemsView, Payload payload) =>
		new(
			new WeakReference<ProbeAdapter>(adapter),
			new WeakReference<CollectionView>(itemsView),
			new WeakReference<Payload>(payload));

	static int GetCurrentViewHolderCount(ProbeAdapter adapter)
	{
		var list = (System.Collections.ICollection)CurrentViewHoldersField.GetValue(adapter)!;
		return list.Count;
	}

	static int GetClickedHandlerCount(ProbeSelectableViewHolder holder)
	{
		var handlers = (Delegate?)ClickedEventField.GetValue(holder);
		return handlers?.GetInvocationList().Length ?? 0;
	}

	static int CountAlive<T>(List<ScenarioRefs> refs, Func<ScenarioRefs, WeakReference<T>> selector)
		where T : class
	{
		var count = 0;
		foreach (var item in refs)
		{
			if (selector(item).TryGetTarget(out _))
				count++;
		}

		return count;
	}

	static void ForceCollect()
	{
		for (var i = 0; i < 8; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(50);
		}
	}

	sealed class ProbeAdapter : SelectableItemsViewAdapter<CollectionView, IItemsViewSource>
	{
		public ProbeAdapter(CollectionView itemsView)
			: base(itemsView)
		{
		}
	}

	sealed class ProbeSelectableViewHolder : SelectableViewHolder
	{
		public ProbeSelectableViewHolder(AView itemView, bool isSelectionEnabled)
			: base(itemView, isSelectionEnabled)
		{
		}
	}

	sealed class Payload
	{
		readonly byte[] _bytes;

		public Payload(int id, int size)
		{
			Id = id;
			_bytes = new byte[size];
			_bytes[0] = (byte)(id % 251);
			_bytes[^1] = (byte)((id + 17) % 251);
		}

		public int Id { get; }
	}

	sealed record Scenario(ScenarioRefs Refs, ProbeSelectableViewHolder HolderRoot, int StaleHolderEntries, int StaleEventHandlers);

	sealed record ScenarioRefs(
		WeakReference<ProbeAdapter> Adapter,
		WeakReference<CollectionView> ItemsView,
		WeakReference<Payload> Payload);
}

sealed record ProbeResult(
	int Iterations,
	int BindsPerHolder,
	int PayloadBytes,
	int ControlStaleHolderEntries,
	int CurrentStaleHolderEntries,
	int ControlStaleEventHandlers,
	int CurrentStaleEventHandlers,
	int ControlAdaptersRetained,
	int ControlItemsViewsRetained,
	int ControlPayloadsRetained,
	int CurrentAdaptersRetained,
	int CurrentItemsViewsRetained,
	int CurrentPayloadsRetained,
	long ManagedHeapBytes)
{
	public bool ProvedLeak =>
		ControlStaleHolderEntries == 0 &&
		ControlStaleEventHandlers == 0 &&
		CurrentStaleHolderEntries == Iterations * (BindsPerHolder - 1) &&
		CurrentStaleEventHandlers == Iterations * (BindsPerHolder - 1) &&
		ControlAdaptersRetained == 0 &&
		ControlItemsViewsRetained == 0 &&
		ControlPayloadsRetained == 0 &&
		CurrentAdaptersRetained == Iterations &&
		CurrentItemsViewsRetained == Iterations &&
		CurrentPayloadsRetained == Iterations;

	public string ToReport()
	{
		var retainedPayloadMiB = CurrentPayloadsRetained * PayloadBytes / 1024.0 / 1024.0;
		var heapMiB = ManagedHeapBytes / 1024.0 / 1024.0;

		return string.Join(Environment.NewLine, new[]
		{
			"AndroidSelectableItemsViewAdapterClickedRetentionLeakRepro",
			$"Iterations: {Iterations}",
			$"Binds per holder: {BindsPerHolder}",
			$"Payload bytes per scenario: {PayloadBytes}",
			$"Control stale _currentViewHolders entries: {ControlStaleHolderEntries}",
			$"Current stale _currentViewHolders entries: {CurrentStaleHolderEntries}",
			$"Control stale Clicked handlers: {ControlStaleEventHandlers}",
			$"Current stale Clicked handlers: {CurrentStaleEventHandlers}",
			$"Control retained adapters: {ControlAdaptersRetained}/{Iterations}",
			$"Control retained CollectionViews: {ControlItemsViewsRetained}/{Iterations}",
			$"Control retained payloads: {ControlPayloadsRetained}/{Iterations}",
			$"Current retained adapters: {CurrentAdaptersRetained}/{Iterations}",
			$"Current retained CollectionViews: {CurrentItemsViewsRetained}/{Iterations}",
			$"Current retained payloads: {CurrentPayloadsRetained}/{Iterations}",
			$"Retained payload estimate: {retainedPayloadMiB:F1} MiB",
			$"Managed heap after proof: {heapMiB:F1} MiB",
			$"Proved leak: {ProvedLeak}"
		});
	}
}
