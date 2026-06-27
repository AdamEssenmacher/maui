#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AndroidX.RecyclerView.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Items;
using Microsoft.Maui.Graphics;

namespace AndroidMauiRecyclerViewElementRetentionLeakRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveNativeRecyclerViews,
	int AliveHandlers,
	int AliveCollectionViews,
	int AlivePayloads,
	int AlivePayloadByteArrays,
	long RetainedPayloadBytes);

public sealed record ReproReport(
	int Attempts,
	int PayloadBytes,
	RunStats Control,
	RunStats Current,
	long ManagedHeapBaseline,
	long ManagedHeapFinal)
{
	public bool LeakProved =>
		Control.AliveCollectionViews == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Current.AliveCollectionViews == Attempts &&
		Current.AlivePayloads == Attempts &&
		Current.AlivePayloadByteArrays == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidMauiRecyclerViewElementRetentionLeakRepro",
			$"Attempts: {Attempts}",
			$"Payload per attempt: {PayloadBytes / 1024 / 1024} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Managed heap baseline: {FormatBytes(ManagedHeapBaseline)}",
			$"Managed heap final: {FormatBytes(ManagedHeapFinal)}",
			$"Managed heap delta: {FormatBytes(ManagedHeapFinal - ManagedHeapBaseline)}");
	}

	string Format(RunStats stats)
	{
		return string.Join(Environment.NewLine,
			$"Run: {stats.Name}",
			$"  retained native RecyclerViews: {stats.AliveNativeRecyclerViews}/{stats.Attempts}",
			$"  handlers alive after full GC: {stats.AliveHandlers}/{stats.Attempts}",
			$"  CollectionViews alive after full GC: {stats.AliveCollectionViews}/{stats.Attempts}",
			$"  payloads alive after full GC: {stats.AlivePayloads}/{stats.Attempts}",
			$"  payload byte arrays alive after full GC: {stats.AlivePayloadByteArrays}/{stats.Attempts}",
			$"  retained payload bytes: {FormatBytes(stats.RetainedPayloadBytes)} ({stats.RetainedPayloadBytes * 100.0 / (PayloadBytes * stats.Attempts):0.0}%)");
	}

	static string FormatBytes(long bytes)
	{
		var sign = bytes < 0 ? "-" : "";
		var value = Math.Abs((double)bytes);
		if (value >= 1024 * 1024)
			return $"{sign}{value / 1024 / 1024:0.0} MiB";
		if (value >= 1024)
			return $"{sign}{value / 1024:0.0} KiB";
		return $"{bytes} B";
	}
}

internal static class ReproSession
{
	const int Attempts = 80;
	const int PayloadBytes = 1024 * 1024;

	static readonly List<RecyclerView> RetainedNativeRecyclerViews = new();

	static readonly string[] FieldNamesToClear =
	{
		"ItemsView",
		"ItemsViewAdapter",
		"_emptyViewAdapter",
		"_itemDecoration",
		"_scrollHelper",
		"CreateAdapter",
		"<ItemsLayout>k__BackingField"
	};

	public static async Task<ReproReport> RunAsync(IMauiContext mauiContext)
	{
		await Task.Yield();

		RetainedNativeRecyclerViews.Clear();
		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			mauiContext,
			"control: disconnect then clear stale MauiRecyclerView fields",
			clearRecyclerViewFields: true);

		var current = await RunScenarioAsync(
			mauiContext,
			"current: disconnected MauiRecyclerView keeps stale fields",
			clearRecyclerViewFields: false);

		ForceFullGc();
		GC.KeepAlive(RetainedNativeRecyclerViews);
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(IMauiContext mauiContext, string name, bool clearRecyclerViewFields)
	{
		var recyclerRefs = new List<WeakReference<RecyclerView>>(Attempts);
		var handlerRefs = new List<WeakReference<CollectionViewHandler>>(Attempts);
		var collectionViewRefs = new List<WeakReference<CollectionView>>(Attempts);
		var payloadRefs = new List<PayloadWeakReference>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDisconnectedCollectionView(
				mauiContext,
				clearRecyclerViewFields,
				recyclerRefs,
				handlerRefs,
				collectionViewRefs,
				payloadRefs,
				i);

			if (i % 10 == 0)
				await Task.Yield();
		}

		ForceFullGc();
		GC.KeepAlive(RetainedNativeRecyclerViews);

		var aliveNativeRecyclerViews = recyclerRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveHandlers = handlerRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveCollectionViews = collectionViewRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloads = payloadRefs.Count(static wr => wr.Payload.TryGetTarget(out _));
		var alivePayloadByteArrays = payloadRefs.Count(static wr => wr.Bytes.TryGetTarget(out _));

		return new RunStats(
			name,
			Attempts,
			aliveNativeRecyclerViews,
			aliveHandlers,
			aliveCollectionViews,
			alivePayloads,
			alivePayloadByteArrays,
			(long)alivePayloadByteArrays * PayloadBytes);
	}

	static void CreateDisconnectedCollectionView(
		IMauiContext mauiContext,
		bool clearRecyclerViewFields,
		List<WeakReference<RecyclerView>> recyclerRefs,
		List<WeakReference<CollectionViewHandler>> handlerRefs,
		List<WeakReference<CollectionView>> collectionViewRefs,
		List<PayloadWeakReference> payloadRefs,
		int index)
	{
		var payload = new Payload(index, PayloadBytes);
		var collectionView = new CollectionView
		{
			ItemsLayout = LinearItemsLayout.Vertical,
			ItemsSource = Enumerable.Range(0, 24).Select(item => $"Order {index}-{item}").ToArray(),
			Header = $"Batch {index}",
			BindingContext = payload
		};

		var handler = new CollectionViewHandler();
		handler.SetMauiContext(mauiContext);
		handler.SetVirtualView(collectionView);

		var recyclerView = handler.PlatformView;

		payloadRefs.Add(new PayloadWeakReference(new WeakReference<Payload>(payload), new WeakReference<byte[]>(payload.Bytes)));
		collectionViewRefs.Add(new WeakReference<CollectionView>(collectionView));
		handlerRefs.Add(new WeakReference<CollectionViewHandler>(handler));
		recyclerRefs.Add(new WeakReference<RecyclerView>(recyclerView));
		RetainedNativeRecyclerViews.Add(recyclerView);

		((IElementHandler)handler).DisconnectHandler();

		if (clearRecyclerViewFields)
			ClearRecyclerViewReferences(recyclerView);
	}

	static void ClearRecyclerViewReferences(RecyclerView recyclerView)
	{
		foreach (var fieldName in FieldNamesToClear)
		{
			var field = FindField(recyclerView.GetType(), fieldName);
			if (field is not null)
				field.SetValue(recyclerView, null);
		}
	}

	static FieldInfo? FindField(Type type, string name)
	{
		for (var current = type; current != null; current = current.BaseType)
		{
			var field = current.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field != null)
				return field;
		}

		return null;
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

	sealed record PayloadWeakReference(WeakReference<Payload> Payload, WeakReference<byte[]> Bytes);

	sealed class Payload
	{
		public Payload(int id, int byteCount)
		{
			Id = id;
			Bytes = new byte[byteCount];
			for (var i = 0; i < Bytes.Length; i += 4096)
				Bytes[i] = (byte)((id + i) % 251);
			Bytes[^1] = (byte)((id + Bytes.Length) % 251);
		}

		public int Id { get; }

		public byte[] Bytes { get; }
	}
}
