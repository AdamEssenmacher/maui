#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.Android;
using Microsoft.Maui.Graphics;

namespace AndroidBoxRendererMotionEventHelperRetentionLeakRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveRenderers,
	int AliveBoxViews,
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
		Control.AliveBoxViews == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Current.AliveBoxViews == Attempts &&
		Current.AlivePayloads == Attempts &&
		Current.AlivePayloadByteArrays == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidBoxRendererMotionEventHelperRetentionLeakRepro",
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
			$"  BoxViews alive after full GC: {stats.AliveBoxViews}/{stats.Attempts}",
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
		typeof(BoxRenderer).GetField("_motionEventHelper", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(nameof(BoxRenderer), "_motionEventHelper");

	static readonly FieldInfo MotionEventHelperElementField =
		typeof(BoxRenderer).Assembly
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
			"control: clear MotionEventHelper._element after BoxRenderer disposal",
			clearHelperElement: true);

		var current = await RunScenarioAsync(
			mauiContext,
			"current: dispose BoxRenderer without clearing MotionEventHelper._element",
			clearHelperElement: false);

		ForceFullGc();
		GC.KeepAlive(RetainedNativePeerRoots);
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(IMauiContext mauiContext, string name, bool clearHelperElement)
	{
		var retainedRenderers = new List<BoxRenderer>(Attempts);
		var rendererRefs = new List<WeakReference<BoxRenderer>>(Attempts);
		var boxRefs = new List<WeakReference<BoxView>>(Attempts);
		var payloadRefs = new List<PayloadWeakReference>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDisposedRenderer(
				mauiContext,
				clearHelperElement,
				retainedRenderers,
				rendererRefs,
				boxRefs,
				payloadRefs,
				i);

			if (i % 10 == 0)
				await Task.Yield();
		}

		await Task.Delay(250);
		ForceFullGc();

		var aliveRenderers = rendererRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveBoxViews = boxRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloads = payloadRefs.Count(static wr => wr.Payload.TryGetTarget(out _));
		var alivePayloadByteArrays = payloadRefs.Count(static wr => wr.Bytes.TryGetTarget(out _));

		RetainedNativePeerRoots.Add(retainedRenderers);
		GC.KeepAlive(retainedRenderers);

		return new RunStats(
			name,
			Attempts,
			aliveRenderers,
			aliveBoxViews,
			alivePayloads,
			alivePayloadByteArrays,
			(long)alivePayloadByteArrays * PayloadBytes);
	}

	static void CreateDisposedRenderer(
		IMauiContext mauiContext,
		bool clearHelperElement,
		List<BoxRenderer> retainedRenderers,
		List<WeakReference<BoxRenderer>> rendererRefs,
		List<WeakReference<BoxView>> boxRefs,
		List<PayloadWeakReference> payloadRefs,
		int index)
	{
		var payload = new Payload(index, PayloadBytes);
		var boxView = new BoxView
		{
			BindingContext = payload,
			Color = index % 2 == 0 ? Colors.CornflowerBlue : Colors.OrangeRed,
			WidthRequest = 48,
			HeightRequest = 48
		};

		var renderer = new BoxRenderer(mauiContext.Context ?? throw new InvalidOperationException("Android context is not available."));

		payloadRefs.Add(new PayloadWeakReference(new WeakReference<Payload>(payload), new WeakReference<byte[]>(payload.Bytes)));
		boxRefs.Add(new WeakReference<BoxView>(boxView));
		rendererRefs.Add(new WeakReference<BoxRenderer>(renderer));
		retainedRenderers.Add(renderer);

		((IVisualElementRenderer)renderer).SetElement(boxView);
		renderer.Dispose();

		if (clearHelperElement)
			ClearMotionEventHelperElement(renderer);
	}

	static void ClearMotionEventHelperElement(BoxRenderer renderer)
	{
		var helper = MotionEventHelperField.GetValue(renderer)
			?? throw new InvalidOperationException("BoxRenderer did not create a MotionEventHelper.");

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
