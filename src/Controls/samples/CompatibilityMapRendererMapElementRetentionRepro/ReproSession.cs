#if IOS || MACCATALYST
using System.Collections;
using System.ComponentModel;
using System.Reflection;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Maps;
using ControlsMap = Microsoft.Maui.Controls.Maps.Map;
using MapRenderer = Microsoft.Maui.Controls.Compatibility.Maps.iOS.MapRenderer;

namespace CompatibilityMapRendererMapElementRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 40;
	const int ElementsPerCycle = 3;
	const int PayloadMegabytesPerElement = 1;

	static readonly List<MapElement> RetainedSentinelElements = new();

	static readonly FieldInfo TrackedMapElementsField =
		typeof(MapRenderer).GetField("_trackedMapElements", BindingFlags.Instance | BindingFlags.NonPublic)!;

	static readonly MethodInfo RendererMapElementPropertyChangedMethod =
		typeof(MapRenderer).GetMethod("MapElementPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;

	static readonly MethodInfo CoreMapElementPropertyChangedMethod =
		typeof(ControlsMap).GetMethod("MapElementPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;

	public static readonly string ResultsPath =
		Path.Combine(Path.GetTempPath(), "compatibility-maprenderer-mapelement-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenario(
			"control: explicitly detach renderer map-element subscriptions before disposing",
			context,
			detachRendererMapElementSubscriptions: true);

		var current = await RunScenario(
			"current: dispose compatibility MapRenderer with tracked map elements still subscribed",
			context,
			detachRendererMapElementSubscriptions: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		return new ReproReport(
			Cycles,
			ElementsPerCycle,
			PayloadMegabytesPerElement,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenario(
		string name,
		IMauiContext context,
		bool detachRendererMapElementSubscriptions)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
			await CreateDisposedRendererCycle(i, context, tracked, detachRendererMapElementSubscriptions);

		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	static async Task CreateDisposedRendererCycle(
		int cycle,
		IMauiContext context,
		List<TrackedCycle> tracked,
		bool detachRendererMapElementSubscriptions)
	{
		using var pool = new NSAutoreleasePool();

		var map = new ControlsMap(
			MapSpan.FromCenterAndRadius(
				new Location(47.6205 + (cycle * 0.001), -122.3493),
				Distance.FromMiles(0.5)));

		var elements = CreateMapElements(cycle);
		foreach (var element in elements)
			map.MapElements.Add(element);

		var renderer = new MapRenderer();
		var handler = (IElementHandler)renderer;
		handler.SetMauiContext(context);
		renderer.SetElement(map);
		await Task.Delay(1);

		DetachCoreMapElementSubscriptions(map, elements);
		RetainedSentinelElements.Add(elements[0]);

		if (detachRendererMapElementSubscriptions)
			DetachRendererMapElementSubscriptions(renderer);

		renderer.Dispose();
		tracked.Add(TrackedCycle.Create(cycle, renderer, map, elements));
		await Task.Delay(1);
	}

	static List<MapElement> CreateMapElements(int cycle)
	{
		var elements = new List<MapElement>(ElementsPerCycle);

		for (var i = 0; i < ElementsPerCycle; i++)
		{
			var element = new Polyline
			{
				StrokeColor = Colors.DeepSkyBlue,
				StrokeWidth = 4,
				BindingContext = new LeakPayload(
					cycle,
					i,
					PayloadMegabytesPerElement * 1024L * 1024L)
			};

			var latitude = 47.6205 + (cycle * 0.001) + (i * 0.0001);
			var longitude = -122.3493 - (i * 0.0001);
			element.Geopath.Add(new Location(latitude, longitude));
			element.Geopath.Add(new Location(latitude + 0.0005, longitude + 0.0005));

			elements.Add(element);
		}

		return elements;
	}

	static void DetachCoreMapElementSubscriptions(ControlsMap map, IReadOnlyList<MapElement> elements)
	{
		var handler = (PropertyChangedEventHandler)Delegate.CreateDelegate(
			typeof(PropertyChangedEventHandler),
			map,
			CoreMapElementPropertyChangedMethod);

		foreach (var element in elements)
			element.PropertyChanged -= handler;
	}

	static void DetachRendererMapElementSubscriptions(MapRenderer renderer)
	{
		if (TrackedMapElementsField.GetValue(renderer) is not IEnumerable trackedElements)
			return;

		var handler = (PropertyChangedEventHandler)Delegate.CreateDelegate(
			typeof(PropertyChangedEventHandler),
			renderer,
			RendererMapElementPropertyChangedMethod);

		foreach (MapElement element in trackedElements.Cast<MapElement>().ToArray())
		{
			element.PropertyChanged -= handler;
			element.MapElementId = null;
		}

		TrackedMapElementsField.SetValue(renderer, null);
	}

	static int GetTrackedMapElementsCount(MapRenderer renderer)
	{
		return TrackedMapElementsField.GetValue(renderer) is ICollection collection ? collection.Count : 0;
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

	internal sealed class LeakPayload
	{
		public LeakPayload(int cycle, int elementIndex, long payloadBytes)
		{
			Cycle = cycle;
			ElementIndex = elementIndex;
			PayloadBytes = payloadBytes;
			TileBytes = new byte[payloadBytes];

			for (var i = 0; i < TileBytes.Length; i += 4096)
				TileBytes[i] = (byte)(cycle + elementIndex + i);

			ShapeMetadata = Enumerable.Range(1, 16)
				.Select(index => new ShapeState(
					$"route-{cycle + 1:000}-{elementIndex + 1:00}-{index:000}",
					$"Segment {index}",
					$"Live route overlay state {cycle + 1}.{elementIndex + 1}.{index}"))
				.ToArray();
		}

		public int Cycle { get; }

		public int ElementIndex { get; }

		public long PayloadBytes { get; }

		public byte[] TileBytes { get; }

		public IReadOnlyList<ShapeState> ShapeMetadata { get; }
	}

	internal sealed record ShapeState(string Id, string Title, string UiState);

	internal sealed record TrackedElement(
		int ElementIndex,
		bool IsSentinel,
		WeakReference Element,
		WeakReference Payload,
		long PayloadBytes);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference Renderer,
		WeakReference Map,
		IReadOnlyList<TrackedElement> Elements)
	{
		public static TrackedCycle Create(
			int cycle,
			MapRenderer renderer,
			ControlsMap map,
			IReadOnlyList<MapElement> elements)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference(renderer),
				new WeakReference(map),
				elements.Select((element, index) => new TrackedElement(
					index,
					index == 0,
					new WeakReference(element),
					new WeakReference(element.BindingContext),
					((LeakPayload)element.BindingContext).PayloadBytes))
				.ToArray());
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int IntentionallyRetainedSentinels,
		int AliveRenderers,
		int AliveMaps,
		int AliveSentinelElements,
		int AliveSiblingElements,
		int AliveSentinelPayloads,
		int AliveSiblingPayloads,
		int RendererTrackedMapElements,
		long RetainedSiblingPayloadBytes)
	{
		public static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> cycles)
		{
			var aliveRenderers = 0;
			var aliveMaps = 0;
			var aliveSentinelElements = 0;
			var aliveSiblingElements = 0;
			var aliveSentinelPayloads = 0;
			var aliveSiblingPayloads = 0;
			var rendererTrackedMapElements = 0;
			long retainedSiblingPayloadBytes = 0;

			foreach (var cycle in cycles)
			{
				if (cycle.Renderer.Target is MapRenderer renderer)
				{
					aliveRenderers++;
					rendererTrackedMapElements += GetTrackedMapElementsCount(renderer);
				}

				if (cycle.Map.IsAlive)
					aliveMaps++;

				foreach (var element in cycle.Elements)
				{
					if (element.IsSentinel)
					{
						if (element.Element.IsAlive)
							aliveSentinelElements++;
						if (element.Payload.IsAlive)
							aliveSentinelPayloads++;
					}
					else
					{
						if (element.Element.IsAlive)
							aliveSiblingElements++;
						if (element.Payload.IsAlive)
						{
							aliveSiblingPayloads++;
							retainedSiblingPayloadBytes += element.PayloadBytes;
						}
					}
				}
			}

			return new ScenarioResult(
				name,
				cycles.Count,
				cycles.Count,
				aliveRenderers,
				aliveMaps,
				aliveSentinelElements,
				aliveSiblingElements,
				aliveSentinelPayloads,
				aliveSiblingPayloads,
				rendererTrackedMapElements,
				retainedSiblingPayloadBytes);
		}
	}

	internal sealed record ReproReport(
		int Cycles,
		int ElementsPerCycle,
		int PayloadMegabytesPerElement,
		long BaselineManagedBytes,
		long FinalManagedBytes,
		ScenarioResult Control,
		ScenarioResult Current)
	{
		public int SiblingElementsPerCycle => ElementsPerCycle - 1;

		public int TotalSiblingElements => Cycles * SiblingElementsPerCycle;

		public bool Proven =>
			Control.IntentionallyRetainedSentinels == Cycles &&
			Control.AliveSentinelElements == Cycles &&
			Control.AliveSentinelPayloads == Cycles &&
			Control.AliveRenderers == 0 &&
			Control.AliveMaps == 0 &&
			Control.AliveSiblingElements == 0 &&
			Control.AliveSiblingPayloads == 0 &&
			Control.RendererTrackedMapElements == 0 &&
			Current.IntentionallyRetainedSentinels == Cycles &&
			Current.AliveSentinelElements == Cycles &&
			Current.AliveSentinelPayloads == Cycles &&
			Current.AliveRenderers == Cycles &&
			Current.AliveMaps == 0 &&
			Current.AliveSiblingElements == TotalSiblingElements &&
			Current.AliveSiblingPayloads == TotalSiblingElements &&
			Current.RendererTrackedMapElements == Cycles * ElementsPerCycle;

		public string ToText()
		{
			return string.Join(Environment.NewLine, new[]
			{
				"Compatibility MapRenderer map-element retention repro",
				$"RESULT: {(Proven ? "PROVEN" : "NOT PROVEN")}",
				$"cycles={Cycles}",
				$"elementsPerCycle={ElementsPerCycle}",
				$"payloadMegabytesPerElement={PayloadMegabytesPerElement}",
				$"baselineManagedBytes={BaselineManagedBytes}",
				$"finalManagedBytes={FinalManagedBytes}",
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
				$"  intentionallyRetainedSentinels={result.IntentionallyRetainedSentinels}",
				$"  aliveRenderers={result.AliveRenderers}/{result.TrackedCycles}",
				$"  aliveMaps={result.AliveMaps}/{result.TrackedCycles}",
				$"  aliveSentinelElements={result.AliveSentinelElements}/{result.TrackedCycles}",
				$"  aliveSiblingElements={result.AliveSiblingElements}/{result.TrackedCycles * (ReproSession.ElementsPerCycle - 1)}",
				$"  aliveSentinelPayloads={result.AliveSentinelPayloads}/{result.TrackedCycles}",
				$"  aliveSiblingPayloads={result.AliveSiblingPayloads}/{result.TrackedCycles * (ReproSession.ElementsPerCycle - 1)}",
				$"  rendererTrackedMapElements={result.RendererTrackedMapElements}/{result.TrackedCycles * ReproSession.ElementsPerCycle}",
				$"  retainedSiblingPayloadBytes={result.RetainedSiblingPayloadBytes}",
				$"  retainedSiblingPayloadMiB={result.RetainedSiblingPayloadBytes / 1024d / 1024d:F1}",
			});
		}
	}
}
#else
namespace CompatibilityMapRendererMapElementRetentionRepro;

internal static class ReproSession
{
	public static readonly string ResultsPath =
		Path.Combine(Path.GetTempPath(), "compatibility-maprenderer-mapelement-retention-results.txt");

	public static Task<ReproReport> RunAsync(Microsoft.Maui.IMauiContext context)
	{
		return Task.FromResult(new ReproReport());
	}

	internal sealed record ReproReport
	{
		public string ToText() => "Compatibility MapRenderer map-element retention repro requires iOS or Mac Catalyst.";
	}
}
#endif
