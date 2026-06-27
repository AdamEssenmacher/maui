#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.Android;
using Microsoft.Maui.Platform;
using LabelRenderer = Microsoft.Maui.Controls.Compatibility.Platform.Android.LabelRenderer;
using MauiLabel = Microsoft.Maui.Controls.Label;

namespace AndroidLabelRendererMotionEventHelperRetentionLeakRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveRenderers,
	int AliveLabels,
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
		Control.AliveLabels == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Current.AliveLabels == Attempts &&
		Current.AlivePayloads == Attempts &&
		Current.AlivePayloadByteArrays == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidLabelRendererMotionEventHelperRetentionLeakRepro",
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
			$"Managed heap delta: {FormatBytes(ManagedHeapFinal - ManagedHeapBaseline)}");
	}

	string Format(RunStats stats)
	{
		return string.Join(Environment.NewLine,
			$"Run: {stats.Name}",
			$"  disposed native renderers retained: {stats.AliveRenderers}/{stats.Attempts}",
			$"  Labels alive after full GC: {stats.AliveLabels}/{stats.Attempts}",
			$"  payloads alive after full GC: {stats.AlivePayloads}/{stats.Attempts}",
			$"  payload byte arrays alive after full GC: {stats.AlivePayloadByteArrays}/{stats.Attempts}",
			$"  retained payload bytes: {FormatBytes(stats.RetainedPayloadBytes)} ({stats.RetainedPayloadBytes * 100.0 / (PayloadBytes * stats.Attempts):0.0}%)");
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
	static readonly List<object> RetainedNativePeerRoots = new();

	static readonly FieldInfo MotionEventHelperField =
		typeof(LabelRenderer).GetField("_motionEventHelper", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(nameof(LabelRenderer), "_motionEventHelper");

	static readonly FieldInfo MotionEventHelperElementField =
		typeof(LabelRenderer).Assembly
			.GetType("Microsoft.Maui.Controls.Compatibility.Platform.Android.MotionEventHelper")
			?.GetField("_element", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException("MotionEventHelper", "_element");

	public static async Task<ReproReport> RunAsync(IMauiContext mauiContext)
	{
		await Task.Yield();

		RetainedNativePeerRoots.Clear();
		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			mauiContext,
			"control: clear MotionEventHelper._element after LabelRenderer disposal",
			clearHelperElement: true);

		var current = await RunScenarioAsync(
			mauiContext,
			"current: dispose LabelRenderer without clearing MotionEventHelper._element",
			clearHelperElement: false);

		ForceFullGc();
		GC.KeepAlive(RetainedNativePeerRoots);
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(IMauiContext mauiContext, string name, bool clearHelperElement)
	{
		var retainedRenderers = new List<LabelRenderer>(Attempts);
		var rendererRefs = new List<WeakReference<LabelRenderer>>(Attempts);
		var labelRefs = new List<WeakReference<MauiLabel>>(Attempts);
		var payloadRefs = new List<PayloadWeakReference>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDisposedRenderer(
				mauiContext,
				clearHelperElement,
				retainedRenderers,
				rendererRefs,
				labelRefs,
				payloadRefs,
				i);

			if (i % 10 == 0)
				await Task.Yield();
		}

		await Task.Delay(250);
		ForceFullGc();

		var aliveRenderers = rendererRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveLabels = labelRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloads = payloadRefs.Count(static wr => wr.Payload.TryGetTarget(out _));
		var alivePayloadByteArrays = payloadRefs.Count(static wr => wr.Bytes.TryGetTarget(out _));

		RetainedNativePeerRoots.Add(retainedRenderers);
		GC.KeepAlive(retainedRenderers);

		return new RunStats(
			name,
			Attempts,
			aliveRenderers,
			aliveLabels,
			alivePayloads,
			alivePayloadByteArrays,
			(long)alivePayloadByteArrays * PayloadBytes);
	}

	static void CreateDisposedRenderer(
		IMauiContext mauiContext,
		bool clearHelperElement,
		List<LabelRenderer> retainedRenderers,
		List<WeakReference<LabelRenderer>> rendererRefs,
		List<WeakReference<MauiLabel>> labelRefs,
		List<PayloadWeakReference> payloadRefs,
		int index)
	{
		var payload = new Payload(index, PayloadBytes);
		var label = new MauiLabel
		{
			BindingContext = payload,
			Text = $"Payload label {index}",
			WidthRequest = 96,
			HeightRequest = 96
		};

		var renderer = new LabelRenderer(mauiContext.Context ?? throw new InvalidOperationException("Android context is not available."));
		var contextHandler = label.ToHandler(mauiContext);

		payloadRefs.Add(new PayloadWeakReference(new WeakReference<Payload>(payload), new WeakReference<byte[]>(payload.Bytes)));
		labelRefs.Add(new WeakReference<MauiLabel>(label));
		rendererRefs.Add(new WeakReference<LabelRenderer>(renderer));
		retainedRenderers.Add(renderer);

		((IVisualElementRenderer)renderer).SetElement(label);
		renderer.Dispose();
		contextHandler.DisconnectHandler();

		if (clearHelperElement)
			ClearMotionEventHelperElement(renderer);
	}

	static void ClearMotionEventHelperElement(LabelRenderer renderer)
	{
		var helper = MotionEventHelperField.GetValue(renderer)
			?? throw new InvalidOperationException("LabelRenderer did not create a MotionEventHelper.");

		MotionEventHelperElementField.SetValue(helper, null);
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
