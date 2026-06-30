#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Android.Gms.Maps;
using Microsoft.Maui;
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Maps;
using Microsoft.Maui.Maps.Handlers;

namespace AndroidMapHandlerMapElementsPostRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 40;
	const int PayloadBytesPerCycle = 2 * 1024 * 1024;

	static readonly List<MapView> RetainedNativePeers = new();

	public static async Task<ReproReport> RunAsync(IMauiContext baseContext)
	{
		RetainedNativePeers.Clear();
		ClearStaticMapBundle();

		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			"control: no MapElements post after map-ready state",
			baseContext,
			triggerMapElementsPost: false);

		var current = await RunScenarioAsync(
			"current: MapElements queues handler-capturing MapView.Post",
			baseContext,
			triggerMapElementsPost: true);

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
		bool triggerMapElementsPost)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateCycle(baseContext, i, tracked, triggerMapElementsPost);

			if (i % 8 == 0)
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
		bool triggerMapElementsPost)
	{
		var payloadService = new PayloadService(cycle, PayloadBytesPerCycle);
		var payloadProvider = new PayloadServiceProvider(baseContext.Services, payloadService);
		var androidContext = baseContext.Context
			?? Android.App.Application.Context
			?? throw new InvalidOperationException("No Android context is available.");
		var cycleContext = new MauiContext(payloadProvider, androidContext);

		var map = new Map();
		var handler = new TestMapHandler();

		handler.SetMauiContext(cycleContext);
		map.Handler = handler;

		var platformView = handler.PlatformView
			?? throw new InvalidOperationException("MapHandler did not create an Android MapView.");

		var noPendingMapReadyCallback = EnsureNoPendingMapReadyCallback(handler);

		// Route through the current MapHandler.MapElements() post branch without depending on
		// Google Maps API-key readiness. The queued callback is never run in the repro; the
		// synthetic GoogleMap only selects the same source path that a ready map uses.
		SetMap(handler, CreateSyntheticGoogleMap());
		var mapBranchWasReady = handler.Map is not null;

		if (triggerMapElementsPost)
			MapHandler.MapElements(handler, map);

		SetMap(handler, null);

		((IElementHandler)handler).DisconnectHandler();
		map.Handler = null;
		map.BindingContext = null;
		map.MapElements.Clear();
		map.Pins.Clear();

		RetainedNativePeers.Add(platformView);
		tracked.Add(TrackedCycle.Create(
			cycle,
			platformView,
			map,
			handler,
			cycleContext,
			payloadProvider,
			payloadService,
			payloadService.Payload,
			mapBranchWasReady,
			triggerMapElementsPost,
			noPendingMapReadyCallback));
	}

	static GoogleMap CreateSyntheticGoogleMap()
	{
		return (GoogleMap)RuntimeHelpers.GetUninitializedObject(typeof(GoogleMap));
	}

	static void SetMap(MapHandler handler, GoogleMap? map)
	{
		SetField(handler, "<Map>k__BackingField", map);
	}

	static bool EnsureNoPendingMapReadyCallback(MapHandler handler)
	{
		var callback = GetField(handler, "_mapReady") as IDisposable;
		callback?.Dispose();
		SetField(handler, "_mapReady", null);
		return GetField(handler, "_mapReady") is null;
	}

	sealed class TestMapHandler : MapHandler
	{
		protected override void ConnectHandler(MapView platformView)
		{
			// Skip GetMapAsync so this proof does not exercise the already tracked
			// C134 pending map-ready callback root.
		}

		protected override void DisconnectHandler(MapView platformView)
		{
		}
	}

	static void ClearStaticMapBundle()
	{
		SetField(typeof(MapHandler), "s_bundle", null);
	}

	static object? GetField(object target, string fieldName)
	{
		for (var type = target.GetType(); type is not null; type = type.BaseType)
		{
			var field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field is not null)
				return field.GetValue(target);
		}

		return null;
	}

	static void SetField(object target, string fieldName, object? value)
	{
		for (var type = target.GetType(); type is not null; type = type.BaseType)
		{
			var field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field is not null)
			{
				field.SetValue(target, value);
				return;
			}
		}

		throw new MissingFieldException(target.GetType().FullName, fieldName);
	}

	static void SetField(Type type, string fieldName, object? value)
	{
		var field = type.GetField(fieldName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
			?? throw new MissingFieldException(type.FullName, fieldName);
		field.SetValue(null, value);
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
			Payload[^1] = (byte)((cycle + 43) % byte.MaxValue);
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
		WeakReference<MapView> NativePeer,
		WeakReference<Map> VirtualMap,
		WeakReference<MapHandler> Handler,
		WeakReference<IMauiContext> MauiContext,
		WeakReference<IServiceProvider> PayloadProvider,
		WeakReference<PayloadService> PayloadService,
		WeakReference<byte[]> PayloadBuffer,
		bool MapBranchWasReady,
		bool TriggeredMapElementsPost,
		bool NoPendingMapReadyCallback)
	{
		public static TrackedCycle Create(
			int cycle,
			MapView platformView,
			Map virtualMap,
			MapHandler handler,
			IMauiContext mauiContext,
			IServiceProvider payloadProvider,
			PayloadService payloadService,
			byte[] payloadBuffer,
			bool mapBranchWasReady,
			bool triggeredMapElementsPost,
			bool noPendingMapReadyCallback)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<MapView>(platformView),
				new WeakReference<Map>(virtualMap),
				new WeakReference<MapHandler>(handler),
				new WeakReference<IMauiContext>(mauiContext),
				new WeakReference<IServiceProvider>(payloadProvider),
				new WeakReference<PayloadService>(payloadService),
				new WeakReference<byte[]>(payloadBuffer),
				mapBranchWasReady,
				triggeredMapElementsPost,
				noPendingMapReadyCallback);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativePeers,
		int AliveVirtualMaps,
		int AliveHandlers,
		int AliveMauiContexts,
		int AlivePayloadProviders,
		int AlivePayloadServices,
		int AlivePayloadBuffers,
		long RetainedPayloadBytes,
		int MapReadyBranches,
		int MapElementsPosts,
		int CyclesWithNoPendingMapReadyCallback)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativePeers = 0;
			var aliveVirtualMaps = 0;
			var aliveHandlers = 0;
			var aliveMauiContexts = 0;
			var alivePayloadProviders = 0;
			var alivePayloadServices = 0;
			var alivePayloadBuffers = 0;
			var mapReadyBranches = 0;
			var mapElementsPosts = 0;
			var cyclesWithNoPendingMapReadyCallback = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativePeer.TryGetTarget(out _))
					aliveNativePeers++;

				if (cycle.VirtualMap.TryGetTarget(out _))
					aliveVirtualMaps++;

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

				if (cycle.MapBranchWasReady)
					mapReadyBranches++;

				if (cycle.TriggeredMapElementsPost)
					mapElementsPosts++;

				if (cycle.NoPendingMapReadyCallback)
					cyclesWithNoPendingMapReadyCallback++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativePeers,
				aliveVirtualMaps,
				aliveHandlers,
				aliveMauiContexts,
				alivePayloadProviders,
				alivePayloadServices,
				alivePayloadBuffers,
				(long)alivePayloadBuffers * PayloadBytesPerCycle,
				mapReadyBranches,
				mapElementsPosts,
				cyclesWithNoPendingMapReadyCallback);
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
		Control.MapReadyBranches == Cycles &&
		Current.MapReadyBranches == Cycles &&
		Control.CyclesWithNoPendingMapReadyCallback == Cycles &&
		Current.CyclesWithNoPendingMapReadyCallback == Cycles &&
		Control.MapElementsPosts == 0 &&
		Current.MapElementsPosts == Cycles &&
		Control.AliveHandlers == 0 &&
		Control.AliveMauiContexts == 0 &&
		Control.AlivePayloadBuffers == 0 &&
		Current.AliveHandlers == Cycles &&
		Current.AliveMauiContexts == Cycles &&
		Current.AlivePayloadBuffers == Cycles &&
		Current.RetainedPayloadBytes >= 70L * 1024 * 1024;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var controlMiB = Control.RetainedPayloadBytes / 1024d / 1024d;
		var currentMiB = Current.RetainedPayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidMapHandlerMapElementsPostRetentionRepro",
			$"Cycles per scenario: {Cycles}",
			$"Payload bytes per cycle: {PayloadBytesPerCycle:N0}",
			"Source path exercised: MapHandler.MapElements -> MapView.Post",
			"Both scenarios use a test MapHandler that skips GetMapAsync so no pending _mapReady callback exists",
			"Control skips MapElements; current MAUI queues the handler-capturing MapView.Post callback",
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
			$"  map-ready branches selected: {result.MapReadyBranches}/{result.TrackedCycles}",
			$"  cycles with no pending _mapReady callback: {result.CyclesWithNoPendingMapReadyCallback}/{result.TrackedCycles}",
			$"  MapElements posts requested: {result.MapElementsPosts}/{result.TrackedCycles}",
			$"  alive native MapView peers: {result.AliveNativePeers}/{result.TrackedCycles}",
			$"  alive virtual Maps: {result.AliveVirtualMaps}/{result.TrackedCycles}",
			$"  alive MapHandlers: {result.AliveHandlers}/{result.TrackedCycles}",
			$"  alive MauiContexts: {result.AliveMauiContexts}/{result.TrackedCycles}",
			$"  alive payload providers: {result.AlivePayloadProviders}/{result.TrackedCycles}",
			$"  alive payload services: {result.AlivePayloadServices}/{result.TrackedCycles}",
			$"  alive payload buffers: {result.AlivePayloadBuffers}/{result.TrackedCycles}",
			$"  retained scoped-service payload bytes: {result.RetainedPayloadBytes:N0}");
	}
}
