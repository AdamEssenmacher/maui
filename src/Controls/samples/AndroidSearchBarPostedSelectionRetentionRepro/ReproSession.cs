#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using AView = Android.Views.View;
using SearchView = AndroidX.AppCompat.Widget.SearchView;

namespace AndroidSearchBarPostedSelectionRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 96;
	const int PayloadBytesPerCycle = 1024 * 1024;

	static readonly List<SearchView> RetainedNativePeers = new();

	public static async Task<ReproReport> RunAsync(IMauiContext baseContext)
	{
		RetainedNativePeers.Clear();

		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			"control: assign native query only after handler disconnect",
			baseContext,
			fireConnectedQueryChange: false);

		var current = await RunScenarioAsync(
			"current: connected query change queues MAUI selection callback",
			baseContext,
			fireConnectedQueryChange: true);

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
		bool fireConnectedQueryChange)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateCycle(baseContext, i, tracked, fireConnectedQueryChange);

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
		bool fireConnectedQueryChange)
	{
		var payloadService = new PayloadService(cycle, PayloadBytesPerCycle);
		var payloadProvider = new PayloadServiceProvider(baseContext.Services, payloadService);
		var androidContext = baseContext.Context
			?? Android.App.Application.Context
			?? throw new InvalidOperationException("No Android context is available.");
		var cycleContext = new MauiContext(payloadProvider, androidContext);

		var searchBar = new SearchBar
		{
			Text = fireConnectedQueryChange ? $"initial-query-{cycle:D3}" : null,
			Placeholder = "Find customers, orders, and invoices"
		};
		var handler = new SearchBarHandler();

		handler.SetMauiContext(cycleContext);
		searchBar.Handler = handler;

		var platformView = handler.PlatformView
			?? throw new InvalidOperationException("SearchBarHandler did not create an Android SearchView.");
		var queryEditor = handler.QueryEditor
			?? throw new InvalidOperationException("SearchBarHandler did not expose its query editor.");

		// Current MAUI fires QueryTextChange while the handler is connected, which schedules
		// SearchBarHandler.OnQueryEditorSelectionChanged() through QueryEditor.Post(...).
		// The control keeps the same retained native query state, but assigns it only after
		// disconnect, so no handler-capturing selection callback is queued.
		if (fireConnectedQueryChange)
			platformView.SetQuery($"updated-query-{cycle:D3}", false);
		var assignedQueryLength = platformView.Query?.Length ?? 0;

		((IElementHandler)handler).DisconnectHandler();
		searchBar.Handler = null;
		searchBar.Text = null;
		searchBar.BindingContext = null;

		if (!fireConnectedQueryChange)
		{
			platformView.SetQuery($"updated-query-{cycle:D3}", false);
			assignedQueryLength = platformView.Query?.Length ?? 0;
		}

		RetainedNativePeers.Add(platformView);
		tracked.Add(TrackedCycle.Create(
			cycle,
			platformView,
			queryEditor,
			searchBar,
			handler,
			cycleContext,
			payloadProvider,
			payloadService,
			payloadService.Payload,
			assignedQueryLength,
			fireConnectedQueryChange));
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
			Payload[^1] = (byte)((cycle + 31) % byte.MaxValue);
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
		WeakReference<SearchView> NativePeer,
		WeakReference<EditText> QueryEditor,
		WeakReference<SearchBar> VirtualView,
		WeakReference<SearchBarHandler> Handler,
		WeakReference<IMauiContext> MauiContext,
		WeakReference<IServiceProvider> PayloadProvider,
		WeakReference<PayloadService> PayloadService,
		WeakReference<byte[]> PayloadBuffer,
		int AssignedQueryLength,
		bool FiredConnectedQueryChange)
	{
		public static TrackedCycle Create(
			int cycle,
			SearchView platformView,
			EditText queryEditor,
			SearchBar virtualView,
			SearchBarHandler handler,
			IMauiContext mauiContext,
			IServiceProvider payloadProvider,
			PayloadService payloadService,
			byte[] payloadBuffer,
			int assignedQueryLength,
			bool firedConnectedQueryChange)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<SearchView>(platformView),
				new WeakReference<EditText>(queryEditor),
				new WeakReference<SearchBar>(virtualView),
				new WeakReference<SearchBarHandler>(handler),
				new WeakReference<IMauiContext>(mauiContext),
				new WeakReference<IServiceProvider>(payloadProvider),
				new WeakReference<PayloadService>(payloadService),
				new WeakReference<byte[]>(payloadBuffer),
				assignedQueryLength,
				firedConnectedQueryChange);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativePeers,
		int AliveQueryEditors,
		int AliveVirtualViews,
		int AliveHandlers,
		int AliveMauiContexts,
		int AlivePayloadProviders,
		int AlivePayloadServices,
		int AlivePayloadBuffers,
		long RetainedPayloadBytes,
		int AssignedQueries,
		int ConnectedQueryChanges)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativePeers = 0;
			var aliveQueryEditors = 0;
			var aliveVirtualViews = 0;
			var aliveHandlers = 0;
			var aliveMauiContexts = 0;
			var alivePayloadProviders = 0;
			var alivePayloadServices = 0;
			var alivePayloadBuffers = 0;
			var assignedQueries = 0;
			var connectedQueryChanges = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativePeer.TryGetTarget(out _))
					aliveNativePeers++;

				if (cycle.QueryEditor.TryGetTarget(out _))
					aliveQueryEditors++;

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

				if (cycle.AssignedQueryLength > 0)
					assignedQueries++;

				if (cycle.FiredConnectedQueryChange)
					connectedQueryChanges++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativePeers,
				aliveQueryEditors,
				aliveVirtualViews,
				aliveHandlers,
				aliveMauiContexts,
				alivePayloadProviders,
				alivePayloadServices,
				alivePayloadBuffers,
				(long)alivePayloadBuffers * PayloadBytesPerCycle,
				assignedQueries,
				connectedQueryChanges);
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
		Control.ConnectedQueryChanges == 0 &&
		Control.AliveHandlers == 0 &&
		Control.AliveMauiContexts == 0 &&
		Control.AlivePayloadBuffers == 0 &&
		Current.ConnectedQueryChanges == Cycles &&
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
			"AndroidSearchBarPostedSelectionRetentionRepro",
			$"Cycles per scenario: {Cycles}",
			$"Payload bytes per cycle: {PayloadBytesPerCycle:N0}",
			"Source path exercised: SearchBarHandler.OnQueryTextChange -> OnQueryEditorSelectionChanged -> QueryEditor.Post",
			"Control assigns the native query after handler disconnect; current MAUI queues the connected selection callback before disconnect",
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
			$"  assigned queries: {result.AssignedQueries}/{result.TrackedCycles}",
			$"  connected query changes: {result.ConnectedQueryChanges}/{result.TrackedCycles}",
			$"  alive native SearchView peers: {result.AliveNativePeers}/{result.TrackedCycles}",
			$"  alive query editors: {result.AliveQueryEditors}/{result.TrackedCycles}",
			$"  alive virtual SearchBars: {result.AliveVirtualViews}/{result.TrackedCycles}",
			$"  alive SearchBarHandlers: {result.AliveHandlers}/{result.TrackedCycles}",
			$"  alive MauiContexts: {result.AliveMauiContexts}/{result.TrackedCycles}",
			$"  alive payload providers: {result.AlivePayloadProviders}/{result.TrackedCycles}",
			$"  alive payload services: {result.AlivePayloadServices}/{result.TrackedCycles}",
			$"  alive payload buffers: {result.AlivePayloadBuffers}/{result.TrackedCycles}",
			$"  retained scoped-service payload bytes: {result.RetainedPayloadBytes:N0}");
	}
}
