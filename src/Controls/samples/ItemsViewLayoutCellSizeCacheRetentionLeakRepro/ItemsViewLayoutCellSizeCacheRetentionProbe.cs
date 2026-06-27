using System.Collections;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using CoreGraphics;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Items;

namespace ItemsViewLayoutCellSizeCacheRetentionLeakRepro;

static class ItemsViewLayoutCellSizeCacheRetentionProbe
{
	const int Iterations = 80;
	const int PayloadBytes = 1024 * 1024;

	static readonly MethodInfo CacheCellSizeMethod =
		typeof(ItemsViewLayout).GetMethod("CacheCellSize", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new InvalidOperationException("Could not find ItemsViewLayout.CacheCellSize.");

	static readonly FieldInfo CellSizeCacheField =
		typeof(ItemsViewLayout).GetField("_cellSizeCache", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new InvalidOperationException("Could not find ItemsViewLayout._cellSizeCache.");

	public static ProbeResult Run()
	{
		var control = CreateScenario(startId: 0, clearCacheAfterRemove: true);
		var current = CreateScenario(startId: Iterations, clearCacheAfterRemove: false);

		ForceCollect();
		GC.KeepAlive(control.LayoutRoot);
		GC.KeepAlive(current.LayoutRoot);

		return new ProbeResult(
			Iterations,
			PayloadBytes,
			control.SourceCountAfterRemove,
			current.SourceCountAfterRemove,
			control.CacheEntriesAfterRemove,
			current.CacheEntriesAfterRemove,
			CountAlive(control.PayloadRefs),
			CountAlive(current.PayloadRefs),
			GC.GetTotalMemory(forceFullCollection: true));
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static Scenario CreateScenario(int startId, bool clearCacheAfterRemove)
	{
		var layout = CreateLayout();
		var payloads = Enumerable.Range(startId, Iterations)
			.Select(id => new Payload(id, PayloadBytes))
			.ToArray();
		var source = new ObservableCollection<Payload>(payloads);

		foreach (var payload in payloads)
			CacheCellSize(layout, payload);

		foreach (var payload in payloads)
			source.Remove(payload);

		if (clearCacheAfterRemove)
			ClearCache(layout);

		var payloadRefs = payloads
			.Select(payload => new WeakReference<Payload>(payload))
			.ToList();

		return new Scenario(layout, payloadRefs, source.Count, GetCacheCount(layout));
	}

	static ItemsViewLayout CreateLayout()
	{
		var itemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Vertical)
		{
			ItemSpacing = 4
		};

		var layout = new ListViewLayout(itemsLayout, ItemSizingStrategy.MeasureAllItems);
		layout.ConstrainTo(new CGSize(390, 844));
		return layout;
	}

	static void CacheCellSize(ItemsViewLayout layout, Payload payload) =>
		CacheCellSizeMethod.Invoke(layout, new object[] { payload, new CGSize(390, 44) });

	static int GetCacheCount(ItemsViewLayout layout)
	{
		var cache = (IDictionary)CellSizeCacheField.GetValue(layout)!;
		return cache.Count;
	}

	static void ClearCache(ItemsViewLayout layout)
	{
		var cache = (IDictionary)CellSizeCacheField.GetValue(layout)!;
		cache.Clear();
	}

	static int CountAlive(List<WeakReference<Payload>> refs)
	{
		var count = 0;
		foreach (var item in refs)
		{
			if (item.TryGetTarget(out _))
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

	sealed record Scenario(
		ItemsViewLayout LayoutRoot,
		List<WeakReference<Payload>> PayloadRefs,
		int SourceCountAfterRemove,
		int CacheEntriesAfterRemove);
}

sealed record ProbeResult(
	int Iterations,
	int PayloadBytes,
	int ControlSourceCountAfterRemove,
	int CurrentSourceCountAfterRemove,
	int ControlCacheEntriesAfterRemove,
	int CurrentCacheEntriesAfterRemove,
	int ControlPayloadsRetained,
	int CurrentPayloadsRetained,
	long ManagedHeapBytes)
{
	public bool ProvedLeak =>
		ControlSourceCountAfterRemove == 0 &&
		CurrentSourceCountAfterRemove == 0 &&
		ControlCacheEntriesAfterRemove == 0 &&
		CurrentCacheEntriesAfterRemove == Iterations &&
		ControlPayloadsRetained == 0 &&
		CurrentPayloadsRetained == Iterations;

	public string ToReport()
	{
		var retainedPayloadMiB = CurrentPayloadsRetained * PayloadBytes / 1024.0 / 1024.0;
		var heapMiB = ManagedHeapBytes / 1024.0 / 1024.0;

		return string.Join(Environment.NewLine, new[]
		{
			"ItemsViewLayoutCellSizeCacheRetentionLeakRepro",
			$"Iterations: {Iterations}",
			$"Payload bytes per item: {PayloadBytes}",
			$"Control source count after remove: {ControlSourceCountAfterRemove}",
			$"Current source count after remove: {CurrentSourceCountAfterRemove}",
			$"Control cell-size cache entries after remove: {ControlCacheEntriesAfterRemove}",
			$"Current cell-size cache entries after remove: {CurrentCacheEntriesAfterRemove}",
			$"Control retained payloads: {ControlPayloadsRetained}/{Iterations}",
			$"Current retained payloads: {CurrentPayloadsRetained}/{Iterations}",
			$"Retained payload estimate: {retainedPayloadMiB:F1} MiB",
			$"Managed heap after proof: {heapMiB:F1} MiB",
			$"Proved leak: {ProvedLeak}"
		});
	}
}
