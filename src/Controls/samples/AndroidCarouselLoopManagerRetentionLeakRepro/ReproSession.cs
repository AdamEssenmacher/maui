#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using AndroidX.RecyclerView.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Items;

namespace AndroidCarouselLoopManagerRetentionLeakRepro;

internal static class ReproSession
{
	const int Attempts = 80;
	const int PayloadBytes = 1024 * 1024;

	static readonly List<RecyclerView> RetainedNativeRecyclerViews = new();
	static readonly List<object> RetainedLoopManagers = new();

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

		RetainedNativeRecyclerViews.Clear();
		RetainedLoopManagers.Clear();
		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var structuralControl = await RunLoopManagerScenarioAsync(
			mauiContext,
			"structural control: retain loop managers after SetItemsSource(null)",
			clearLoopManagerSource: true);

		var structuralLoopOnly = await RunLoopManagerScenarioAsync(
			mauiContext,
			"structural leak: retained loop managers keep item sources",
			clearLoopManagerSource: false);

		var loopOnly = await RunScenarioAsync(
			mauiContext,
			"current loop-manager path after base recycler fields are cleared",
			clearBaseRecyclerViewFields: true,
			clearLoopManager: false);

		var current = await RunScenarioAsync(
			mauiContext,
			"current: disconnected MauiCarouselRecyclerView keeps stale fields",
			clearBaseRecyclerViewFields: false,
			clearLoopManager: false);

		ForceFullGc();
		GC.KeepAlive(RetainedNativeRecyclerViews);
		GC.KeepAlive(RetainedLoopManagers);
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, structuralControl, structuralLoopOnly, loopOnly, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(
		IMauiContext mauiContext,
		string name,
		bool clearBaseRecyclerViewFields,
		bool clearLoopManager)
	{
		var recyclerRefs = new List<WeakReference<RecyclerView>>(Attempts);
		var handlerRefs = new List<WeakReference<CarouselViewHandler>>(Attempts);
		var carouselRefs = new List<WeakReference<CarouselView>>(Attempts);
		var payloadRefs = new List<PayloadWeakReference>(Attempts);
		var retainedStartIndex = RetainedNativeRecyclerViews.Count;

		for (var i = 0; i < Attempts; i++)
		{
			CreateDisconnectedCarouselView(
				mauiContext,
				clearBaseRecyclerViewFields,
				clearLoopManager,
				recyclerRefs,
				handlerRefs,
				carouselRefs,
				payloadRefs,
				i);

			if (i % 10 == 0)
				await Task.Yield();
		}

		ForceFullGc();
		GC.KeepAlive(RetainedNativeRecyclerViews);

		var aliveNativeRecyclerViews = recyclerRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveHandlers = handlerRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveCarouselViews = carouselRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloads = payloadRefs.Count(static wr => wr.Payload.TryGetTarget(out _));
		var alivePayloadByteArrays = payloadRefs.Count(static wr => wr.Bytes.TryGetTarget(out _));
		var diagnosticPath = GetDiagnosticPath(
			RetainedNativeRecyclerViews,
			retainedStartIndex,
			carouselRefs,
			payloadRefs);

		return new RunStats(
			name,
			Attempts,
			aliveNativeRecyclerViews,
			aliveHandlers,
			aliveCarouselViews,
			alivePayloads,
			alivePayloadByteArrays,
			(long)alivePayloadByteArrays * PayloadBytes,
			diagnosticPath);
	}

	static async Task<RunStats> RunLoopManagerScenarioAsync(
		IMauiContext mauiContext,
		string name,
		bool clearLoopManagerSource)
	{
		var recyclerRefs = new List<WeakReference<RecyclerView>>(Attempts);
		var handlerRefs = new List<WeakReference<CarouselViewHandler>>(Attempts);
		var carouselRefs = new List<WeakReference<CarouselView>>(Attempts);
		var payloadRefs = new List<PayloadWeakReference>(Attempts);
		var retainedStartIndex = RetainedLoopManagers.Count;

		for (var i = 0; i < Attempts; i++)
		{
			CreateRetainedLoopManager(
				mauiContext,
				clearLoopManagerSource,
				recyclerRefs,
				handlerRefs,
				carouselRefs,
				payloadRefs,
				i);

			if (i % 10 == 0)
				await Task.Yield();
		}

		ForceFullGc();
		GC.KeepAlive(RetainedLoopManagers);

		var aliveNativeRecyclerViews = recyclerRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveHandlers = handlerRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveCarouselViews = carouselRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloads = payloadRefs.Count(static wr => wr.Payload.TryGetTarget(out _));
		var alivePayloadByteArrays = payloadRefs.Count(static wr => wr.Bytes.TryGetTarget(out _));
		var diagnosticPath = GetDiagnosticPath(
			RetainedLoopManagers,
			retainedStartIndex,
			carouselRefs,
			payloadRefs);

		return new RunStats(
			name,
			Attempts,
			aliveNativeRecyclerViews,
			aliveHandlers,
			aliveCarouselViews,
			alivePayloads,
			alivePayloadByteArrays,
			(long)alivePayloadByteArrays * PayloadBytes,
			diagnosticPath);
	}

	static string GetDiagnosticPath(
		IReadOnlyList<object> roots,
		int retainedStartIndex,
		List<WeakReference<CarouselView>> carouselRefs,
		List<PayloadWeakReference> payloadRefs)
	{
		if (retainedStartIndex >= roots.Count)
			return "diagnostic path: no retained root";

		var root = roots[retainedStartIndex];

		if (carouselRefs.FirstOrDefault(static wr => wr.TryGetTarget(out _)) is { } carouselRef &&
			carouselRef.TryGetTarget(out var carouselView))
		{
			return FindReferencePath(root, carouselView, maxDepth: 8, maxNodes: 12000) ??
				"diagnostic path to CarouselView: not found in managed fields";
		}

		var payloadRef = payloadRefs.FirstOrDefault(static wr => wr.Payload.TryGetTarget(out _));
		if (payloadRef?.Payload.TryGetTarget(out var payload) == true)
		{
			return FindReferencePath(root, payload, maxDepth: 8, maxNodes: 12000) ??
				"diagnostic path to payload: not found in managed fields";
		}

		return "diagnostic path: no alive CarouselView or payload";
	}

	static string? FindReferencePath(object root, object target, int maxDepth, int maxNodes)
	{
		var visited = new HashSet<object>(ReferenceEqualityComparer.Instance) { root };
		var queue = new Queue<(object Value, string Path, int Depth)>();
		queue.Enqueue((root, root.GetType().Name, 0));
		var visitedNodes = 0;

		while (queue.Count > 0 && visitedNodes++ < maxNodes)
		{
			var (value, path, depth) = queue.Dequeue();
			if (ReferenceEquals(value, target))
				return path;

			if (depth >= maxDepth)
				continue;

			foreach (var child in GetReferenceChildren(value))
			{
				if (ReferenceEquals(child.Value, target))
					return $"{path}.{child.Name} -> {target.GetType().Name}";

				if (visited.Add(child.Value))
					queue.Enqueue((child.Value, $"{path}.{child.Name}", depth + 1));
			}
		}

		return null;
	}

	static IEnumerable<(string Name, object Value)> GetReferenceChildren(object value)
	{
		if (value is Delegate del)
		{
			if (del.Target is { } target)
				yield return ("Target", target);
			yield break;
		}

		if (value is Array array && value is not byte[])
		{
			var limit = Math.Min(array.Length, 32);
			for (var i = 0; i < limit; i++)
			{
				if (array.GetValue(i) is { } item && ShouldTraverse(item))
					yield return ($"[{i}]", item);
			}
		}

		for (var type = value.GetType(); type != null; type = type.BaseType)
		{
			FieldInfo[] fields;
			try
			{
				fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
			}
			catch
			{
				continue;
			}

			foreach (var field in fields)
			{
				if (field.FieldType.IsValueType || field.FieldType == typeof(string))
					continue;

				object? fieldValue;
				try
				{
					fieldValue = field.GetValue(value);
				}
				catch
				{
					continue;
				}

				if (fieldValue is null || !ShouldTraverse(fieldValue))
					continue;

				yield return ($"{type.Name}.{field.Name}", fieldValue);
			}
		}
	}

	static bool ShouldTraverse(object value)
	{
		if (value is string || value is byte[] || value is Type)
			return false;

		var type = value.GetType();
		return !type.IsPrimitive && !type.IsEnum;
	}

	static void CreateDisconnectedCarouselView(
		IMauiContext mauiContext,
		bool clearBaseRecyclerViewFields,
		bool clearLoopManager,
		List<WeakReference<RecyclerView>> recyclerRefs,
		List<WeakReference<CarouselViewHandler>> handlerRefs,
		List<WeakReference<CarouselView>> carouselRefs,
		List<PayloadWeakReference> payloadRefs,
		int index)
	{
		var payload = new Payload(index, PayloadBytes);
		var carouselView = new CarouselView
		{
			Loop = true,
			ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Horizontal),
			ItemsSource = new ObservableCollection<string>(
				Enumerable.Range(0, 12).Select(item => $"Invoice {index}-{item}")),
			BindingContext = payload
		};

		var handler = new CarouselViewHandler();
		handler.SetMauiContext(mauiContext);
		handler.SetVirtualView(carouselView);

		var recyclerView = handler.PlatformView;
		((IMauiRecyclerView<CarouselView>)recyclerView).UpdateItemsSource();

		payloadRefs.Add(new PayloadWeakReference(new WeakReference<Payload>(payload), new WeakReference<byte[]>(payload.Bytes)));
		carouselRefs.Add(new WeakReference<CarouselView>(carouselView));
		handlerRefs.Add(new WeakReference<CarouselViewHandler>(handler));
		recyclerRefs.Add(new WeakReference<RecyclerView>(recyclerView));
		RetainedNativeRecyclerViews.Add(recyclerView);

		((IElementHandler)handler).DisconnectHandler();

		if (clearBaseRecyclerViewFields)
			ClearBaseRecyclerViewReferences(recyclerView);

		if (clearLoopManager)
			ClearField(recyclerView, "_carouselViewLoopManager");

		carouselView = null!;
		handler = null!;
		payload = null!;
	}

	static void CreateRetainedLoopManager(
		IMauiContext mauiContext,
		bool clearLoopManagerSource,
		List<WeakReference<RecyclerView>> recyclerRefs,
		List<WeakReference<CarouselViewHandler>> handlerRefs,
		List<WeakReference<CarouselView>> carouselRefs,
		List<PayloadWeakReference> payloadRefs,
		int index)
	{
		var payload = new Payload(index, PayloadBytes);
		var carouselView = new CarouselView
		{
			Loop = true,
			ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Horizontal),
			ItemsSource = new ObservableCollection<string>(
				Enumerable.Range(0, 12).Select(item => $"Invoice {index}-{item}")),
			BindingContext = payload
		};

		var handler = new CarouselViewHandler();
		handler.SetMauiContext(mauiContext);
		handler.SetVirtualView(carouselView);

		var recyclerView = handler.PlatformView;
		((IMauiRecyclerView<CarouselView>)recyclerView).UpdateItemsSource();

		var loopManager = GetFieldValue(recyclerView, "_carouselViewLoopManager")
			?? throw new InvalidOperationException("Could not find _carouselViewLoopManager.");
		RetainedLoopManagers.Add(loopManager);

		if (clearLoopManagerSource)
			ClearLoopManagerSource(loopManager);

		payloadRefs.Add(new PayloadWeakReference(new WeakReference<Payload>(payload), new WeakReference<byte[]>(payload.Bytes)));
		carouselRefs.Add(new WeakReference<CarouselView>(carouselView));
		handlerRefs.Add(new WeakReference<CarouselViewHandler>(handler));
		recyclerRefs.Add(new WeakReference<RecyclerView>(recyclerView));

		((IElementHandler)handler).DisconnectHandler();
		ClearBaseRecyclerViewReferences(recyclerView);
		ClearField(recyclerView, "_carouselViewLoopManager");

		carouselView = null!;
		handler = null!;
		payload = null!;
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

	static void ClearField(object instance, string fieldName)
	{
		for (var current = instance.GetType(); current != null; current = current.BaseType)
		{
			var field = current.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			field?.SetValue(instance, null);
		}
	}

	static object? GetFieldValue(object instance, string fieldName)
	{
		var field = FindField(instance.GetType(), fieldName);
		return field?.GetValue(instance);
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

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveNativeRecyclerViews,
	int AliveHandlers,
	int AliveCarouselViews,
	int AlivePayloads,
	int AlivePayloadByteArrays,
	long RetainedPayloadBytes,
	string DiagnosticPath);

public sealed record ReproReport(
	int Attempts,
	int PayloadBytes,
	RunStats StructuralControl,
	RunStats StructuralLoopOnly,
	RunStats LoopOnly,
	RunStats Current,
	long ManagedHeapBaseline,
	long ManagedHeapFinal)
{
	public bool LeakProved =>
		StructuralControl.AliveCarouselViews == 0 &&
		StructuralControl.AlivePayloads == 0 &&
		StructuralControl.AlivePayloadByteArrays == 0 &&
		StructuralLoopOnly.AliveCarouselViews == Attempts &&
		StructuralLoopOnly.AlivePayloads == Attempts &&
		StructuralLoopOnly.AlivePayloadByteArrays == Attempts &&
		LoopOnly.AliveCarouselViews == Attempts &&
		LoopOnly.AlivePayloads == Attempts &&
		LoopOnly.AlivePayloadByteArrays == Attempts &&
		LoopOnly.DiagnosticPath.Contains("_carouselViewLoopManager", StringComparison.Ordinal) &&
		Current.AliveCarouselViews == Attempts &&
		Current.AlivePayloads == Attempts &&
		Current.AlivePayloadByteArrays == Attempts;

	public string ToText()
	{
		var builder = new StringBuilder();
		builder.AppendLine("AndroidCarouselLoopManagerRetentionLeakRepro");
		builder.AppendLine($"Attempts: {Attempts}");
		builder.AppendLine($"Payload per attempt: {FormatBytes(PayloadBytes)}");
		builder.AppendLine($"Leak proved: {LeakProved}");
		builder.AppendLine();
		AppendRun(builder, StructuralControl);
		builder.AppendLine();
		AppendRun(builder, StructuralLoopOnly);
		builder.AppendLine();
		AppendRun(builder, LoopOnly);
		builder.AppendLine();
		AppendRun(builder, Current);
		builder.AppendLine();
		builder.AppendLine($"Managed heap baseline: {FormatBytes(ManagedHeapBaseline)}");
		builder.AppendLine($"Managed heap final: {FormatBytes(ManagedHeapFinal)}");
		builder.AppendLine($"Managed heap delta: {FormatBytes(ManagedHeapFinal - ManagedHeapBaseline)}");
		return builder.ToString();
	}

	void AppendRun(StringBuilder builder, RunStats stats)
	{
		builder.AppendLine($"Run: {stats.Name}");
		builder.AppendLine($"  retained native RecyclerViews: {stats.AliveNativeRecyclerViews}/{stats.Attempts}");
		builder.AppendLine($"  handlers alive after full GC: {stats.AliveHandlers}/{stats.Attempts}");
		builder.AppendLine($"  CarouselViews alive after full GC: {stats.AliveCarouselViews}/{stats.Attempts}");
			builder.AppendLine($"  payloads alive after full GC: {stats.AlivePayloads}/{stats.Attempts}");
			builder.AppendLine($"  payload byte arrays alive after full GC: {stats.AlivePayloadByteArrays}/{stats.Attempts}");
			builder.AppendLine($"  retained payload bytes: {FormatBytes(stats.RetainedPayloadBytes)} ({stats.RetainedPayloadBytes * 100.0 / (PayloadBytes * stats.Attempts):0.0}%)");
			builder.AppendLine($"  {stats.DiagnosticPath}");
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
