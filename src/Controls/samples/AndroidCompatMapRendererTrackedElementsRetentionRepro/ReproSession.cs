#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Maui;
using Microsoft.Maui.Controls.Compatibility.Maps.Android;
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Devices.Sensors;

namespace AndroidCompatMapRendererTrackedElementsRetentionRepro;

internal static class ReproSession
{
	const int RemovedElements = 160;
	const int PayloadBytes = 512 * 1024;

	static readonly FieldInfo RendererVirtualViewField =
		typeof(Microsoft.Maui.Controls.Handlers.Compatibility.VisualElementRenderer<Map>)
			.GetField("_virtualView", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException("VisualElementRenderer<Map>", "_virtualView");

	static readonly FieldInfo RendererMauiContextField =
		typeof(Microsoft.Maui.Controls.Handlers.Compatibility.VisualElementRenderer<Map>)
			.GetField("_mauiContext", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException("VisualElementRenderer<Map>", "_mauiContext");

	static readonly FieldInfo TrackedMapElementsField =
		typeof(MapRenderer).GetField("_trackedMapElements", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(nameof(MapRenderer), "_trackedMapElements");

	static readonly MethodInfo AddMapElementsMethod =
		typeof(MapRenderer).GetMethod("AddMapElements", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(nameof(MapRenderer), "AddMapElements");

	static readonly MethodInfo RemoveMapElementsMethod =
		typeof(MapRenderer).GetMethod("RemoveMapElements", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(nameof(MapRenderer), "RemoveMapElements");

	public static async Task<ReproReport> RunAsync()
	{
		var control = await RunScenarioAsync(
			"control: remove overlays then explicitly clear renderer _trackedMapElements",
			clearTrackedElementsAfterRemove: true);

		var current = await RunScenarioAsync(
			"current: RemoveMapElements leaves removed overlays in _trackedMapElements",
			clearTrackedElementsAfterRemove: false);

		return new ReproReport(RemovedElements, PayloadBytes, control, current);
	}

	static async Task<RunStats> RunScenarioAsync(string name, bool clearTrackedElementsAfterRemove)
	{
		ForceFullGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		var liveRenderers = new List<MapRenderer>(1);
		var elementRefs = new List<WeakReference<Polyline>>(RemovedElements);
		var payloadRefs = new List<WeakReference<RoutePayload>>(RemovedElements);
		var bufferRefs = new List<WeakReference<byte[]>>(RemovedElements);

		CreateLiveRendererWithRemovedTrackedElements(
			liveRenderers,
			clearTrackedElementsAfterRemove,
			elementRefs,
			payloadRefs,
			bufferRefs);

		ForceFullGc();
		await Task.Delay(250);
		ForceFullGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var trackedCount = GetTrackedMapElementCount(liveRenderers[0]);
		GC.KeepAlive(liveRenderers);

		return new RunStats(
			name,
			RemovedElements,
			trackedCount,
			CountAlive(elementRefs),
			CountAlive(payloadRefs),
			CountAlive(bufferRefs),
			heapBefore,
			heapAfter);
	}

	static void CreateLiveRendererWithRemovedTrackedElements(
		List<MapRenderer> liveRenderers,
		bool clearTrackedElementsAfterRemove,
		List<WeakReference<Polyline>> elementRefs,
		List<WeakReference<RoutePayload>> payloadRefs,
		List<WeakReference<byte[]>> bufferRefs)
	{
		var renderer = new MapRenderer(Android.App.Application.Context);
		var map = new Map();
		var mauiContext = new MauiContext(new EmptyServiceProvider());
		var removedElements = new List<MapElement>(RemovedElements);

		RendererVirtualViewField.SetValue(renderer, map);
		RendererMauiContextField.SetValue(renderer, mauiContext);
		map.Handler = renderer;
		liveRenderers.Add(renderer);

		for (var i = 0; i < RemovedElements; i++)
		{
			var payload = new RoutePayload(i, PayloadBytes);
			var polyline = CreateRoutePolyline(i, payload);

			removedElements.Add(polyline);
			elementRefs.Add(new WeakReference<Polyline>(polyline));
			payloadRefs.Add(new WeakReference<RoutePayload>(payload));
			bufferRefs.Add(new WeakReference<byte[]>(payload.Bytes));

			payload = null!;
			polyline = null!;
		}

		// These are the real compatibility renderer add/remove paths. They do not need a ready
		// GoogleMap to demonstrate the managed tracking-list root: AddMapElements appends to
		// _trackedMapElements before it tries to create native overlay objects.
		AddMapElementsMethod.Invoke(renderer, new object[] { removedElements });
		RemoveMapElementsMethod.Invoke(renderer, new object[] { removedElements });

		if (clearTrackedElementsAfterRemove)
			TrackedMapElementsField.SetValue(renderer, null);

		removedElements = null!;
		map = null!;
		mauiContext = null!;
		renderer = null!;
	}

	static Polyline CreateRoutePolyline(int index, RoutePayload payload)
	{
		var polyline = new Polyline
		{
			BindingContext = payload,
			StrokeWidth = 4
		};

		var latitude = 47.6205 + (index * 0.0001);
		var longitude = -122.3493 - (index * 0.0001);
		for (var i = 0; i < 8; i++)
			polyline.Geopath.Add(new Location(latitude + (i * 0.001), longitude - (i * 0.001)));

		return polyline;
	}

	static int GetTrackedMapElementCount(MapRenderer renderer)
	{
		if (TrackedMapElementsField.GetValue(renderer) is ICollection collection)
			return collection.Count;

		return 0;
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

	static int CountAlive<T>(IEnumerable<WeakReference<T>> weakReferences)
		where T : class
	{
		var count = 0;
		foreach (var weakReference in weakReferences)
		{
			if (weakReference.TryGetTarget(out _))
				count++;
		}

		return count;
	}

	sealed class EmptyServiceProvider : IServiceProvider
	{
		public object? GetService(Type serviceType) => null;
	}

	sealed class RoutePayload
	{
		public RoutePayload(int index, int size)
		{
			Bytes = new byte[size];
			for (var i = 0; i < Bytes.Length; i += 4096)
				Bytes[i] = (byte)(index + i);
		}

		public byte[] Bytes { get; }
	}
}

public sealed record RunStats(
	string Name,
	int RemovedElements,
	int TrackedElementsInRenderer,
	int AliveRemovedElements,
	int AlivePayloads,
	int AlivePayloadBuffers,
	long HeapBefore,
	long HeapAfter);

public sealed record ReproReport(
	int RemovedElements,
	int PayloadBytes,
	RunStats Control,
	RunStats Current)
{
	public bool LeakProved =>
		Control.TrackedElementsInRenderer == 0 &&
		Control.AliveRemovedElements == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AlivePayloadBuffers == 0 &&
		Current.TrackedElementsInRenderer == RemovedElements &&
		Current.AliveRemovedElements == RemovedElements &&
		Current.AlivePayloads == RemovedElements &&
		Current.AlivePayloadBuffers == RemovedElements;

	public string ToText()
	{
		var text = new StringBuilder();
		text.AppendLine("AndroidCompatMapRendererTrackedElementsRetentionRepro");
		text.AppendLine($"Removed route overlay models: {RemovedElements}");
		text.AppendLine($"Payload per removed overlay: {FormatBytes(PayloadBytes)}");
		text.AppendLine($"Leak proved: {LeakProved}");
		text.AppendLine();
		text.AppendLine(Format(Control));
		text.AppendLine();
		text.AppendLine(Format(Current));

		if (LeakProved)
			text.AppendLine("RESULT: PROVEN");
		else
			text.AppendLine("RESULT: NOT PROVEN");

		return text.ToString();
	}

	string Format(RunStats stats)
	{
		var retainedPayloadBytes = (long)stats.AlivePayloadBuffers * PayloadBytes;
		var totalPayloadBytes = (long)stats.RemovedElements * PayloadBytes;

		return string.Join(Environment.NewLine,
			$"Run: {stats.Name}",
			$"  renderer _trackedMapElements count: {stats.TrackedElementsInRenderer}/{stats.RemovedElements}",
			$"  removed Polyline models alive after full GC: {stats.AliveRemovedElements}/{stats.RemovedElements}",
			$"  route payloads alive after full GC: {stats.AlivePayloads}/{stats.RemovedElements}",
			$"  route payload byte arrays alive after full GC: {stats.AlivePayloadBuffers}/{stats.RemovedElements}",
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
