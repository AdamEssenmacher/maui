#if IOS || MACCATALYST
using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Maps;
using ControlsMap = Microsoft.Maui.Controls.Maps.Map;
using MapRenderer = Microsoft.Maui.Controls.Compatibility.Maps.iOS.MapRenderer;

namespace CompatibilityMapRendererResetRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadMegabytesPerContext = 1;

	static readonly List<MapElement> RetainedRemovedElements = new();

	static readonly FieldInfo TrackedMapElementsField =
		typeof(MapRenderer).GetField("_trackedMapElements", BindingFlags.Instance | BindingFlags.NonPublic)!;

	static readonly MethodInfo RendererMapElementPropertyChangedMethod =
		typeof(MapRenderer).GetMethod("MapElementPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;

	static readonly MethodInfo CoreMapElementPropertyChangedMethod =
		typeof(ControlsMap).GetMethod("MapElementPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;

	public static readonly string ResultsPath =
		"/tmp/compatibility-maprenderer-reset-retention-results.txt";

	public static async Task<ReproReport> RunAsync(IMauiContext rootContext)
	{
		RetainedRemovedElements.Clear();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenario(
			"control: clear map elements, detach core and renderer subscriptions, then dispose",
			rootContext,
			detachRendererMapElementSubscriptions: true);

		var current = await RunScenario(
			"current: clear map elements, detach only the core Map subscription, then dispose",
			rootContext,
			detachRendererMapElementSubscriptions: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		return new ReproReport(
			Cycles,
			PayloadMegabytesPerContext,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenario(
		string name,
		IMauiContext rootContext,
		bool detachRendererMapElementSubscriptions)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
			await CreateClearedRendererCycle(i, rootContext, tracked, detachRendererMapElementSubscriptions);

		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	static async Task CreateClearedRendererCycle(
		int cycle,
		IMauiContext rootContext,
		List<TrackedCycle> tracked,
		bool detachRendererMapElementSubscriptions)
	{
		var map = new ControlsMap(
			MapSpan.FromCenterAndRadius(
				new Location(47.6205 + (cycle * 0.001), -122.3493),
				Distance.FromMiles(0.5)));

		var removedElement = CreateMapElement(cycle);
		map.MapElements.Add(removedElement);

		var contextPayload = new ContextPayload(
			cycle,
			PayloadMegabytesPerContext * 1024L * 1024L);
		var scopedContext = CreateScopedContext(rootContext, contextPayload);

		var renderer = new MapRenderer();
		var handler = (IElementHandler)renderer;
		handler.SetMauiContext(scopedContext);
		renderer.SetElement(map);
		await Task.Delay(1);

		map.MapElements.Clear();
		await Task.Delay(1);

		DetachCoreMapElementSubscriptions(map, removedElement);
		RetainedRemovedElements.Add(removedElement);

		if (detachRendererMapElementSubscriptions)
			DetachRendererMapElementSubscriptions(renderer, removedElement);

		renderer.Dispose();

		tracked.Add(TrackedCycle.Create(cycle, renderer, scopedContext, contextPayload, removedElement));
		await Task.Delay(1);
	}

	static IMauiContext CreateScopedContext(IMauiContext rootContext, ContextPayload payload)
	{
		var services = new ServiceCollection();
		services.AddSingleton<IMauiHandlersFactory>(rootContext.Handlers);
		services.AddSingleton(payload);
		return new MauiContext(services.BuildServiceProvider());
	}

	static MapElement CreateMapElement(int cycle)
	{
		var element = new Polyline
		{
			StrokeColor = Colors.DeepSkyBlue,
			StrokeWidth = 4,
		};

		var latitude = 47.6205 + (cycle * 0.001);
		var longitude = -122.3493;
		element.Geopath.Add(new Location(latitude, longitude));
		element.Geopath.Add(new Location(latitude + 0.0005, longitude + 0.0005));

		return element;
	}

	static void DetachCoreMapElementSubscriptions(ControlsMap map, MapElement element)
	{
		var handler = (PropertyChangedEventHandler)Delegate.CreateDelegate(
			typeof(PropertyChangedEventHandler),
			map,
			CoreMapElementPropertyChangedMethod);

		element.PropertyChanged -= handler;
	}

	static void DetachRendererMapElementSubscriptions(MapRenderer renderer, MapElement element)
	{
		var handler = (PropertyChangedEventHandler)Delegate.CreateDelegate(
			typeof(PropertyChangedEventHandler),
			renderer,
			RendererMapElementPropertyChangedMethod);

		element.PropertyChanged -= handler;
		element.MapElementId = null;
		TrackedMapElementsField.SetValue(renderer, null);
	}

	static int GetTrackedMapElementsCount(MapRenderer renderer)
	{
		return TrackedMapElementsField.GetValue(renderer) is System.Collections.ICollection collection
			? collection.Count
			: 0;
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

	internal sealed class ContextPayload
	{
		public ContextPayload(int cycle, long payloadBytes)
		{
			Cycle = cycle;
			PayloadBytes = payloadBytes;
			TileBytes = new byte[payloadBytes];

			for (var i = 0; i < TileBytes.Length; i += 4096)
				TileBytes[i] = (byte)(cycle + i);

			RouteMetadata = Enumerable.Range(1, 16)
				.Select(index => new RouteState(
					$"dispatch-route-{cycle + 1:000}-{index:000}",
					$"Segment {index}",
					$"Window scoped route overlay state {cycle + 1}.{index}"))
				.ToArray();
		}

		public int Cycle { get; }

		public long PayloadBytes { get; }

		public byte[] TileBytes { get; }

		public IReadOnlyList<RouteState> RouteMetadata { get; }
	}

	internal sealed record RouteState(string Id, string Title, string UiState);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference Renderer,
		WeakReference ScopedContext,
		WeakReference ContextPayload,
		WeakReference RemovedElement,
		long PayloadBytes)
	{
		public static TrackedCycle Create(
			int cycle,
			MapRenderer renderer,
			IMauiContext scopedContext,
			ContextPayload payload,
			MapElement removedElement)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference(renderer),
				new WeakReference(scopedContext),
				new WeakReference(payload),
				new WeakReference(removedElement),
				payload.PayloadBytes);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int IntentionallyRetainedRemovedElements,
		int AliveRenderers,
		int AliveScopedContexts,
		int AliveContextPayloads,
		int AliveRemovedElements,
		int RendererTrackedMapElements,
		long RetainedPayloadBytes)
	{
		public static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> cycles)
		{
			var aliveRenderers = 0;
			var aliveScopedContexts = 0;
			var aliveContextPayloads = 0;
			var aliveRemovedElements = 0;
			var rendererTrackedMapElements = 0;
			long retainedPayloadBytes = 0;

			foreach (var cycle in cycles)
			{
				if (cycle.Renderer.Target is MapRenderer renderer)
				{
					aliveRenderers++;
					rendererTrackedMapElements += GetTrackedMapElementsCount(renderer);
				}

				if (cycle.ScopedContext.IsAlive)
					aliveScopedContexts++;

				if (cycle.RemovedElement.IsAlive)
					aliveRemovedElements++;

				if (cycle.ContextPayload.IsAlive)
				{
					aliveContextPayloads++;
					retainedPayloadBytes += cycle.PayloadBytes;
				}
			}

			return new ScenarioResult(
				name,
				cycles.Count,
				cycles.Count,
				aliveRenderers,
				aliveScopedContexts,
				aliveContextPayloads,
				aliveRemovedElements,
				rendererTrackedMapElements,
				retainedPayloadBytes);
		}
	}

	internal sealed record ReproReport(
		int Cycles,
		int PayloadMegabytesPerContext,
		long BaselineManagedBytes,
		long FinalManagedBytes,
		ScenarioResult Control,
		ScenarioResult Current)
	{
		public bool Proven =>
			Control.IntentionallyRetainedRemovedElements == Cycles &&
			Control.AliveRemovedElements == Cycles &&
			Control.AliveRenderers == 0 &&
			Control.AliveScopedContexts == 0 &&
			Control.AliveContextPayloads == 0 &&
			Control.RendererTrackedMapElements == 0 &&
			Current.IntentionallyRetainedRemovedElements == Cycles &&
			Current.AliveRemovedElements == Cycles &&
			Current.AliveRenderers == Cycles &&
			Current.AliveScopedContexts == Cycles &&
			Current.AliveContextPayloads == Cycles &&
			Current.RendererTrackedMapElements == 0;

		public string ToText()
		{
			return string.Join(Environment.NewLine, new[]
			{
				"Compatibility MapRenderer reset retention repro",
				$"RESULT: {(Proven ? "PROVEN" : "NOT PROVEN")}",
				$"cycles={Cycles}",
				$"payloadMegabytesPerContext={PayloadMegabytesPerContext}",
				$"baselineManagedBytes={BaselineManagedBytes}",
				$"finalManagedBytes={FinalManagedBytes}",
				$"managedHeapDeltaMiB={(FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d:F1}",
				Format(Control),
				Format(Current),
			});
		}

		static string Format(ScenarioResult result)
		{
			return string.Join(Environment.NewLine, new[]
			{
				$"scenario={result.Name}",
				$"  trackedCycles={result.TrackedCycles}",
				$"  intentionallyRetainedRemovedElements={result.IntentionallyRetainedRemovedElements}",
				$"  aliveRemovedElements={result.AliveRemovedElements}/{result.TrackedCycles}",
				$"  aliveRenderers={result.AliveRenderers}/{result.TrackedCycles}",
				$"  aliveScopedContexts={result.AliveScopedContexts}/{result.TrackedCycles}",
				$"  aliveContextPayloads={result.AliveContextPayloads}/{result.TrackedCycles}",
				$"  rendererTrackedMapElements={result.RendererTrackedMapElements}",
				$"  retainedPayloadBytes={result.RetainedPayloadBytes}",
				$"  retainedPayloadMiB={result.RetainedPayloadBytes / 1024d / 1024d:F1}",
			});
		}
	}
}
#else
namespace CompatibilityMapRendererResetRetentionRepro;

internal static class ReproSession
{
	public static readonly string ResultsPath =
		"/tmp/compatibility-maprenderer-reset-retention-results.txt";

	public static Task<ReproReport> RunAsync(Microsoft.Maui.IMauiContext context)
	{
		return Task.FromResult(new ReproReport());
	}

	internal sealed record ReproReport
	{
		public string ToText() => "Compatibility MapRenderer reset retention repro requires iOS or Mac Catalyst.";
	}
}
#endif
