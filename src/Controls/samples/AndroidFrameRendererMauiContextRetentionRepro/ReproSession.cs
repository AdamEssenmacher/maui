#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using AView = Android.Views.View;

namespace AndroidFrameRendererMauiContextRetentionRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveRenderers,
	int AliveFrames,
	int AliveContexts,
	int AliveProviders,
	int AlivePayloadServices,
	int AlivePayloadByteArrays,
	int RenderersWithMauiContext,
	int RenderersResolvingPayloadService,
	long RetainedPayloadBytes);

public sealed record ReproReport(
	int Attempts,
	int PayloadBytes,
	RunStats Control,
	RunStats Current,
	long ManagedHeapBaseline,
	long ManagedHeapFinal)
{
	public bool LeakProved =>
		Control.AliveContexts == 0 &&
		Control.AliveProviders == 0 &&
		Control.AlivePayloadServices == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Control.RenderersWithMauiContext == 0 &&
		Control.RenderersResolvingPayloadService == 0 &&
		Current.AliveFrames == 0 &&
		Current.AliveContexts == Attempts &&
		Current.AliveProviders == Attempts &&
		Current.AlivePayloadServices == Attempts &&
		Current.AlivePayloadByteArrays == Attempts &&
		Current.RenderersWithMauiContext == Attempts &&
		Current.RenderersResolvingPayloadService == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidFrameRendererMauiContextRetentionRepro",
			$"Attempts: {Attempts}",
			$"Payload per attempt: {PayloadBytes / 1024 / 1024} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Managed heap baseline: {FormatBytes(ManagedHeapBaseline)}",
			$"Managed heap final: {FormatBytes(ManagedHeapFinal)}",
			$"Managed heap delta: {FormatBytes(ManagedHeapFinal - ManagedHeapBaseline)}",
			LeakProved ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
	}

	string Format(RunStats stats)
	{
		return string.Join(Environment.NewLine,
			$"Run: {stats.Name}",
			$"  retained disposed native renderers: {stats.Attempts}",
			$"  renderers alive after full GC: {stats.AliveRenderers}/{stats.Attempts}",
			$"  Frames alive after full GC: {stats.AliveFrames}/{stats.Attempts}",
			$"  MauiContexts alive after full GC: {stats.AliveContexts}/{stats.Attempts}",
			$"  service providers alive after full GC: {stats.AliveProviders}/{stats.Attempts}",
			$"  payload services alive after full GC: {stats.AlivePayloadServices}/{stats.Attempts}",
			$"  payload byte arrays alive after full GC: {stats.AlivePayloadByteArrays}/{stats.Attempts}",
			$"  retained renderers with MauiContext: {stats.RenderersWithMauiContext}/{stats.Attempts}",
			$"  retained renderers resolving payload service: {stats.RenderersResolvingPayloadService}/{stats.Attempts}",
			$"  retained context payload bytes: {FormatBytes(stats.RetainedPayloadBytes)} ({stats.RetainedPayloadBytes * 100.0 / (PayloadBytes * stats.Attempts):0.0}%)");
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

internal static class ReproSession
{
	const int Attempts = 96;
	const int PayloadBytes = 1024 * 1024;

	static readonly FieldInfo FrameRendererElementField =
		typeof(FrameRenderer).GetField("_element", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(nameof(FrameRenderer), "_element");

	static readonly FieldInfo FrameRendererMauiContextField =
		typeof(FrameRenderer).GetField("_mauiContext", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(nameof(FrameRenderer), "_mauiContext");

	static readonly FieldInfo FrameRendererMotionEventHelperField =
		typeof(FrameRenderer).GetField("_motionEventHelper", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(nameof(FrameRenderer), "_motionEventHelper");

	static readonly FieldInfo MotionEventHelperElementField =
		FrameRendererMotionEventHelperField.FieldType.GetField("_element", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(FrameRendererMotionEventHelperField.FieldType.FullName, "_element");

	public static async Task<ReproReport> RunAsync(IMauiContext hostContext)
	{
		await Task.Yield();

		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			hostContext,
			"control: clear known element roots and FrameRenderer._mauiContext",
			clearMauiContext: true);

		var current = await RunScenarioAsync(
			hostContext,
			"current: clear known element roots only",
			clearMauiContext: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(IMauiContext hostContext, string name, bool clearMauiContext)
	{
		var retainedNativeRenderers = new List<AView>(Attempts);
		var rendererRefs = new List<WeakReference<FrameRenderer>>(Attempts);
		var frameRefs = new List<WeakReference<Frame>>(Attempts);
		var contextRefs = new List<WeakReference<IMauiContext>>(Attempts);
		var providerRefs = new List<WeakReference<PayloadServiceProvider>>(Attempts);
		var payloadRefs = new List<PayloadWeakReference>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDisposedRenderer(
				hostContext,
				clearMauiContext,
				retainedNativeRenderers,
				rendererRefs,
				frameRefs,
				contextRefs,
				providerRefs,
				payloadRefs,
				i);

			if (i % 12 == 0)
				await Task.Yield();
		}

		ForceFullGc();
		GC.KeepAlive(retainedNativeRenderers);

		var aliveRenderers = rendererRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveFrames = frameRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveContexts = contextRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveProviders = providerRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloadServices = payloadRefs.Count(static wr => wr.PayloadService.TryGetTarget(out _));
		var alivePayloadByteArrays = payloadRefs.Count(static wr => wr.Bytes.TryGetTarget(out _));
		var renderersWithMauiContext = rendererRefs.Count(static wr =>
			wr.TryGetTarget(out var renderer) &&
			((IElementHandler)renderer).MauiContext is not null);
		var renderersResolvingPayloadService = rendererRefs.Count(static wr =>
			wr.TryGetTarget(out var renderer) &&
			((IElementHandler)renderer).MauiContext?.Services.GetService(typeof(PayloadService)) is PayloadService);

		return new RunStats(
			name,
			Attempts,
			aliveRenderers,
			aliveFrames,
			aliveContexts,
			aliveProviders,
			alivePayloadServices,
			alivePayloadByteArrays,
			renderersWithMauiContext,
			renderersResolvingPayloadService,
			(long)alivePayloadByteArrays * PayloadBytes);
	}

	static void CreateDisposedRenderer(
		IMauiContext hostContext,
		bool clearMauiContext,
		List<AView> retainedNativeRenderers,
		List<WeakReference<FrameRenderer>> rendererRefs,
		List<WeakReference<Frame>> frameRefs,
		List<WeakReference<IMauiContext>> contextRefs,
		List<WeakReference<PayloadServiceProvider>> providerRefs,
		List<PayloadWeakReference> payloadRefs,
		int index)
	{
		var payload = new PayloadService(index, PayloadBytes);
		var provider = new PayloadServiceProvider(hostContext.Services, payload);
		var androidContext = hostContext.Context ?? throw new InvalidOperationException("Android context is not available.");
		var mauiContext = new MauiContext(provider, androidContext);
		var frame = new Frame
		{
			CornerRadius = 8,
			HasShadow = true,
			Padding = 12,
			Margin = new Thickness(4)
		};

		var renderer = new FrameRenderer(androidContext);

		payloadRefs.Add(new PayloadWeakReference(new WeakReference<PayloadService>(payload), new WeakReference<byte[]>(payload.Bytes)));
		providerRefs.Add(new WeakReference<PayloadServiceProvider>(provider));
		contextRefs.Add(new WeakReference<IMauiContext>(mauiContext));
		frameRefs.Add(new WeakReference<Frame>(frame));
		rendererRefs.Add(new WeakReference<FrameRenderer>(renderer));
		retainedNativeRenderers.Add(renderer);

		((IElementHandler)renderer).SetMauiContext(mauiContext);
		((IElementHandler)renderer).SetVirtualView(frame);

		renderer.Dispose();

		// C122 is already tracked separately. Clear those roots in both runs so only _mauiContext differs.
		ClearKnownElementRoots(renderer);

		if (clearMauiContext)
			FrameRendererMauiContextField.SetValue(renderer, null);
	}

	static void ClearKnownElementRoots(FrameRenderer renderer)
	{
		FrameRendererElementField.SetValue(renderer, null);
		var motionEventHelper = FrameRendererMotionEventHelperField.GetValue(renderer);
		if (motionEventHelper is not null)
			MotionEventHelperElementField.SetValue(motionEventHelper, null);
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

	sealed record PayloadWeakReference(WeakReference<PayloadService> PayloadService, WeakReference<byte[]> Bytes);

	sealed class PayloadService
	{
		public PayloadService(int id, int byteCount)
		{
			Id = id;
			Bytes = new byte[byteCount];
			for (var i = 0; i < Bytes.Length; i += 4096)
				Bytes[i] = (byte)((id + i) % 251);
			Bytes[^1] = (byte)((id + Bytes.Length) % 251);
		}

		public int Id { get; }

		public byte[] Bytes { get; }
	}

	sealed class PayloadServiceProvider : IServiceProvider
	{
		readonly IServiceProvider _fallback;
		readonly PayloadService _payload;

		public PayloadServiceProvider(IServiceProvider fallback, PayloadService payload)
		{
			_fallback = fallback;
			_payload = payload;
		}

		public object? GetService(Type serviceType)
		{
			if (serviceType == typeof(PayloadService))
				return _payload;

			return _fallback.GetService(serviceType);
		}
	}
}
