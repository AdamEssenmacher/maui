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
using Microsoft.Maui.Maps.Platform;
using ControlsMap = Microsoft.Maui.Controls.Maps.Map;
using ControlsMapElement = Microsoft.Maui.Controls.Maps.MapElement;

namespace IosMapElementRemovedHandlerRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 80;
	internal const int PayloadBytesPerContext = 1024 * 1024;

	static readonly List<ControlsMapElement> RetainedRemovedElements = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-mapelement-removed-handler-retention-results.txt");

	public static ReproReport Run()
	{
		WriteProgress("Starting iOS MapElement removed-handler retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = RunScenario(
			"control: disconnect removed map element handler",
			clearRemovedElementState: true);

		WriteProgress("Running current MAUI scenario.");
		var current = RunScenario(
			"current: removed MapElement keeps MapElementHandler",
			clearRemovedElementState: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedRemovedElements);

		return new ReproReport(
			Cycles,
			PayloadBytesPerContext,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static ScenarioResult RunScenario(string name, bool clearRemovedElementState)
	{
		var tracked = RunScenarioCore(name, clearRemovedElementState);
		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static List<TrackedCycle> RunScenarioCore(string name, bool clearRemovedElementState)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 16 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			CreateRemovedElementCycle(i, tracked, clearRemovedElementState);
		}

		return tracked;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateRemovedElementCycle(
		int cycle,
		List<TrackedCycle> tracked,
		bool clearRemovedElementState)
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

		var removedElement = CreatePolyline(cycle);

		map.MapElements.Add(removedElement);
		ForceOverlayRenderer(mapHandler.PlatformView, removedElement);

		if (removedElement.Handler is not MapElementHandler elementHandler)
			throw new InvalidOperationException("The map overlay renderer path did not create a MapElementHandler.");

		// Use individual remove to avoid the already-known MapElements.Clear() reset
		// subscription leak. The remaining root is the removed element's own handler.
		if (!map.MapElements.Remove(removedElement))
			throw new InvalidOperationException("The test map element could not be removed.");

		if (clearRemovedElementState)
			ClearRemovedElementState(removedElement);

		((IElementHandler)mapHandler).DisconnectHandler();
		map.Handler = null;

		RetainedRemovedElements.Add(removedElement);
		tracked.Add(TrackedCycle.Create(cycle, removedElement, elementHandler, map, mapHandler, context, services, payload));
	}

	static Polyline CreatePolyline(int cycle)
	{
		var line = new Polyline
		{
			StrokeColor = Microsoft.Maui.Graphics.Colors.Red,
			StrokeWidth = 3
		};

		line.Geopath.Add(new Location(47.6062 + cycle * 0.00001, -122.3321 - cycle * 0.00001));
		line.Geopath.Add(new Location(47.6067 + cycle * 0.00001, -122.3317 - cycle * 0.00001));

		return line;
	}

	static void ForceOverlayRenderer(MauiMKMapView platformView, ControlsMapElement element)
	{
		if (element.MapElementId is not IMKOverlay overlay)
			throw new InvalidOperationException("Map.MapElements did not assign an MKOverlay MapElementId.");

		var renderer = platformView.OverlayRenderer?.Invoke(platformView, overlay);
		if (renderer is null)
			throw new InvalidOperationException("The map overlay renderer delegate did not return a renderer.");

		GC.KeepAlive(renderer);
	}

	static void ClearRemovedElementState(ControlsMapElement element)
	{
		element.Handler?.DisconnectHandler();
		element.Handler = null;
		element.MapElementId = null;
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
			tokens[i] = $"ios-map-element-context-token-{cycle:D4}-{i:D2}";

		return tokens;
	}

	static byte[] CreatePayload(int cycle, int payloadBytes)
	{
		var payload = new byte[payloadBytes];
		for (var i = 0; i < payload.Length; i += 4096)
			payload[i] = (byte)(0x37 + cycle + i);

		return payload;
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<ControlsMapElement> RemovedElement,
		WeakReference<MapElementHandler> RemovedElementHandler,
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
			ControlsMapElement removedElement,
			MapElementHandler removedElementHandler,
			ControlsMap map,
			MapHandler mapHandler,
			MauiContext context,
			IServiceProvider serviceProvider,
			PayloadService payload)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<ControlsMapElement>(removedElement),
				new WeakReference<MapElementHandler>(removedElementHandler),
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
		int AliveRemovedElements,
		int RemovedElementsWithHandler,
		int RemovedElementsWithMapElementId,
		int AliveRemovedElementHandlers,
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
			var aliveRemovedElements = 0;
			var removedElementsWithHandler = 0;
			var removedElementsWithMapElementId = 0;
			var aliveRemovedElementHandlers = 0;
			var aliveMaps = 0;
			var aliveMapHandlers = 0;
			var aliveMauiContexts = 0;
			var aliveServiceProviders = 0;
			var alivePayloadServices = 0;
			var alivePayloadByteArrays = 0;
			long retainedPayloadBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.RemovedElement.TryGetTarget(out var removedElement))
				{
					aliveRemovedElements++;

					if (removedElement.Handler is MapElementHandler)
						removedElementsWithHandler++;

					if (removedElement.MapElementId is not null)
						removedElementsWithMapElementId++;
				}

				if (cycle.RemovedElementHandler.TryGetTarget(out _))
					aliveRemovedElementHandlers++;

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
				aliveRemovedElements,
				removedElementsWithHandler,
				removedElementsWithMapElementId,
				aliveRemovedElementHandlers,
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
		Control.AliveRemovedElements == Cycles &&
		Current.AliveRemovedElements == Cycles &&
		Control.RemovedElementsWithHandler == 0 &&
		Control.RemovedElementsWithMapElementId == 0 &&
		Control.AliveRemovedElementHandlers == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Current.RemovedElementsWithHandler == Cycles &&
		Current.RemovedElementsWithMapElementId == 0 &&
		Current.AliveRemovedElementHandlers == Cycles &&
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
			"IosMapElementRemovedHandlerRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload bytes per MauiContext service graph: {PayloadBytesPerContext:N0}",
			"Source paths mirrored: Map.MapElements collection changes, MapHandler.MapElements, MauiMKMapView.ClearMapElements/AddElements, overlay renderer creation, and ElementHandler disconnect",
			"Retained app objects: removed Polyline MapElement models only",
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
			$"  alive removed MapElements: {result.AliveRemovedElements}/{result.TrackedCycles}",
			$"  removed MapElements with Handler: {result.RemovedElementsWithHandler}/{result.TrackedCycles}",
			$"  removed MapElements with MapElementId: {result.RemovedElementsWithMapElementId}/{result.TrackedCycles}",
			$"  alive removed MapElement handlers: {result.AliveRemovedElementHandlers}/{result.TrackedCycles}",
			$"  alive Maps: {result.AliveMaps}/{result.TrackedCycles}",
			$"  alive MapHandlers: {result.AliveMapHandlers}/{result.TrackedCycles}",
			$"  alive MauiContexts: {result.AliveMauiContexts}/{result.TrackedCycles}",
			$"  alive service providers: {result.AliveServiceProviders}/{result.TrackedCycles}",
			$"  alive payload services: {result.AlivePayloadServices}/{result.TrackedCycles}",
			$"  alive payload byte arrays: {result.AlivePayloadByteArrays}/{result.TrackedCycles}",
			$"  retained payload bytes: {result.RetainedPayloadBytes:N0}");
	}
}
