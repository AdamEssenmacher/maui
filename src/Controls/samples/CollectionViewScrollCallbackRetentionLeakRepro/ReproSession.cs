using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Controls.Handlers.Items2;
using Microsoft.Maui.Handlers;

namespace CollectionViewScrollCallbackRetentionLeakRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadMegabytesPerCycle = 1;
	internal const long PayloadSizeBytes = PayloadMegabytesPerCycle * 1024L * 1024L;

	static readonly PropertyInfo ControllerProperty =
		typeof(ItemsViewHandler2<ReorderableItemsView>).GetProperty("Controller", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(typeof(ItemsViewHandler2<ReorderableItemsView>).FullName, "Controller");

	public static ReproReport Run(IMauiContext mauiContext)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = RunExplicitCallbackClearControl(mauiContext);
		var current = RunCurrentInvalidAnimatedScroll(mauiContext);

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

	static ScenarioResult RunExplicitCallbackClearControl(IMauiContext mauiContext)
	{
		var retainedLists = new List<CollectionView>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateCycle(mauiContext, retainedLists, tracked, i, clearPendingCallback: true);

		ForceFullGc();

		var result = ScenarioResult.From("control: invalid animated ScrollTo then clear pending callback", tracked);
		GC.KeepAlive(retainedLists);
		return result;
	}

	static ScenarioResult RunCurrentInvalidAnimatedScroll(IMauiContext mauiContext)
	{
		var retainedLists = new List<CollectionView>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateCycle(mauiContext, retainedLists, tracked, i, clearPendingCallback: false);

		ForceFullGc();

		var result = ScenarioResult.From("current: invalid animated grouped ScrollTo leaves pending callback", tracked);
		GC.KeepAlive(retainedLists);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateCycle(
		IMauiContext mauiContext,
		List<CollectionView> retainedLists,
		List<TrackedCycle> tracked,
		int cycle,
		bool clearPendingCallback)
	{
		var requestPayload = new MissingDocumentPayload(cycle, PayloadSizeBytes);
		var group = CreateVisibleGroup(cycle);
		var collectionView = CreateCollectionView(group);
		var handler = ConnectHandler(collectionView, mauiContext);

		tracked.Add(TrackedCycle.Create(cycle, requestPayload, handler));

		collectionView.ScrollTo(requestPayload, group, ScrollToPosition.End, animate: true);

		var controller = ControllerProperty.GetValue(handler)
			?? throw new InvalidOperationException("CollectionViewHandler2 controller was not created.");

		if (clearPendingCallback)
			ClearPendingScrollAnimationCallback(controller);

		retainedLists.Add(collectionView);
	}

	static CollectionViewHandler2 ConnectHandler(CollectionView collectionView, IMauiContext mauiContext)
	{
		var handler = new CollectionViewHandler2();
		((IElementHandler)handler).SetMauiContext(mauiContext);
		((IElementHandler)handler).SetVirtualView(collectionView);
		return handler;
	}

	static CollectionView CreateCollectionView(DocumentGroup group)
	{
		return new CollectionView
		{
			IsGrouped = true,
			ItemSizingStrategy = ItemSizingStrategy.MeasureAllItems,
			ItemsLayout = LinearItemsLayout.Vertical,
			WidthRequest = 420,
			HeightRequest = 720,
			ItemsSource = new ObservableCollection<DocumentGroup> { group },
			ItemTemplate = new DataTemplate(() => new Label { HeightRequest = 32 }),
			GroupHeaderTemplate = new DataTemplate(() => new Label { HeightRequest = 28 })
		};
	}

	static DocumentGroup CreateVisibleGroup(int cycle)
	{
		var group = new DocumentGroup($"Project {cycle + 1:000}");

		for (var i = 0; i < 30; i++)
			group.Add(new VisibleDocumentRow($"DOC-{cycle + 1:000}-{i + 1:000}", "Visible cached row"));

		return group;
	}

	static void ClearPendingScrollAnimationCallback(object controller)
	{
		var field = FindField(controller.GetType(), "_scrollAnimationEndedCallback")
			?? throw new MissingFieldException(controller.GetType().FullName, "_scrollAnimationEndedCallback");

		field.SetValue(controller, null);
	}

	static FieldInfo? FindField(Type? type, string name)
	{
		while (type is not null)
		{
			var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
			if (field is not null)
				return field;

			type = type.BaseType;
		}

		return null;
	}

	static bool HasPendingCallback(CollectionViewHandler2 handler)
	{
		var controller = ControllerProperty.GetValue(handler);
		if (controller is null)
			return false;

		var field = FindField(controller.GetType(), "_scrollAnimationEndedCallback");
		return field?.GetValue(controller) is not null;
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

	sealed class DocumentGroup : ObservableCollection<VisibleDocumentRow>
	{
		public DocumentGroup(string name)
		{
			Name = name;
		}

		public string Name { get; }
	}

	internal sealed record VisibleDocumentRow(string Id, string Summary);

	internal sealed class MissingDocumentPayload
	{
		public MissingDocumentPayload(int cycle, long payloadBytes)
		{
			Cycle = cycle;
			PayloadBytes = payloadBytes;
			DocumentBytes = new byte[payloadBytes];

			for (var i = 0; i < DocumentBytes.Length; i += 4096)
				DocumentBytes[i] = (byte)(cycle + i);

			SearchRows = Enumerable.Range(1, 50)
				.Select(index => new VisibleDocumentRow(
					$"MISSING-{cycle + 1:000}-{index:000}",
					"Filtered document preview retained by stale ScrollTo request"))
				.ToArray();
		}

		public int Cycle { get; }

		public long PayloadBytes { get; }

		public byte[] DocumentBytes { get; }

		public IReadOnlyList<VisibleDocumentRow> SearchRows { get; }
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference Payload,
		WeakReference PayloadBytes,
		WeakReference Handler,
		long PayloadByteCount)
	{
		public static TrackedCycle Create(int cycle, MissingDocumentPayload payload, CollectionViewHandler2 handler)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference(payload),
				new WeakReference(payload.DocumentBytes),
				new WeakReference(handler),
				payload.PayloadBytes);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveHandlers,
		int PendingCallbacks,
		int AlivePayloads,
		int AlivePayloadByteArrays,
		long RetainedPayloadBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> cycles)
		{
			var aliveHandlers = 0;
			var pendingCallbacks = 0;
			var alivePayloads = 0;
			var alivePayloadByteArrays = 0;
			long retainedPayloadBytes = 0;

			foreach (var cycle in cycles)
			{
				if (cycle.Handler.Target is CollectionViewHandler2 handler)
				{
					aliveHandlers++;
					if (HasPendingCallback(handler))
						pendingCallbacks++;
				}

				if (cycle.Payload.IsAlive)
				{
					alivePayloads++;
					retainedPayloadBytes += cycle.PayloadByteCount;
				}

				if (cycle.PayloadBytes.IsAlive)
					alivePayloadByteArrays++;
			}

			return new ScenarioResult(
				name,
				cycles.Count,
				aliveHandlers,
				pendingCallbacks,
				alivePayloads,
				alivePayloadByteArrays,
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
			Control.AliveHandlers == Control.TrackedCycles &&
			Current.AliveHandlers == Current.TrackedCycles &&
			Control.PendingCallbacks == 0 &&
			Current.PendingCallbacks == Current.TrackedCycles &&
			Control.AlivePayloads == 0 &&
			Control.AlivePayloadByteArrays == 0 &&
			Current.AlivePayloads == Current.TrackedCycles &&
			Current.AlivePayloadByteArrays == Current.TrackedCycles;

		public string ToText()
		{
			return string.Join(Environment.NewLine,
				"CollectionView grouped ScrollTo pending callback retention repro",
				$"Cycles: {Cycles}",
				$"Payload per missing ScrollTo item: {PayloadMegabytesPerCycle} MiB",
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
			var expectedPayload = result.TrackedCycles == 0 ? 0 : result.TrackedCycles * PayloadSizeBytes;
			var retainedPercent = expectedPayload == 0 ? 0 : result.RetainedPayloadBytes * 100.0 / expectedPayload;

			return string.Join(Environment.NewLine,
				$"Scenario: {result.Name}",
				$"  app-retained grouped CollectionViews: {result.TrackedCycles}",
				$"  retained handlers: {result.AliveHandlers}/{result.TrackedCycles}",
				$"  pending scroll animation callbacks: {result.PendingCallbacks}/{result.TrackedCycles}",
				$"  retained missing ScrollTo payloads: {result.AlivePayloads}/{result.TrackedCycles}",
				$"  retained payload byte arrays: {result.AlivePayloadByteArrays}/{result.TrackedCycles}",
				$"  retained payload bytes: {FormatBytes(result.RetainedPayloadBytes)} ({retainedPercent:0.0}%)");
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
}
