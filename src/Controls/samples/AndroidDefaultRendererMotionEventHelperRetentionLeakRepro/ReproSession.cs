#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Android.Content;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.Android;
using Microsoft.Maui.Platform;
using CompatibilityPlatform = Microsoft.Maui.Controls.Compatibility.Platform.Android.Platform;
using MauiView = Microsoft.Maui.Controls.ContentView;

namespace AndroidDefaultRendererMotionEventHelperRetentionLeakRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveRenderers,
	int AliveViews,
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
		Control.AliveViews == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Current.AliveViews == Attempts &&
		Current.AlivePayloads == Attempts &&
		Current.AlivePayloadByteArrays == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidDefaultRendererMotionEventHelperRetentionLeakRepro",
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
			$"  Views alive after full GC: {stats.AliveViews}/{stats.Attempts}",
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

	static readonly Type DefaultRendererType =
		typeof(CompatibilityPlatform).GetNestedType("DefaultRenderer", BindingFlags.NonPublic)
		?? throw new MissingMemberException(nameof(CompatibilityPlatform), "DefaultRenderer");

	static readonly ConstructorInfo DefaultRendererConstructor =
		DefaultRendererType.GetConstructor(
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
			binder: null,
			types: new[] { typeof(Context) },
			modifiers: null)
		?? throw new MissingMemberException(DefaultRendererType.FullName, ".ctor(Context)");

	static readonly FieldInfo MotionEventHelperField =
		DefaultRendererType.GetField("_motionEventHelper", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(DefaultRendererType.FullName, "_motionEventHelper");

	static readonly FieldInfo MotionEventHelperElementField =
		DefaultRendererType.Assembly
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
			"control: clear MotionEventHelper._element after DefaultRenderer disposal",
			clearHelperElement: true);

		var current = await RunScenarioAsync(
			mauiContext,
			"current: dispose DefaultRenderer without clearing MotionEventHelper._element",
			clearHelperElement: false);

		ForceFullGc();
		GC.KeepAlive(RetainedNativePeerRoots);
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(IMauiContext mauiContext, string name, bool clearHelperElement)
	{
		var retainedRenderers = new List<object>(Attempts);
		var rendererRefs = new List<WeakReference<object>>(Attempts);
		var viewRefs = new List<WeakReference<MauiView>>(Attempts);
		var payloadRefs = new List<PayloadWeakReference>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDisposedRenderer(
				mauiContext,
				clearHelperElement,
				retainedRenderers,
				rendererRefs,
				viewRefs,
				payloadRefs,
				i);

			if (i % 10 == 0)
				await Task.Yield();
		}

		await Task.Delay(250);
		ForceFullGc();

		var aliveRenderers = rendererRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveViews = viewRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloads = payloadRefs.Count(static wr => wr.Payload.TryGetTarget(out _));
		var alivePayloadByteArrays = payloadRefs.Count(static wr => wr.Bytes.TryGetTarget(out _));

		RetainedNativePeerRoots.Add(retainedRenderers);
		GC.KeepAlive(retainedRenderers);

		return new RunStats(
			name,
			Attempts,
			aliveRenderers,
			aliveViews,
			alivePayloads,
			alivePayloadByteArrays,
			(long)alivePayloadByteArrays * PayloadBytes);
	}

	static void CreateDisposedRenderer(
		IMauiContext mauiContext,
		bool clearHelperElement,
		List<object> retainedRenderers,
		List<WeakReference<object>> rendererRefs,
		List<WeakReference<MauiView>> viewRefs,
		List<PayloadWeakReference> payloadRefs,
		int index)
	{
		var payload = new Payload(index, PayloadBytes);
		var view = new MauiView
		{
			BindingContext = payload,
			WidthRequest = 96,
			HeightRequest = 96
		};

		var renderer = CreateDefaultRenderer(mauiContext.Context ?? throw new InvalidOperationException("Android context is not available."));
		var contextHandler = view.ToHandler(mauiContext);

		payloadRefs.Add(new PayloadWeakReference(new WeakReference<Payload>(payload), new WeakReference<byte[]>(payload.Bytes)));
		viewRefs.Add(new WeakReference<MauiView>(view));
		rendererRefs.Add(new WeakReference<object>(renderer));
		retainedRenderers.Add(renderer);

		((IVisualElementRenderer)renderer).SetElement(view);
		((IDisposable)renderer).Dispose();
		contextHandler.DisconnectHandler();

		if (clearHelperElement)
			ClearMotionEventHelperElement(renderer);
	}

	static object CreateDefaultRenderer(Context context) =>
		DefaultRendererConstructor.Invoke(new object[] { context });

	static void ClearMotionEventHelperElement(object renderer)
	{
		var helper = MotionEventHelperField.GetValue(renderer)
			?? throw new InvalidOperationException("DefaultRenderer did not create a MotionEventHelper.");

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
