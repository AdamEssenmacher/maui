#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using Microsoft.Maui;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using AView = Android.Views.View;

namespace AndroidWebChromeClientCustomViewRetentionLeakRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveCustomViews,
	int AlivePayloads,
	int AlivePayloadByteArrays,
	int DecorChildrenAdded,
	long RetainedPayloadBytes,
	int CallbackHiddenCount);

public sealed record ReproReport(
	int Attempts,
	int PayloadBytes,
	int InitialDecorChildCount,
	RunStats Control,
	RunStats Current,
	long ManagedHeapBaseline,
	long ManagedHeapFinal)
{
	public bool LeakProved =>
		Control.AliveCustomViews == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Control.DecorChildrenAdded == 0 &&
		Current.AliveCustomViews == Attempts &&
		Current.AlivePayloads == Attempts &&
		Current.AlivePayloadByteArrays == Attempts &&
		Current.DecorChildrenAdded == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidWebChromeClientCustomViewRetentionLeakRepro",
			$"Attempts: {Attempts}",
			$"Payload per attempt: {PayloadBytes / 1024 / 1024} MiB",
			$"Initial decor child count: {InitialDecorChildCount}",
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
			$"  custom views alive after full GC: {stats.AliveCustomViews}/{stats.Attempts}",
			$"  payloads alive after full GC: {stats.AlivePayloads}/{stats.Attempts}",
			$"  payload byte arrays alive after full GC: {stats.AlivePayloadByteArrays}/{stats.Attempts}",
			$"  decor children added: {stats.DecorChildrenAdded}",
			$"  custom-view callbacks hidden: {stats.CallbackHiddenCount}/{stats.Attempts}",
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
	const int Iterations = 80;
	const int PayloadBytes = 1024 * 1024;

	public static async Task<ReproReport> RunAsync(IMauiContext mauiContext)
	{
		await Task.Yield();

		var activity = (mauiContext.Context?.GetActivity() ?? Microsoft.Maui.ApplicationModel.Platform.CurrentActivity)
			?? throw new InvalidOperationException("Activity is not available.");
		var decor = activity.Window?.DecorView as FrameLayout
			?? throw new InvalidOperationException("Activity decor view is not a FrameLayout.");

		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);
		var initialDecorChildCount = decor.ChildCount;

		var control = await RunScenarioAsync(
			mauiContext,
			activity,
			decor,
			"control: hide custom view before Disconnect",
			hideBeforeDisconnect: true,
			initialDecorChildCount);

		var current = await RunScenarioAsync(
			mauiContext,
			activity,
			decor,
			"current: Disconnect leaves custom view attached",
			hideBeforeDisconnect: false,
			initialDecorChildCount);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Iterations, PayloadBytes, initialDecorChildCount, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(
		IMauiContext mauiContext,
		Activity activity,
		FrameLayout decor,
		string name,
		bool hideBeforeDisconnect,
		int initialDecorChildCount)
	{
		var customViewRefs = new List<WeakReference<AView>>();
		var payloadRefs = new List<PayloadWeakReference>();
		var callbacks = new List<CountingCustomViewCallback>();

		for (var i = 0; i < Iterations; i++)
		{
			CreateFullscreenCustomView(mauiContext, activity, hideBeforeDisconnect, customViewRefs, payloadRefs, callbacks, i);

			if (i % 10 == 0)
				await Task.Yield();
		}

		ForceFullGc();

		var aliveCustomViews = customViewRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloads = payloadRefs.Count(static wr => wr.Payload.TryGetTarget(out _));
		var alivePayloadByteArrays = payloadRefs.Count(static wr => wr.Bytes.TryGetTarget(out _));
		var callbackHiddenCount = callbacks.Count(static callback => callback.HiddenCount > 0);
		var decorChildrenAdded = decor.ChildCount - initialDecorChildCount;

		return new RunStats(
			name,
			Iterations,
			aliveCustomViews,
			alivePayloads,
			alivePayloadByteArrays,
			decorChildrenAdded,
			(long)alivePayloadByteArrays * PayloadBytes,
			callbackHiddenCount);
	}

	static void CreateFullscreenCustomView(
		IMauiContext mauiContext,
		Context context,
		bool hideBeforeDisconnect,
		List<WeakReference<AView>> customViewRefs,
		List<PayloadWeakReference> payloadRefs,
		List<CountingCustomViewCallback> callbacks,
		int index)
	{
		var payload = new Payload(index, PayloadBytes);
		var customView = new PayloadFrameLayout(context, payload)
		{
			LayoutParameters = new FrameLayout.LayoutParams(
				ViewGroup.LayoutParams.MatchParent,
				ViewGroup.LayoutParams.MatchParent)
		};
		customView.SetBackgroundColor(Color.White);

		var callback = new CountingCustomViewCallback();
		var handler = new WebViewHandler();
		handler.SetMauiContext(mauiContext);
		var client = new MauiWebChromeClient(handler);

		customViewRefs.Add(new WeakReference<AView>(customView));
		payloadRefs.Add(new PayloadWeakReference(new WeakReference<Payload>(payload), new WeakReference<byte[]>(payload.Bytes)));
		callbacks.Add(callback);

		client.OnShowCustomView(customView, callback);

		if (hideBeforeDisconnect)
			client.OnHideCustomView();

		Disconnect(client);
	}

	static void Disconnect(MauiWebChromeClient client)
	{
		typeof(MauiWebChromeClient)
			.GetMethod("Disconnect", BindingFlags.Instance | BindingFlags.NonPublic)
			?.Invoke(client, null);
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
}

internal sealed class PayloadFrameLayout : FrameLayout
{
	public PayloadFrameLayout(Context context, Payload payload)
		: base(context)
	{
		Payload = payload;
		AddView(new TextView(context)
		{
			Text = $"Fullscreen custom view payload {payload.Index}",
			TextSize = 18,
			Gravity = GravityFlags.Center
		});
	}

	public Payload Payload { get; }
}

internal sealed class CountingCustomViewCallback : Java.Lang.Object, WebChromeClient.ICustomViewCallback
{
	public int HiddenCount { get; private set; }

	public void OnCustomViewHidden()
	{
		HiddenCount++;
	}
}

internal sealed class Payload
{
	public Payload(int index, int bytes)
	{
		Index = index;
		Bytes = new byte[bytes];

		for (var i = 0; i < Bytes.Length; i += 4096)
			Bytes[i] = (byte)(i + index);
	}

	public int Index { get; }
	public byte[] Bytes { get; }
}

internal readonly record struct PayloadWeakReference(
	WeakReference<Payload> Payload,
	WeakReference<byte[]> Bytes);
