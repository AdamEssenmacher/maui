#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AndroidX.RecyclerView.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Items;
using Microsoft.Maui.Graphics;

namespace AndroidItemsViewEmptyViewPostRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 96;
	const int PayloadBytesPerCycle = 1024 * 1024;

	static readonly List<RecyclerView> RetainedNativePeers = new();

	public static async Task<ReproReport> RunAsync(IMauiContext baseContext)
	{
		RetainedNativePeers.Clear();

		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			"control: no arrange-time empty-view layout post",
			baseContext,
			triggerDeferredLayoutPost: false);

		var current = await RunScenarioAsync(
			"current: arrange queues empty-view layout post",
			baseContext,
			triggerDeferredLayoutPost: true);

		await Task.Delay(500);
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativePeers);

		return new ReproReport(
			Cycles,
			PayloadBytesPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext baseContext,
		bool triggerDeferredLayoutPost)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateCycle(baseContext, i, tracked, triggerDeferredLayoutPost);

			if (i % 12 == 0)
				await Task.Yield();
		}

		await Task.Delay(250);
		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	static void CreateCycle(
		IMauiContext baseContext,
		int cycle,
		List<TrackedCycle> tracked,
		bool triggerDeferredLayoutPost)
	{
		var payloadService = new PayloadService(cycle, PayloadBytesPerCycle);
		var payloadProvider = new PayloadServiceProvider(baseContext.Services, payloadService);
		var androidContext = baseContext.Context
			?? Android.App.Application.Context
			?? throw new InvalidOperationException("No Android context is available.");
		var cycleContext = new MauiContext(payloadProvider, androidContext);

		var collectionView = new CollectionView
		{
			ItemsSource = Array.Empty<object>(),
			EmptyView = $"No matching invoices for customer segment {cycle:D3}",
			WidthRequest = 360,
			HeightRequest = 640
		};
		var handler = new CollectionViewHandler();

		handler.SetMauiContext(cycleContext);
		collectionView.Handler = handler;

		var platformView = handler.PlatformView
			?? throw new InvalidOperationException("CollectionViewHandler did not create an Android RecyclerView.");
		var recyclerView = platformView as IMauiRecyclerView<ReorderableItemsView>
			?? throw new InvalidOperationException("CollectionViewHandler did not create a MAUI RecyclerView.");

		recyclerView.UpdateAdapter();
		recyclerView.UpdateEmptyView();

		var emptyAdapterWasActive = platformView.GetAdapter() is EmptyViewAdapter;
		var emptyViewHolderMissing = platformView.FindViewHolderForAdapterPosition(0) is null;

		// Current MAUI posts a handler-capturing callback when the EmptyView holder is not
		// ready during arrange. The control keeps the same native RecyclerView/EmptyView
		// shape, but skips the arrange call so the deferred callback is not queued.
		if (triggerDeferredLayoutPost)
			handler.PlatformArrange(new Rect(0, 0, 360, 640));

		((IElementHandler)handler).DisconnectHandler();
		collectionView.Handler = null;
		collectionView.EmptyView = null;
		collectionView.ItemsSource = null;
		collectionView.BindingContext = null;

		var clearedRecyclerViewFields = ClearKnownRecyclerViewHandlerFields(platformView);

		RetainedNativePeers.Add(platformView);
		tracked.Add(TrackedCycle.Create(
			cycle,
			platformView,
			collectionView,
			handler,
			cycleContext,
			payloadProvider,
			payloadService,
			payloadService.Payload,
			emptyAdapterWasActive,
			emptyViewHolderMissing,
			triggerDeferredLayoutPost,
			clearedRecyclerViewFields));
	}

	static int ClearKnownRecyclerViewHandlerFields(RecyclerView platformView)
	{
		var cleared = 0;
		foreach (var fieldName in new[]
		{
			"ItemsView",
			"ItemsViewAdapter",
			"CreateAdapter",
			"_getItemsLayout",
			"<ItemsLayout>k__BackingField",
			"_emptyViewAdapter",
			"RecyclerViewScrollListener",
			"_snapManager",
			"_scrollHelper",
			"_itemDecoration",
			"_itemTouchHelper",
			"_itemTouchHelperCallback",
			"_layoutPropertyChangedProxy",
			"_layoutPropertyChanged"
		})
		{
			if (TryClearField(platformView, fieldName))
				cleared++;
		}

		return cleared;
	}

	static bool TryClearField(object target, string fieldName)
	{
		for (var type = target.GetType(); type is not null; type = type.BaseType)
		{
			var field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field is null || field.FieldType.IsValueType)
				continue;

			try
			{
				field.SetValue(target, null);
				return true;
			}
			catch
			{
				return false;
			}
		}

		return false;
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

	internal sealed class PayloadService
	{
		public PayloadService(int cycle, int payloadBytes)
		{
			Cycle = cycle;
			Payload = new byte[payloadBytes];
			Payload[0] = (byte)(cycle % byte.MaxValue);
			Payload[^1] = (byte)((cycle + 17) % byte.MaxValue);
		}

		public int Cycle { get; }
		public byte[] Payload { get; }
	}

	sealed class PayloadServiceProvider : IServiceProvider
	{
		readonly IServiceProvider _inner;
		readonly PayloadService _payloadService;

		public PayloadServiceProvider(IServiceProvider inner, PayloadService payloadService)
		{
			_inner = inner;
			_payloadService = payloadService;
		}

		public object? GetService(Type serviceType)
		{
			if (serviceType == typeof(PayloadService))
				return _payloadService;

			return _inner.GetService(serviceType);
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<RecyclerView> NativePeer,
		WeakReference<CollectionView> VirtualView,
		WeakReference<CollectionViewHandler> Handler,
		WeakReference<IMauiContext> MauiContext,
		WeakReference<IServiceProvider> PayloadProvider,
		WeakReference<PayloadService> PayloadService,
		WeakReference<byte[]> PayloadBuffer,
		bool EmptyAdapterWasActive,
		bool EmptyViewHolderMissing,
		bool TriggeredDeferredLayoutPost,
		int ClearedRecyclerViewFields)
	{
		public static TrackedCycle Create(
			int cycle,
			RecyclerView platformView,
			CollectionView virtualView,
			CollectionViewHandler handler,
			IMauiContext mauiContext,
			IServiceProvider payloadProvider,
			PayloadService payloadService,
			byte[] payloadBuffer,
			bool emptyAdapterWasActive,
			bool emptyViewHolderMissing,
			bool triggeredDeferredLayoutPost,
			int clearedRecyclerViewFields)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<RecyclerView>(platformView),
				new WeakReference<CollectionView>(virtualView),
				new WeakReference<CollectionViewHandler>(handler),
				new WeakReference<IMauiContext>(mauiContext),
				new WeakReference<IServiceProvider>(payloadProvider),
				new WeakReference<PayloadService>(payloadService),
				new WeakReference<byte[]>(payloadBuffer),
				emptyAdapterWasActive,
				emptyViewHolderMissing,
				triggeredDeferredLayoutPost,
				clearedRecyclerViewFields);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativePeers,
		int AliveVirtualViews,
		int AliveHandlers,
		int AliveMauiContexts,
		int AlivePayloadProviders,
		int AlivePayloadServices,
		int AlivePayloadBuffers,
		long RetainedPayloadBytes,
		int ActiveEmptyAdapters,
		int MissingEmptyViewHolders,
		int DeferredLayoutPosts,
		int CyclesWithClearedRecyclerViewFields)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativePeers = 0;
			var aliveVirtualViews = 0;
			var aliveHandlers = 0;
			var aliveMauiContexts = 0;
			var alivePayloadProviders = 0;
			var alivePayloadServices = 0;
			var alivePayloadBuffers = 0;
			var activeEmptyAdapters = 0;
			var missingEmptyViewHolders = 0;
			var deferredLayoutPosts = 0;
			var cyclesWithClearedRecyclerViewFields = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativePeer.TryGetTarget(out _))
					aliveNativePeers++;

				if (cycle.VirtualView.TryGetTarget(out _))
					aliveVirtualViews++;

				if (cycle.Handler.TryGetTarget(out _))
					aliveHandlers++;

				if (cycle.MauiContext.TryGetTarget(out _))
					aliveMauiContexts++;

				if (cycle.PayloadProvider.TryGetTarget(out _))
					alivePayloadProviders++;

				if (cycle.PayloadService.TryGetTarget(out _))
					alivePayloadServices++;

				if (cycle.PayloadBuffer.TryGetTarget(out _))
					alivePayloadBuffers++;

				if (cycle.EmptyAdapterWasActive)
					activeEmptyAdapters++;

				if (cycle.EmptyViewHolderMissing)
					missingEmptyViewHolders++;

				if (cycle.TriggeredDeferredLayoutPost)
					deferredLayoutPosts++;

				if (cycle.ClearedRecyclerViewFields >= 4)
					cyclesWithClearedRecyclerViewFields++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativePeers,
				aliveVirtualViews,
				aliveHandlers,
				aliveMauiContexts,
				alivePayloadProviders,
				alivePayloadServices,
				alivePayloadBuffers,
				(long)alivePayloadBuffers * PayloadBytesPerCycle,
				activeEmptyAdapters,
				missingEmptyViewHolders,
				deferredLayoutPosts,
				cyclesWithClearedRecyclerViewFields);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadBytesPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.AliveNativePeers == Cycles &&
		Current.AliveNativePeers == Cycles &&
		Control.ActiveEmptyAdapters == Cycles &&
		Current.ActiveEmptyAdapters == Cycles &&
		Control.MissingEmptyViewHolders == Cycles &&
		Current.MissingEmptyViewHolders == Cycles &&
		Control.DeferredLayoutPosts == 0 &&
		Current.DeferredLayoutPosts == Cycles &&
		Control.CyclesWithClearedRecyclerViewFields == Cycles &&
		Current.CyclesWithClearedRecyclerViewFields == Cycles &&
		Control.AliveHandlers == 0 &&
		Control.AliveMauiContexts == 0 &&
		Control.AlivePayloadBuffers == 0 &&
		Current.AliveHandlers == Cycles &&
		Current.AliveMauiContexts == Cycles &&
		Current.AlivePayloadBuffers == Cycles &&
		Current.RetainedPayloadBytes >= 80L * 1024 * 1024;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var controlMiB = Control.RetainedPayloadBytes / 1024d / 1024d;
		var currentMiB = Current.RetainedPayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidItemsViewEmptyViewPostRetentionRepro",
			$"Cycles per scenario: {Cycles}",
			$"Payload bytes per cycle: {PayloadBytesPerCycle:N0}",
			"Source path exercised: ItemsViewHandler.UpdateEmptyViewSize -> PlatformView.Post",
			"Both scenarios clear known MauiRecyclerView stale handler fields after disconnect",
			"Control skips arrange; current MAUI arranges while the empty-view holder is missing and queues the deferred callback",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained scoped-service payload: {controlMiB:N1} MiB",
			$"Current retained scoped-service payload: {currentMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  active EmptyViewAdapters: {result.ActiveEmptyAdapters}/{result.TrackedCycles}",
			$"  missing empty-view holders before arrange: {result.MissingEmptyViewHolders}/{result.TrackedCycles}",
			$"  deferred layout posts requested: {result.DeferredLayoutPosts}/{result.TrackedCycles}",
			$"  cleared known RecyclerView fields: {result.CyclesWithClearedRecyclerViewFields}/{result.TrackedCycles}",
			$"  alive native RecyclerView peers: {result.AliveNativePeers}/{result.TrackedCycles}",
			$"  alive virtual CollectionViews: {result.AliveVirtualViews}/{result.TrackedCycles}",
			$"  alive CollectionViewHandlers: {result.AliveHandlers}/{result.TrackedCycles}",
			$"  alive MauiContexts: {result.AliveMauiContexts}/{result.TrackedCycles}",
			$"  alive payload providers: {result.AlivePayloadProviders}/{result.TrackedCycles}",
			$"  alive payload services: {result.AlivePayloadServices}/{result.TrackedCycles}",
			$"  alive payload buffers: {result.AlivePayloadBuffers}/{result.TrackedCycles}",
			$"  retained scoped-service payload bytes: {result.RetainedPayloadBytes:N0}");
	}
}
