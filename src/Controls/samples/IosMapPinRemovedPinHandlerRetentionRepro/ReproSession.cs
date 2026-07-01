#nullable enable

using System.Runtime.CompilerServices;
using Foundation;
using MapKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Hosting;
using Microsoft.Maui.Maps.Handlers;
using ControlsMap = Microsoft.Maui.Controls.Maps.Map;

namespace IosMapPinRemovedPinHandlerRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 80;
	internal const int PayloadBytesPerContext = 1024 * 1024;

	static readonly List<Pin> RetainedRemovedPins = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-mappin-removed-pin-handler-retention-results.txt");

	public static ReproReport Run()
	{
		WriteProgress("Starting iOS MapPin removed-pin handler retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = RunScenario(
			"control: disconnect removed pin handler and clear MarkerId",
			clearRemovedPinState: true);

		WriteProgress("Running current MAUI scenario.");
		var current = RunScenario(
			"current: removed Pin keeps MapPinHandler and MarkerId",
			clearRemovedPinState: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedRemovedPins);

		return new ReproReport(
			Cycles,
			PayloadBytesPerContext,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static ScenarioResult RunScenario(string name, bool clearRemovedPinState)
	{
		var tracked = RunScenarioCore(name, clearRemovedPinState);
		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static List<TrackedCycle> RunScenarioCore(string name, bool clearRemovedPinState)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 16 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			CreateRemovedPinCycle(i, tracked, clearRemovedPinState);
		}

		return tracked;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateRemovedPinCycle(
		int cycle,
		List<TrackedCycle> tracked,
		bool clearRemovedPinState)
	{
		var payload = new PayloadService(cycle, PayloadBytesPerContext);
		var services = new ServiceCollection()
			.AddSingleton(payload)
			.ConfigureMauiHandlers(handlers => handlers.AddMauiMaps())
			.BuildServiceProvider();
		var context = new MauiContext(services);

		var map = new ControlsMap();
		var mapHandler = new MapHandler();
		mapHandler.SetMauiContext(context);
		map.Handler = mapHandler;

		var removedPin = new Pin
		{
			Label = $"removed-dispatch-pin-{cycle:D4}",
			Address = $"site-{cycle:D4}",
			Location = new Location(47.6062 + cycle * 0.00001, -122.3321 - cycle * 0.00001),
			Type = PinType.Place
		};

		map.Pins.Add(removedPin);

		if (removedPin.Handler is not MapPinHandler pinHandler)
			throw new InvalidOperationException("Map.Pins did not create a MapPinHandler for the added pin.");

		if (removedPin.MarkerId is not MKPointAnnotation)
			throw new InvalidOperationException("Map.Pins did not assign an MKPointAnnotation MarkerId.");

		// The real trigger: remove the app-retained pin while the map is still connected.
		// MauiMKMapView.AddPins() removes native annotations, but it does not disconnect or
		// clear the removed pin's handler/MarkerId state.
		map.Pins.Clear();

		if (clearRemovedPinState)
			ClearRemovedPinState(removedPin);

		((IElementHandler)mapHandler).DisconnectHandler();
		map.Handler = null;

		RetainedRemovedPins.Add(removedPin);
		tracked.Add(TrackedCycle.Create(cycle, removedPin, pinHandler, map, mapHandler, context, services, payload));
	}

	static void ClearRemovedPinState(Pin pin)
	{
		pin.Handler?.DisconnectHandler();
		pin.Handler = null;
		pin.MarkerId = null;
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			using var pool = new NSAutoreleasePool();
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(100);
		}
	}

	static void WriteProgress(string message)
	{
		try
		{
			File.WriteAllText(ResultsPath, message + Environment.NewLine);
		}
		catch
		{
			// Progress output is diagnostic only; the final report write remains authoritative.
		}
	}

	internal sealed class PayloadService
	{
		public PayloadService(int cycle, int payloadBytes)
		{
			Cycle = cycle;
			PayloadBytes = payloadBytes;
			Payload = CreatePayload(cycle, payloadBytes);
			Tokens = CreateTokens(cycle);
		}

		public int Cycle { get; }

		public int PayloadBytes { get; }

		public byte[] Payload { get; }

		public IReadOnlyList<string> Tokens { get; }
	}

	static string[] CreateTokens(int cycle)
	{
		var tokens = new string[16];
		for (var i = 0; i < tokens.Length; i++)
			tokens[i] = $"ios-map-pin-context-token-{cycle:D4}-{i:D2}";

		return tokens;
	}

	static byte[] CreatePayload(int cycle, int payloadBytes)
	{
		var payload = new byte[payloadBytes];
		for (var i = 0; i < payload.Length; i += 4096)
			payload[i] = (byte)(0x31 + cycle + i);

		return payload;
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<Pin> RemovedPin,
		WeakReference<MapPinHandler> RemovedPinHandler,
		WeakReference<ControlsMap> Map,
		WeakReference<MapHandler> MapHandler,
		WeakReference<MauiContext> MauiContext,
		WeakReference<IServiceProvider> ServiceProvider,
		WeakReference<PayloadService> PayloadService,
		WeakReference<byte[]> PayloadBytes,
		long PayloadBytesPerContext)
	{
		public static TrackedCycle Create(
			int cycle,
			Pin removedPin,
			MapPinHandler removedPinHandler,
			ControlsMap map,
			MapHandler mapHandler,
			MauiContext context,
			IServiceProvider serviceProvider,
			PayloadService payload)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<Pin>(removedPin),
				new WeakReference<MapPinHandler>(removedPinHandler),
				new WeakReference<ControlsMap>(map),
				new WeakReference<MapHandler>(mapHandler),
				new WeakReference<MauiContext>(context),
				new WeakReference<IServiceProvider>(serviceProvider),
				new WeakReference<PayloadService>(payload),
				new WeakReference<byte[]>(payload.Payload),
				payload.PayloadBytes);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveRemovedPins,
		int RemovedPinsWithHandler,
		int RemovedPinsWithMarkerId,
		int AliveRemovedPinHandlers,
		int AliveMaps,
		int AliveMapHandlers,
		int AliveMauiContexts,
		int AliveServiceProviders,
		int AlivePayloadServices,
		int AlivePayloadByteArrays,
		long RetainedPayloadBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveRemovedPins = 0;
			var removedPinsWithHandler = 0;
			var removedPinsWithMarkerId = 0;
			var aliveRemovedPinHandlers = 0;
			var aliveMaps = 0;
			var aliveMapHandlers = 0;
			var aliveMauiContexts = 0;
			var aliveServiceProviders = 0;
			var alivePayloadServices = 0;
			var alivePayloadByteArrays = 0;
			long retainedPayloadBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.RemovedPin.TryGetTarget(out var removedPin))
				{
					aliveRemovedPins++;

					if (removedPin.Handler is MapPinHandler)
						removedPinsWithHandler++;

					if (removedPin.MarkerId is MKPointAnnotation)
						removedPinsWithMarkerId++;
				}

				if (cycle.RemovedPinHandler.TryGetTarget(out _))
					aliveRemovedPinHandlers++;

				if (cycle.Map.TryGetTarget(out _))
					aliveMaps++;

				if (cycle.MapHandler.TryGetTarget(out _))
					aliveMapHandlers++;

				if (cycle.MauiContext.TryGetTarget(out _))
					aliveMauiContexts++;

				if (cycle.ServiceProvider.TryGetTarget(out _))
					aliveServiceProviders++;

				if (cycle.PayloadService.TryGetTarget(out _))
					alivePayloadServices++;

				if (cycle.PayloadBytes.TryGetTarget(out _))
				{
					alivePayloadByteArrays++;
					retainedPayloadBytes += cycle.PayloadBytesPerContext;
				}
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveRemovedPins,
				removedPinsWithHandler,
				removedPinsWithMarkerId,
				aliveRemovedPinHandlers,
				aliveMaps,
				aliveMapHandlers,
				aliveMauiContexts,
				aliveServiceProviders,
				alivePayloadServices,
				alivePayloadByteArrays,
				retainedPayloadBytes);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadBytesPerContext,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.AliveRemovedPins == Cycles &&
		Current.AliveRemovedPins == Cycles &&
		Control.RemovedPinsWithHandler == 0 &&
		Control.RemovedPinsWithMarkerId == 0 &&
		Control.AliveRemovedPinHandlers == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Current.RemovedPinsWithHandler == Cycles &&
		Current.RemovedPinsWithMarkerId == Cycles &&
		Current.AliveRemovedPinHandlers == Cycles &&
		Current.AliveMauiContexts == Cycles &&
		Current.AlivePayloadByteArrays == Cycles &&
		Current.AliveMaps == 0 &&
		Current.AliveMapHandlers == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var controlMiB = Control.RetainedPayloadBytes / 1024d / 1024d;
		var currentMiB = Current.RetainedPayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosMapPinRemovedPinHandlerRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload bytes per MauiContext service graph: {PayloadBytesPerContext:N0}",
			"Source paths mirrored: Map.Pins CollectionChanged, MapHandler.MapPins, MauiMKMapView.AddPins, MapPinHandler, and ElementHandler disconnect",
			"Retained app objects: removed Pin models only",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained context payload: {controlMiB:N1} MiB",
			$"Current retained context payload: {currentMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  alive removed Pins: {result.AliveRemovedPins}/{result.TrackedCycles}",
			$"  removed Pins with Handler: {result.RemovedPinsWithHandler}/{result.TrackedCycles}",
			$"  removed Pins with MKPointAnnotation MarkerId: {result.RemovedPinsWithMarkerId}/{result.TrackedCycles}",
			$"  alive removed Pin handlers: {result.AliveRemovedPinHandlers}/{result.TrackedCycles}",
			$"  alive Maps: {result.AliveMaps}/{result.TrackedCycles}",
			$"  alive MapHandlers: {result.AliveMapHandlers}/{result.TrackedCycles}",
			$"  alive MauiContexts: {result.AliveMauiContexts}/{result.TrackedCycles}",
			$"  alive service providers: {result.AliveServiceProviders}/{result.TrackedCycles}",
			$"  alive payload services: {result.AlivePayloadServices}/{result.TrackedCycles}",
			$"  alive payload byte arrays: {result.AlivePayloadByteArrays}/{result.TrackedCycles}",
			$"  retained payload bytes: {result.RetainedPayloadBytes:N0}");
	}
}
