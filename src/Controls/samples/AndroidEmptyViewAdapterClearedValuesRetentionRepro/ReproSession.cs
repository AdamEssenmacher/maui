#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AndroidX.RecyclerView.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Items;

namespace AndroidEmptyViewAdapterClearedValuesRetentionRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveNativeRecyclerViews,
	int AliveHiddenEmptyAdapters,
	int AliveCollectionViews,
	int AliveHandlers,
	int AlivePayloadObjects,
	int AlivePayloadByteArrays,
	long RetainedPayloadBytes);

public sealed record ReproReport(
	int Attempts,
	int PayloadsPerAttempt,
	int PayloadBytes,
	RunStats Control,
	RunStats Current,
	long ManagedHeapBaseline,
	long ManagedHeapFinal)
{
	int ExpectedPayloads => Attempts * PayloadsPerAttempt;

	public bool LeakProved =>
		Control.AliveNativeRecyclerViews == Attempts &&
		Control.AliveHiddenEmptyAdapters == Attempts &&
		Control.AliveCollectionViews == Attempts &&
		Control.AlivePayloadObjects == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Control.RetainedPayloadBytes == 0 &&
		Current.AliveNativeRecyclerViews == Attempts &&
		Current.AliveHiddenEmptyAdapters == Attempts &&
		Current.AliveCollectionViews == Attempts &&
		Current.AlivePayloadObjects == ExpectedPayloads &&
		Current.AlivePayloadByteArrays == ExpectedPayloads &&
		Current.RetainedPayloadBytes == (long)ExpectedPayloads * PayloadBytes;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidEmptyViewAdapterClearedValuesRetentionRepro",
			$"Attempts: {Attempts}",
			$"Payloads per attempt: {PayloadsPerAttempt}",
			$"Payload per cleared EmptyView: {FormatBytes(PayloadBytes)}",
			$"Expected cleared payloads per run: {ExpectedPayloads}",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Managed heap baseline: {FormatBytes(ManagedHeapBaseline)}",
			$"Managed heap final: {FormatBytes(ManagedHeapFinal)}",
			$"Managed heap delta: {FormatBytes(ManagedHeapFinal - ManagedHeapBaseline)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	string Format(RunStats stats)
	{
		return string.Join(Environment.NewLine,
			$"Run: {stats.Name}",
			$"  retained native RecyclerViews: {stats.AliveNativeRecyclerViews}/{stats.Attempts}",
			$"  hidden EmptyViewAdapters alive: {stats.AliveHiddenEmptyAdapters}/{stats.Attempts}",
			$"  live CollectionViews alive: {stats.AliveCollectionViews}/{stats.Attempts}",
			$"  handlers alive after full GC: {stats.AliveHandlers}/{stats.Attempts}",
			$"  cleared payload objects alive: {stats.AlivePayloadObjects}/{ExpectedPayloads}",
			$"  cleared payload byte arrays alive: {stats.AlivePayloadByteArrays}/{ExpectedPayloads}",
			$"  retained cleared payload bytes: {FormatBytes(stats.RetainedPayloadBytes)}");
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
	const int PayloadsPerAttempt = 1;
	const int PayloadBytes = 512 * 1024;

	static readonly List<RecyclerView> RetainedNativeRecyclerViews = new();

	public static async Task<ReproReport> RunAsync(IMauiContext mauiContext)
	{
		await Task.Yield();

		RetainedNativeRecyclerViews.Clear();
		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			mauiContext,
			"control: clear hidden EmptyViewAdapter cached values after public clear",
			clearHiddenAdapterValues: true);

		var current = await RunScenarioAsync(
			mauiContext,
			"current: public clear leaves hidden EmptyViewAdapter cached values assigned",
			clearHiddenAdapterValues: false);

		ForceFullGc();
		GC.KeepAlive(RetainedNativeRecyclerViews);
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadsPerAttempt, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(IMauiContext mauiContext, string name, bool clearHiddenAdapterValues)
	{
		var recyclerRefs = new List<WeakReference<RecyclerView>>(Attempts);
		var hiddenAdapterRefs = new List<WeakReference<EmptyViewAdapter>>(Attempts);
		var handlerRefs = new List<WeakReference<CollectionViewHandler>>(Attempts);
		var collectionViewRefs = new List<WeakReference<CollectionView>>(Attempts);
		var payloadRefs = new List<PayloadWeakReference>(Attempts * PayloadsPerAttempt);

		for (var i = 0; i < Attempts; i++)
		{
			CreateLiveCollectionViewWithClearedEmptyValues(
				mauiContext,
				clearHiddenAdapterValues,
				recyclerRefs,
				hiddenAdapterRefs,
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
		var aliveHiddenAdapters = hiddenAdapterRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveHandlers = handlerRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveCollectionViews = collectionViewRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloads = payloadRefs.Count(static wr => wr.Payload.TryGetTarget(out _));
		var alivePayloadByteArrays = payloadRefs.Count(static wr => wr.Bytes.TryGetTarget(out _));

		return new RunStats(
			name,
			Attempts,
			aliveNativeRecyclerViews,
			aliveHiddenAdapters,
			aliveCollectionViews,
			aliveHandlers,
			alivePayloads,
			alivePayloadByteArrays,
			(long)alivePayloadByteArrays * PayloadBytes);
	}

	static void CreateLiveCollectionViewWithClearedEmptyValues(
		IMauiContext mauiContext,
		bool clearHiddenAdapterValues,
		List<WeakReference<RecyclerView>> recyclerRefs,
		List<WeakReference<EmptyViewAdapter>> hiddenAdapterRefs,
		List<WeakReference<CollectionViewHandler>> handlerRefs,
		List<WeakReference<CollectionView>> collectionViewRefs,
		List<PayloadWeakReference> payloadRefs,
		int index)
	{
		var emptyPayload = new Payload(index, "EmptyView", PayloadBytes);
		var items = Array.Empty<object>();

		var collectionView = new CollectionView
		{
			ItemsLayout = LinearItemsLayout.Vertical,
			ItemsSource = items,
			EmptyView = emptyPayload
		};

		var handler = new CollectionViewHandler();
		handler.SetMauiContext(mauiContext);
		handler.SetVirtualView(collectionView);

		var recyclerView = handler.PlatformView;
		var mauiRecyclerView = (IMauiRecyclerView<ReorderableItemsView>)recyclerView;

		// Force the hidden empty adapter to snapshot EmptyView while the CollectionView is empty.
		mauiRecyclerView.UpdateAdapter();
		mauiRecyclerView.UpdateEmptyView();

		var hiddenAdapter = GetHiddenEmptyAdapter(recyclerView)
			?? throw new InvalidOperationException("Expected MauiRecyclerView to create a hidden EmptyViewAdapter.");

		payloadRefs.Add(new PayloadWeakReference(new WeakReference<Payload>(emptyPayload), new WeakReference<byte[]>(emptyPayload.Bytes)));
		collectionViewRefs.Add(new WeakReference<CollectionView>(collectionView));
		handlerRefs.Add(new WeakReference<CollectionViewHandler>(handler));
		recyclerRefs.Add(new WeakReference<RecyclerView>(recyclerView));
		hiddenAdapterRefs.Add(new WeakReference<EmptyViewAdapter>(hiddenAdapter));
		RetainedNativeRecyclerViews.Add(recyclerView);

		collectionView.EmptyView = null;
		mauiRecyclerView.UpdateAdapter();
		mauiRecyclerView.UpdateEmptyView();

		if (clearHiddenAdapterValues)
			ClearHiddenEmptyAdapterValues(hiddenAdapter);

		emptyPayload = null!;
		items = null!;
		collectionView = null!;
		handler = null!;
		recyclerView = null!;
		mauiRecyclerView = null!;
		hiddenAdapter = null!;
	}

	static EmptyViewAdapter? GetHiddenEmptyAdapter(RecyclerView recyclerView)
	{
		var field = FindField(recyclerView.GetType(), "_emptyViewAdapter")
			?? throw new InvalidOperationException("Could not find MauiRecyclerView._emptyViewAdapter.");

		return (EmptyViewAdapter?)field.GetValue(recyclerView);
	}

	static void ClearHiddenEmptyAdapterValues(EmptyViewAdapter adapter)
	{
		adapter.Header = null;
		adapter.Footer = null;
		adapter.EmptyView = null;
		adapter.HeaderTemplate = null;
		adapter.FooterTemplate = null;
		adapter.EmptyViewTemplate = null;
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
			Java.Lang.JavaSystem.Gc();
			Thread.Sleep(100);
		}
	}

	sealed record PayloadWeakReference(WeakReference<Payload> Payload, WeakReference<byte[]> Bytes);

	sealed class Payload
	{
		public Payload(int id, string slot, int byteCount)
		{
			Id = id;
			Slot = slot;
			Bytes = new byte[byteCount];
			for (var i = 0; i < Bytes.Length; i += 4096)
				Bytes[i] = (byte)((id + slot.Length + i) % 251);
			Bytes[^1] = (byte)((id + slot.Length + Bytes.Length) % 251);
		}

		public int Id { get; }

		public string Slot { get; }

		public byte[] Bytes { get; }

		public override string ToString() => $"{Slot} payload {Id}";
	}
}
