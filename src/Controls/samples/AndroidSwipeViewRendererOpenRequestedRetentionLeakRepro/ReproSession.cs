#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Android.Content;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.Android;

namespace AndroidSwipeViewRendererOpenRequestedRetentionLeakRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveRetainedOldSwipeViews,
	int AliveRenderers,
	int AliveNewSwipeViews,
	int AlivePayloads,
	int AlivePayloadByteArrays,
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
		Control.AliveRetainedOldSwipeViews == Attempts &&
		Control.AliveRenderers == 0 &&
		Control.AliveNewSwipeViews == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Current.AliveRetainedOldSwipeViews == Attempts &&
		Current.AliveRenderers == Attempts &&
		Current.AliveNewSwipeViews == Attempts &&
		Current.AlivePayloads == Attempts &&
		Current.AlivePayloadByteArrays == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidSwipeViewRendererOpenRequestedRetentionLeakRepro",
			$"Attempts: {Attempts}",
			$"Payload per new SwipeView: {PayloadBytes / 1024 / 1024} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Managed heap baseline: {FormatBytes(ManagedHeapBaseline)}",
			$"Managed heap final: {FormatBytes(ManagedHeapFinal)}",
			$"Managed heap delta: {FormatBytes(ManagedHeapFinal - ManagedHeapBaseline)}");
	}

	string Format(RunStats stats)
	{
		return string.Join(Environment.NewLine,
			$"Run: {stats.Name}",
			$"  app-retained old SwipeViews: {stats.AliveRetainedOldSwipeViews}/{stats.Attempts}",
			$"  reused renderers alive after full GC: {stats.AliveRenderers}/{stats.Attempts}",
			$"  newer SwipeViews alive after full GC: {stats.AliveNewSwipeViews}/{stats.Attempts}",
			$"  newer payloads alive after full GC: {stats.AlivePayloads}/{stats.Attempts}",
			$"  newer payload byte arrays alive after full GC: {stats.AlivePayloadByteArrays}/{stats.Attempts}",
			$"  retained newer payload bytes: {FormatBytes(stats.RetainedPayloadBytes)} ({stats.RetainedPayloadBytes * 100.0 / (PayloadBytes * stats.Attempts):0.0}%)");
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
	const int Attempts = 80;
	const int PayloadBytes = 1024 * 1024;

	static readonly MethodInfo OnOpenRequestedMethod =
		typeof(SwipeViewRenderer).GetMethod("OnOpenRequested", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(nameof(SwipeViewRenderer), "OnOpenRequested");

	public static async Task<ReproReport> RunAsync(IMauiContext mauiContext)
	{
		await Task.Yield();

		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			mauiContext,
			"control: renderer reuse then detach old OpenRequested",
			detachOldOpenRequested: true);

		var current = await RunScenarioAsync(
			mauiContext,
			"current: renderer reuse leaves old OpenRequested",
			detachOldOpenRequested: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(IMauiContext mauiContext, string name, bool detachOldOpenRequested)
	{
		var retainedOldSwipeViews = new List<SwipeView>(Attempts);
		var oldSwipeViewRefs = new List<WeakReference<SwipeView>>(Attempts);
		var rendererRefs = new List<WeakReference<SwipeViewRenderer>>(Attempts);
		var newSwipeViewRefs = new List<WeakReference<SwipeView>>(Attempts);
		var payloadRefs = new List<PayloadWeakReference>(Attempts);

		var context = mauiContext.Context ?? throw new InvalidOperationException("Android context is not available.");

		for (var i = 0; i < Attempts; i++)
		{
			CreateReusedRenderer(
				context,
				detachOldOpenRequested,
				retainedOldSwipeViews,
				oldSwipeViewRefs,
				rendererRefs,
				newSwipeViewRefs,
				payloadRefs,
				i);

			if (i % 10 == 0)
				await Task.Yield();
		}

		await Task.Yield();
		ForceFullGc();
		GC.KeepAlive(retainedOldSwipeViews);

		var aliveOldSwipeViews = oldSwipeViewRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveRenderers = rendererRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveNewSwipeViews = newSwipeViewRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloads = payloadRefs.Count(static wr => wr.Payload.TryGetTarget(out _));
		var alivePayloadByteArrays = payloadRefs.Count(static wr => wr.Bytes.TryGetTarget(out _));

		return new RunStats(
			name,
			Attempts,
			aliveOldSwipeViews,
			aliveRenderers,
			aliveNewSwipeViews,
			alivePayloads,
			alivePayloadByteArrays,
			(long)alivePayloadByteArrays * PayloadBytes);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateReusedRenderer(
		Context context,
		bool detachOldOpenRequested,
		List<SwipeView> retainedOldSwipeViews,
		List<WeakReference<SwipeView>> oldSwipeViewRefs,
		List<WeakReference<SwipeViewRenderer>> rendererRefs,
		List<WeakReference<SwipeView>> newSwipeViewRefs,
		List<PayloadWeakReference> payloadRefs,
		int index)
	{
		var oldSwipeView = new SwipeView
		{
			Content = new Label { Text = $"Cached old SwipeView {index}" }
		};

		var payload = new Payload(index, PayloadBytes);
		var newSwipeView = new SwipeView
		{
			Content = new Label { Text = $"New SwipeView {index}" },
			BindingContext = payload
		};

		var renderer = new SwipeViewRenderer(context);
		var visualRenderer = (IVisualElementRenderer)renderer;

		visualRenderer.SetElement(oldSwipeView);
		visualRenderer.SetElement(newSwipeView);

		if (detachOldOpenRequested)
		{
			var openRequestedHandler = (EventHandler<OpenRequestedEventArgs>)Delegate.CreateDelegate(
				typeof(EventHandler<OpenRequestedEventArgs>),
				renderer,
				OnOpenRequestedMethod);
			oldSwipeView.OpenRequested -= openRequestedHandler;
		}

		retainedOldSwipeViews.Add(oldSwipeView);
		oldSwipeViewRefs.Add(new WeakReference<SwipeView>(oldSwipeView));
		rendererRefs.Add(new WeakReference<SwipeViewRenderer>(renderer));
		newSwipeViewRefs.Add(new WeakReference<SwipeView>(newSwipeView));
		payloadRefs.Add(new PayloadWeakReference(new WeakReference<Payload>(payload), new WeakReference<byte[]>(payload.Bytes)));
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

	sealed record PayloadWeakReference(WeakReference<Payload> Payload, WeakReference<byte[]> Bytes);

	sealed class Payload
	{
		public Payload(int id, int byteCount)
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
}
