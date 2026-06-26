#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Android.Views;
using Android.Widget;
using AndroidX.DrawerLayout.Widget;
using Google.Android.Material.AppBar;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using AView = Android.Views.View;
using ControlsToolbar = Microsoft.Maui.Controls.Toolbar;

namespace AndroidToolbarHandlerNavigationListenerLeakRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveHandlers,
	int AliveDrawers,
	int AlivePayloadViews,
	int AlivePayloads,
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
		Control.AliveHandlers == 0 &&
		Control.AliveDrawers == 0 &&
		Control.AlivePayloadViews == 0 &&
		Control.AlivePayloads == 0 &&
		Current.AliveHandlers == Attempts &&
		Current.AliveDrawers == Attempts &&
		Current.AlivePayloadViews == Attempts &&
		Current.AlivePayloads == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidToolbarHandlerNavigationListenerLeakRepro",
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
			$"  retained native toolbars: {stats.Attempts}",
			$"  toolbar handlers alive after full GC: {stats.AliveHandlers}/{stats.Attempts}",
			$"  drawer layouts alive after full GC: {stats.AliveDrawers}/{stats.Attempts}",
			$"  drawer payload views alive after full GC: {stats.AlivePayloadViews}/{stats.Attempts}",
			$"  drawer payloads alive after full GC: {stats.AlivePayloads}/{stats.Attempts}",
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

	static readonly MethodInfo SetupWithDrawerLayoutMethod =
		typeof(ToolbarHandler).GetMethod("SetupWithDrawerLayout", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(nameof(ToolbarHandler), "SetupWithDrawerLayout");

	static readonly PropertyInfo BackNavigationClickProperty =
		typeof(ToolbarHandler).GetProperty("BackNavigationClick", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(nameof(ToolbarHandler), "BackNavigationClick");

	static readonly FieldInfo DrawerLayoutField =
		typeof(ToolbarHandler).GetField("_drawerLayout", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(nameof(ToolbarHandler), "_drawerLayout");

	static readonly FieldInfo ProcessBackClickField =
		typeof(ToolbarHandler).GetField("_processBackClick", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(nameof(ToolbarHandler), "_processBackClick");

	public static async Task<ReproReport> RunAsync(IMauiContext mauiContext)
	{
		await Task.Yield();

		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			mauiContext,
			"control: clear navigation listener and retained drawer field before disconnect",
			cleanupListener: true);

		var current = await RunScenarioAsync(
			mauiContext,
			"current: disconnect leaves native navigation listener pointing at handler",
			cleanupListener: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(IMauiContext mauiContext, string name, bool cleanupListener)
	{
		var retainedNativeToolbars = new List<MaterialToolbar>(Attempts);
		var handlerRefs = new List<WeakReference<ToolbarHandler>>(Attempts);
		var drawerRefs = new List<WeakReference<DrawerLayout>>(Attempts);
		var payloadViewRefs = new List<WeakReference<PayloadFrameLayout>>(Attempts);
		var payloadRefs = new List<WeakReference<Payload>>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDisconnectedToolbar(
				mauiContext,
				cleanupListener,
				retainedNativeToolbars,
				handlerRefs,
				drawerRefs,
				payloadViewRefs,
				payloadRefs,
				i);

			if (i % 10 == 0)
				await Task.Yield();
		}

		ForceFullGc();
		GC.KeepAlive(retainedNativeToolbars);

		var aliveHandlers = handlerRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveDrawers = drawerRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloadViews = payloadViewRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloads = payloadRefs.Count(static wr => wr.TryGetTarget(out _));

		return new RunStats(
			name,
			Attempts,
			aliveHandlers,
			aliveDrawers,
			alivePayloadViews,
			alivePayloads,
			(long)alivePayloads * PayloadBytes);
	}

	static void CreateDisconnectedToolbar(
		IMauiContext mauiContext,
		bool cleanupListener,
		List<MaterialToolbar> retainedNativeToolbars,
		List<WeakReference<ToolbarHandler>> handlerRefs,
		List<WeakReference<DrawerLayout>> drawerRefs,
		List<WeakReference<PayloadFrameLayout>> payloadViewRefs,
		List<WeakReference<Payload>> payloadRefs,
		int index)
	{
		var context = mauiContext.Context ?? Android.App.Application.Context;
		var payload = new Payload(index, PayloadBytes);
		payloadRefs.Add(new WeakReference<Payload>(payload));

		var drawer = new DrawerLayout(context);
		drawerRefs.Add(new WeakReference<DrawerLayout>(drawer));

		var payloadView = new PayloadFrameLayout(context, payload)
		{
			LayoutParameters = new ViewGroup.LayoutParams(
				ViewGroup.LayoutParams.MatchParent,
				ViewGroup.LayoutParams.MatchParent)
		};
		payloadViewRefs.Add(new WeakReference<PayloadFrameLayout>(payloadView));
		drawer.AddView(payloadView);

		var toolbar = new ControlsToolbar(new ContentPage())
		{
			Title = $"Retained toolbar {index}",
			BackButtonVisible = true,
			IsVisible = true
		};

		var handler = new ToolbarHandler();
		handler.SetMauiContext(mauiContext);
		handler.SetVirtualView(toolbar);
		handlerRefs.Add(new WeakReference<ToolbarHandler>(handler));

		var platformToolbar = handler.PlatformView;
		retainedNativeToolbars.Add(platformToolbar);

		SetupWithDrawerLayoutMethod.Invoke(handler, new object?[] { drawer });
		var listener = (AView.IOnClickListener?)BackNavigationClickProperty.GetValue(handler);
		platformToolbar.SetNavigationOnClickListener(listener);

		if (cleanupListener)
		{
			platformToolbar.SetNavigationOnClickListener(null);
			DrawerLayoutField.SetValue(handler, null);
			ProcessBackClickField.SetValue(handler, null);
		}

		((IElementHandler)handler).DisconnectHandler();
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

	sealed class PayloadFrameLayout : FrameLayout
	{
		public PayloadFrameLayout(Android.Content.Context context, Payload payload)
			: base(context)
		{
			Payload = payload;
		}

		public Payload Payload { get; }
	}

	sealed class Payload
	{
		public Payload(int id, int byteCount)
		{
			Id = id;
			Bytes = new byte[byteCount];
			Bytes[0] = (byte)(id % 251);
			Bytes[^1] = (byte)((id + 1) % 251);
		}

		public int Id { get; }

		public byte[] Bytes { get; }
	}
}
