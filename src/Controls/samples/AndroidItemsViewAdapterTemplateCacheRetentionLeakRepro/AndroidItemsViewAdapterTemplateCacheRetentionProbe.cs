using System.Reflection;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Items;

namespace AndroidItemsViewAdapterTemplateCacheRetentionLeakRepro;

static class AndroidItemsViewAdapterTemplateCacheRetentionProbe
{
	const int Iterations = 80;
	const int PayloadBytes = 1024 * 1024;
	const int ItemsPerSelector = 3;

	static readonly FieldInfo ViewTypeTemplateCacheField =
		typeof(ItemsViewAdapter<CollectionView, IItemsViewSource>).GetField("_viewTypeDataTemplates", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new InvalidOperationException("Could not find ItemsViewAdapter._viewTypeDataTemplates.");

	public static ProbeResult Run()
	{
		var controlView = CreateCollectionView();
		var currentView = CreateCollectionView();
		var controlAdapter = new ProbeAdapter(controlView);
		var currentAdapter = new ProbeAdapter(currentView);
		var controlRefs = new List<ScenarioRefs>(Iterations);
		var currentRefs = new List<ScenarioRefs>(Iterations);

		for (var i = 0; i < Iterations; i++)
			controlRefs.Add(CreateControlScenario(controlView, controlAdapter, i));

		for (var i = 0; i < Iterations; i++)
			currentRefs.Add(CreateCurrentScenario(currentView, currentAdapter, i));

		ForceCollect();

		return new ProbeResult(
			Iterations,
			PayloadBytes,
			ItemsPerSelector,
			GetTemplateCacheCount(controlAdapter),
			GetTemplateCacheCount(currentAdapter),
			CountAlive(controlRefs, static r => r.Selector),
			CountAlive(controlRefs, static r => r.Template),
			CountAlive(controlRefs, static r => r.Payload),
			CountAlive(currentRefs, static r => r.Selector),
			CountAlive(currentRefs, static r => r.Template),
			CountAlive(currentRefs, static r => r.Payload),
			GC.GetTotalMemory(forceFullCollection: true));
	}

	static CollectionView CreateCollectionView()
	{
		return new CollectionView
		{
			ItemsSource = Enumerable.Range(0, ItemsPerSelector).Select(i => new ItemModel(i)).ToArray()
		};
	}

	static ScenarioRefs CreateControlScenario(CollectionView view, ProbeAdapter adapter, int index)
	{
		var payload = new Payload(index, PayloadBytes);
		var selector = new PayloadTemplateSelector(index, payload);
		view.ItemTemplate = selector;

		for (var position = 0; position < ItemsPerSelector; position++)
			_ = adapter.GetItemViewType(position);

		ClearTemplateCache(adapter);
		view.ItemTemplate = null;

		var refs = new ScenarioRefs(
			new WeakReference<PayloadTemplateSelector>(selector),
			new WeakReference<DataTemplate>(selector.Template),
			new WeakReference<Payload>(payload));

		return refs;
	}

	static ScenarioRefs CreateCurrentScenario(CollectionView view, ProbeAdapter adapter, int index)
	{
		var payload = new Payload(index + Iterations, PayloadBytes);
		var selector = new PayloadTemplateSelector(index + Iterations, payload);
		view.ItemTemplate = selector;

		for (var position = 0; position < ItemsPerSelector; position++)
			_ = adapter.GetItemViewType(position);

		view.ItemTemplate = null;

		var refs = new ScenarioRefs(
			new WeakReference<PayloadTemplateSelector>(selector),
			new WeakReference<DataTemplate>(selector.Template),
			new WeakReference<Payload>(payload));

		return refs;
	}

	static int GetTemplateCacheCount(ProbeAdapter adapter)
	{
		var cache = (System.Collections.IDictionary)ViewTypeTemplateCacheField.GetValue(adapter)!;
		return cache.Count;
	}

	static void ClearTemplateCache(ProbeAdapter adapter)
	{
		var cache = (System.Collections.IDictionary)ViewTypeTemplateCacheField.GetValue(adapter)!;
		cache.Clear();
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

	sealed class ProbeAdapter : StructuredItemsViewAdapter<CollectionView, IItemsViewSource>
	{
		public ProbeAdapter(CollectionView itemsView)
			: base(itemsView)
		{
		}
	}

	sealed class PayloadTemplateSelector : DataTemplateSelector
	{
		public PayloadTemplateSelector(int id, Payload payload)
		{
			Payload = payload;
			Template = new DataTemplate(() =>
			{
				var label = new Label();
				label.BindingContext = Payload;
				return label;
			});
		}

		public Payload Payload { get; }

		public DataTemplate Template { get; }

		protected override DataTemplate OnSelectTemplate(object item, BindableObject container) => Template;
	}

	sealed class ItemModel
	{
		public ItemModel(int id)
		{
			Id = id;
		}

		public int Id { get; }
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

	sealed record ScenarioRefs(
		WeakReference<PayloadTemplateSelector> Selector,
		WeakReference<DataTemplate> Template,
		WeakReference<Payload> Payload);
}

sealed record ProbeResult(
	int Iterations,
	int PayloadBytes,
	int ItemsPerSelector,
	int ControlTemplateCacheCount,
	int CurrentTemplateCacheCount,
	int ControlSelectorsRetained,
	int ControlTemplatesRetained,
	int ControlPayloadsRetained,
	int CurrentSelectorsRetained,
	int CurrentTemplatesRetained,
	int CurrentPayloadsRetained,
	long ManagedHeapBytes)
{
	public bool ProvedLeak =>
		ControlTemplateCacheCount == 0 &&
		CurrentTemplateCacheCount == Iterations &&
		ControlTemplatesRetained == 0 &&
		ControlPayloadsRetained == 0 &&
		CurrentTemplatesRetained == Iterations &&
		CurrentPayloadsRetained == Iterations;

	public string ToReport()
	{
		var retainedPayloadMiB = CurrentPayloadsRetained * PayloadBytes / 1024.0 / 1024.0;
		var heapMiB = ManagedHeapBytes / 1024.0 / 1024.0;

		return string.Join(Environment.NewLine, new[]
		{
			"AndroidItemsViewAdapterTemplateCacheRetentionLeakRepro",
			$"Iterations: {Iterations}",
			$"Items per selector: {ItemsPerSelector}",
			$"Payload bytes per selector: {PayloadBytes}",
			$"Control template-cache entries: {ControlTemplateCacheCount}",
			$"Current template-cache entries: {CurrentTemplateCacheCount}",
			$"Control retained selectors: {ControlSelectorsRetained}/{Iterations}",
			$"Control retained templates: {ControlTemplatesRetained}/{Iterations}",
			$"Control retained payloads: {ControlPayloadsRetained}/{Iterations}",
			$"Current retained selectors: {CurrentSelectorsRetained}/{Iterations}",
			$"Current retained templates: {CurrentTemplatesRetained}/{Iterations}",
			$"Current retained payloads: {CurrentPayloadsRetained}/{Iterations}",
			$"Retained payload estimate: {retainedPayloadMiB:F1} MiB",
			$"Managed heap after proof: {heapMiB:F1} MiB",
			$"Proved leak: {ProvedLeak}"
		});
	}
}
