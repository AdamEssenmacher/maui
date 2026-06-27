#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AndroidX.RecyclerView.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Items;

namespace AndroidCarouselPendingScrollRetentionLeakRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveLoopManagers,
	int PendingQueuesWithEntries,
	int TotalPendingQueueEntries,
	int QueuedMissingItemRequests,
	int AlivePayloads,
	int AlivePayloadBuffers,
	long HeapBefore,
	long HeapAfter);

public sealed record ReproReport(
	int Attempts,
	int PayloadBytes,
	RunStats Control,
	RunStats Current)
{
	public bool LeakProved =>
		Control.AliveLoopManagers == Attempts &&
		Control.PendingQueuesWithEntries == 0 &&
		Control.TotalPendingQueueEntries == 0 &&
		Control.QueuedMissingItemRequests == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AlivePayloadBuffers == 0 &&
		Current.AliveLoopManagers == Attempts &&
		Current.PendingQueuesWithEntries == Attempts &&
		Current.TotalPendingQueueEntries >= Attempts &&
		Current.QueuedMissingItemRequests == Attempts &&
		Current.AlivePayloads == Attempts &&
		Current.AlivePayloadBuffers == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidCarouselPendingScrollRetentionLeakRepro",
			$"Loop managers kept alive: {Attempts}",
			$"Payload per missing item: {PayloadBytes / 1024 / 1024} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current));
	}

	string Format(RunStats stats)
	{
		var retainedPayloadBytes = (long)stats.AlivePayloadBuffers * PayloadBytes;
		var totalPayloadBytes = (long)stats.Attempts * PayloadBytes;

		return string.Join(Environment.NewLine,
			$"Run: {stats.Name}",
			$"  loop managers alive after full GC: {stats.AliveLoopManagers}/{stats.Attempts}",
			$"  pending _pendingScrollTo queues with entries: {stats.PendingQueuesWithEntries}/{stats.Attempts}",
			$"  total queued ScrollToRequestEventArgs: {stats.TotalPendingQueueEntries}",
			$"  queued missing-item ScrollToRequestEventArgs: {stats.QueuedMissingItemRequests}/{stats.Attempts}",
			$"  missing item payloads alive after full GC: {stats.AlivePayloads}/{stats.Attempts}",
			$"  payload byte arrays alive after full GC: {stats.AlivePayloadBuffers}/{stats.Attempts}",
			$"  retained payload bytes: {FormatBytes(retainedPayloadBytes)} ({retainedPayloadBytes * 100.0 / totalPayloadBytes:0.0}%)",
			$"  managed heap before: {FormatBytes(stats.HeapBefore)}",
			$"  managed heap after: {FormatBytes(stats.HeapAfter)}",
			$"  managed heap delta: {FormatBytes(stats.HeapAfter - stats.HeapBefore)}");
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

	static readonly List<object> RetainedLoopManagers = new();

	static readonly FieldInfo LoopManagerField =
		typeof(MauiCarouselRecyclerView).GetField("_carouselViewLoopManager", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(nameof(MauiCarouselRecyclerView), "_carouselViewLoopManager");

	static readonly FieldInfo PendingScrollToField =
		LoopManagerField.FieldType.GetField("_pendingScrollTo", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(LoopManagerField.FieldType.Name, "_pendingScrollTo");

	static readonly string[] BaseRecyclerViewFieldsToClear =
	{
		"ItemsView",
		"ItemsViewAdapter",
		"_getItemsLayout",
		"RecyclerViewScrollListener",
		"_emptyViewAdapter",
		"_emptyCollectionObserver",
		"_itemsUpdateScrollObserver",
		"_itemDecoration",
		"_snapManager",
		"_scrollHelper",
		"_itemTouchHelper",
		"_itemTouchHelperCallback",
		"_layoutPropertyChangedProxy",
		"_layoutPropertyChanged",
		"CreateAdapter",
		"<ItemsLayout>k__BackingField"
	};

	public static async Task<ReproReport> RunAsync(IMauiContext mauiContext)
	{
		await Task.Yield();

		RetainedLoopManagers.Clear();

		var control = await RunScenarioAsync(
			mauiContext,
			"control: clear loop-manager source and pending ScrollTo queue",
			clearPendingQueue: true);

		var current = await RunScenarioAsync(
			mauiContext,
			"current: loop manager keeps missing-item pending ScrollTo request",
			clearPendingQueue: false);

		GC.KeepAlive(RetainedLoopManagers);
		return new ReproReport(Attempts, PayloadBytes, control, current);
	}

	static async Task<RunStats> RunScenarioAsync(IMauiContext mauiContext, string name, bool clearPendingQueue)
	{
		ForceFullGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		var retainedStartIndex = RetainedLoopManagers.Count;
		var loopManagerRefs = new List<WeakReference<object>>(Attempts);
		var payloadRefs = new List<WeakReference<Payload>>(Attempts);
		var bufferRefs = new List<WeakReference<byte[]>>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateRetainedLoopManagerWithQueuedMissingScroll(
				mauiContext,
				clearPendingQueue,
				loopManagerRefs,
				payloadRefs,
				bufferRefs,
				i);

			if (i % 10 == 0)
				await Task.Yield();
		}

		ForceFullGc();
		await Task.Delay(250);
		ForceFullGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var pendingQueueCounts = RetainedLoopManagers
			.Skip(retainedStartIndex)
			.Select(GetPendingQueueStats)
			.ToArray();

		GC.KeepAlive(RetainedLoopManagers);

		return new RunStats(
			name,
			Attempts,
			CountAlive(loopManagerRefs),
			pendingQueueCounts.Count(static stats => stats.TotalEntries > 0),
			pendingQueueCounts.Sum(static stats => stats.TotalEntries),
			pendingQueueCounts.Sum(static stats => stats.QueuedMissingItemRequests),
			CountAlive(payloadRefs),
			CountAlive(bufferRefs),
			heapBefore,
			heapAfter);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateRetainedLoopManagerWithQueuedMissingScroll(
		IMauiContext mauiContext,
		bool clearPendingQueue,
		List<WeakReference<object>> loopManagerRefs,
		List<WeakReference<Payload>> payloadRefs,
		List<WeakReference<byte[]>> bufferRefs,
		int index)
	{
		var payload = new Payload(index, PayloadBytes);
		var carouselView = new CarouselView
		{
			Loop = true,
			ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Horizontal),
			ItemsSource = new ObservableCollection<string>(
				Enumerable.Range(0, 12).Select(item => $"Invoice batch {index}-{item}"))
		};

		var handler = new CarouselViewHandler();
		handler.SetMauiContext(mauiContext);
		handler.SetVirtualView(carouselView);

		var recyclerView = handler.PlatformView;
		((IMauiRecyclerView<CarouselView>)recyclerView).UpdateItemsSource();

		carouselView.ScrollTo(payload, position: ScrollToPosition.Center, animate: true);

		var loopManager = LoopManagerField.GetValue(recyclerView)
			?? throw new InvalidOperationException("Could not find _carouselViewLoopManager.");

		ClearLoopManagerSource(loopManager);

		if (clearPendingQueue)
			ClearPendingQueue(loopManager);

		RetainedLoopManagers.Add(loopManager);
		loopManagerRefs.Add(new WeakReference<object>(loopManager));
		payloadRefs.Add(new WeakReference<Payload>(payload));
		bufferRefs.Add(new WeakReference<byte[]>(payload.Bytes));

		((IElementHandler)handler).DisconnectHandler();
		ClearBaseRecyclerViewReferences(recyclerView);
		LoopManagerField.SetValue(recyclerView, null);

		carouselView = null!;
		handler = null!;
		payload = null!;
	}

	static void ClearLoopManagerSource(object loopManager)
	{
		var setItemsSource = loopManager.GetType().GetMethod(
			"SetItemsSource",
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

		if (setItemsSource != null)
		{
			setItemsSource.Invoke(loopManager, new object?[] { null });
			return;
		}

		ClearField(loopManager, "_itemsSource");
	}

	static void ClearPendingQueue(object loopManager)
	{
		var pendingQueue = PendingScrollToField.GetValue(loopManager);
		pendingQueue?.GetType().GetMethod("Clear")?.Invoke(pendingQueue, null);
	}

	static (int TotalEntries, int QueuedMissingItemRequests) GetPendingQueueStats(object loopManager)
	{
		var pendingQueue = PendingScrollToField.GetValue(loopManager);
		if (pendingQueue == null)
			return (0, 0);

		var totalEntries = (int)(pendingQueue.GetType().GetProperty("Count")?.GetValue(pendingQueue) ?? 0);
		var queuedMissingItems = 0;

		foreach (var item in (IEnumerable)pendingQueue)
		{
			if (item is ScrollToRequestEventArgs { Item: Payload })
				queuedMissingItems++;
		}

		return (totalEntries, queuedMissingItems);
	}

	static void ClearBaseRecyclerViewReferences(RecyclerView recyclerView)
	{
		recyclerView.ClearOnScrollListeners();
		ClearAdapterReferences(recyclerView.GetAdapter());
		ClearAdapterReferences(GetFieldValue(recyclerView, "ItemsViewAdapter"));
		ClearAdapterReferences(GetFieldValue(recyclerView, "_emptyViewAdapter"));
		recyclerView.SetAdapter(null);
		recyclerView.SetLayoutManager(null);

		foreach (var fieldName in BaseRecyclerViewFieldsToClear)
			ClearField(recyclerView, fieldName);
	}

	static void ClearAdapterReferences(object? adapter)
	{
		if (adapter == null)
			return;

		ClearField(adapter, "ItemsView");
		ClearField(adapter, "ItemsSource");
		ClearField(adapter, "_createItemContentView");
		ClearField(adapter, "_itemTemplateSelector");
		ClearField(adapter, "_viewTypeDataTemplates");
		ClearField(adapter, "Header");
		ClearField(adapter, "Footer");
	}

	static object? GetFieldValue(object instance, string fieldName)
	{
		for (var current = instance.GetType(); current != null; current = current.BaseType)
		{
			var field = current.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field != null)
				return field.GetValue(instance);
		}

		return null;
	}

	static void ClearField(object instance, string fieldName)
	{
		for (var current = instance.GetType(); current != null; current = current.BaseType)
		{
			var field = current.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field != null)
			{
				field.SetValue(instance, null);
				return;
			}
		}
	}

	static int CountAlive<T>(IEnumerable<WeakReference<T>> references)
		where T : class
	{
		var count = 0;

		foreach (var reference in references)
		{
			if (reference.TryGetTarget(out _))
				count++;
		}

		return count;
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

	sealed class Payload
	{
		public Payload(int id, int byteCount)
		{
			Id = id;
			Title = "Missing invoice batch " + id;
			Bytes = new byte[byteCount];

			for (var i = 0; i < Bytes.Length; i += 4096)
				Bytes[i] = (byte)(id + i);
		}

		public int Id { get; }

		public string Title { get; }

		public byte[] Bytes { get; }

		public override string ToString() => Title;
	}
}
