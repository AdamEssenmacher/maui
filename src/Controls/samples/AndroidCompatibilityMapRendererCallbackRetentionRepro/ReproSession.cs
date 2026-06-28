#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Android.App;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Maps.Android;
using Microsoft.Maui.Controls.Maps;

namespace AndroidCompatibilityMapRendererCallbackRetentionRepro;

internal static class ReproSession
{
	const int Attempts = 80;
	const int PayloadBytes = 1024 * 1024;

	static readonly FieldInfo RendererVirtualViewField =
		typeof(Microsoft.Maui.Controls.Handlers.Compatibility.VisualElementRenderer<Map>)
			.GetField("_virtualView", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException("VisualElementRenderer<Map>", "_virtualView");

	static readonly FieldInfo RendererMauiContextField =
		typeof(Microsoft.Maui.Controls.Handlers.Compatibility.VisualElementRenderer<Map>)
			.GetField("_mauiContext", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException("VisualElementRenderer<Map>", "_mauiContext");

	public static async Task<ReproReport> RunAsync()
	{
		var control = await RunScenarioAsync(
			"control: dispose then explicitly clear renderer virtual view and MauiContext",
			clearRendererStateAfterDispose: true);

		var current = await RunScenarioAsync(
			"current: Dispose leaves pending callback renderer state assigned",
			clearRendererStateAfterDispose: false);

		return new ReproReport(Attempts, PayloadBytes, control, current);
	}

	static async Task<RunStats> RunScenarioAsync(string name, bool clearRendererStateAfterDispose)
	{
		ForceFullGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		var nativePendingCallbacks = new List<Java.Lang.Object>(Attempts);
		var rendererRefs = new List<WeakReference<MapRenderer>>(Attempts);
		var mapRefs = new List<WeakReference<Map>>(Attempts);
		var contextRefs = new List<WeakReference<MauiContext>>(Attempts);
		var payloadRefs = new List<WeakReference<PayloadViewModel>>(Attempts);
		var bufferRefs = new List<WeakReference<byte[]>>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDisposedRendererRetainedAsPendingNativeCallback(
				nativePendingCallbacks,
				clearRendererStateAfterDispose,
				rendererRefs,
				mapRefs,
				contextRefs,
				payloadRefs,
				bufferRefs,
				i);

			if (i % 10 == 0)
				await Task.Yield();
		}

		ForceFullGc();
		await Task.Delay(250);
		ForceFullGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		GC.KeepAlive(nativePendingCallbacks);

		return new RunStats(
			name,
			Attempts,
			nativePendingCallbacks.Count,
			CountAlive(rendererRefs),
			CountAlive(mapRefs),
			CountAlive(contextRefs),
			CountAlive(payloadRefs),
			CountAlive(bufferRefs),
			heapBefore,
			heapAfter);
	}

	static void CreateDisposedRendererRetainedAsPendingNativeCallback(
		List<Java.Lang.Object> nativePendingCallbacks,
		bool clearRendererStateAfterDispose,
		List<WeakReference<MapRenderer>> rendererRefs,
		List<WeakReference<Map>> mapRefs,
		List<WeakReference<MauiContext>> contextRefs,
		List<WeakReference<PayloadViewModel>> payloadRefs,
		List<WeakReference<byte[]>> bufferRefs,
		int index)
	{
		var payload = new PayloadViewModel(index, PayloadBytes);
		var map = new Map
		{
			BindingContext = payload
		};
		var serviceProvider = new PayloadServiceProvider(payload);
		var mauiContext = new MauiContext(serviceProvider);
		var renderer = new MapRenderer(Android.App.Application.Context);

		// The real handler setup path stores these fields through SetVirtualView/SetMauiContext.
		// Avoiding MapView.GetMapAsync here keeps the repro independent of Google Play readiness.
		RendererVirtualViewField.SetValue(renderer, map);
		RendererMauiContextField.SetValue(renderer, mauiContext);
		map.Handler = renderer;

		// Native MapView.GetMapAsync retains the callback object until map readiness. For the
		// compatibility renderer, that callback object is the renderer itself.
		nativePendingCallbacks.Add(renderer);

		renderer.Dispose();

		if (clearRendererStateAfterDispose)
		{
			if (map.Handler == renderer)
				map.Handler = null;

			RendererVirtualViewField.SetValue(renderer, null);
			RendererMauiContextField.SetValue(renderer, null);
		}

		rendererRefs.Add(new WeakReference<MapRenderer>(renderer));
		mapRefs.Add(new WeakReference<Map>(map));
		contextRefs.Add(new WeakReference<MauiContext>(mauiContext));
		payloadRefs.Add(new WeakReference<PayloadViewModel>(payload));
		bufferRefs.Add(new WeakReference<byte[]>(payload.Bytes));

		renderer = null!;
		mauiContext = null!;
		serviceProvider = null!;
		map = null!;
		payload = null!;
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

	sealed class PayloadServiceProvider : IServiceProvider
	{
		readonly PayloadViewModel _payload;

		public PayloadServiceProvider(PayloadViewModel payload)
		{
			_payload = payload;
		}

		public object? GetService(Type serviceType)
		{
			if (serviceType == typeof(PayloadViewModel))
				return _payload;

			return null;
		}
	}

	sealed class PayloadViewModel
	{
		public PayloadViewModel(int index, int size)
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
	int Attempts,
	int NativePendingCallbacks,
	int AliveRenderers,
	int AliveMaps,
	int AliveMauiContexts,
	int AlivePayloads,
	int AlivePayloadBuffers,
	long HeapBefore,
	long HeapAfter);

public sealed record ReproReport(
	int Attempts,
	int PayloadBytes,
	RunStats Control,
	RunStats Current)
{
	public bool LeakProved =>
		Control.NativePendingCallbacks == Attempts &&
		Control.AliveRenderers == Attempts &&
		Control.AliveMaps == 0 &&
		Control.AliveMauiContexts == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AlivePayloadBuffers == 0 &&
		Current.NativePendingCallbacks == Attempts &&
		Current.AliveRenderers == Attempts &&
		Current.AliveMaps == Attempts &&
		Current.AliveMauiContexts == Attempts &&
		Current.AlivePayloads == Attempts &&
		Current.AlivePayloadBuffers == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidCompatibilityMapRendererCallbackRetentionRepro",
			$"Disposed compatibility MapRenderer callbacks retained by native queue: {Attempts}",
			$"Payload per map/context: {FormatBytes(PayloadBytes)}",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current));
	}

	string Format(RunStats stats)
	{
		var retainedPayloadBytes = (long)stats.AlivePayloadBuffers * PayloadBytes;
		var totalPayloadBytes = (long)stats.Attempts * PayloadBytes;

		return string.Join(Environment.NewLine,
			$"Run: {stats.Name}",
			$"  native-pending renderer callbacks retained: {stats.NativePendingCallbacks}/{stats.Attempts}",
			$"  renderers alive after full GC: {stats.AliveRenderers}/{stats.Attempts}",
			$"  maps alive after full GC: {stats.AliveMaps}/{stats.Attempts}",
			$"  MauiContexts alive after full GC: {stats.AliveMauiContexts}/{stats.Attempts}",
			$"  payload view models alive after full GC: {stats.AlivePayloads}/{stats.Attempts}",
			$"  payload byte arrays alive after full GC: {stats.AlivePayloadBuffers}/{stats.Attempts}",
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
