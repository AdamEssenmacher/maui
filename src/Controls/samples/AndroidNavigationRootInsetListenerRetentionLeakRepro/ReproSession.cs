#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.CoordinatorLayout.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Platform;
using AView = Android.Views.View;

namespace AndroidNavigationRootInsetListenerRetentionLeakRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveRoots,
	int AlivePayloadViews,
	int AlivePayloads,
	int RegisteredEntryDelta,
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
		Control.AlivePayloadViews == 0 &&
		Control.AlivePayloads == 0 &&
		Current.AlivePayloadViews == Attempts &&
		Current.AlivePayloads == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidNavigationRootInsetListenerRetentionLeakRepro",
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
			$"  stale registered inset entries: {stats.RegisteredEntryDelta}",
			$"  old navigation roots alive after full GC: {stats.AliveRoots}/{stats.Attempts}",
			$"  tracked safe-area payload views alive after full GC: {stats.AlivePayloadViews}/{stats.Attempts}",
			$"  safe-area payloads alive after full GC: {stats.AlivePayloads}/{stats.Attempts}",
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

	static readonly MethodInfo ConnectMethod =
		typeof(NavigationRootManager).GetMethod("Connect", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(nameof(NavigationRootManager), "Connect");

	static readonly Type InsetListenerType =
		typeof(NavigationRootManager).Assembly.GetType("Microsoft.Maui.Platform.MauiWindowInsetListener")
		?? throw new MissingMemberException("Microsoft.Maui.Platform.MauiWindowInsetListener");

	static readonly MethodInfo FindListenerForViewMethod =
		InsetListenerType.GetMethod("FindListenerForView", BindingFlags.Static | BindingFlags.NonPublic)
		?? throw new MissingMethodException(InsetListenerType.FullName, "FindListenerForView");

	static readonly MethodInfo RemoveViewWithLocalListenerMethod =
		InsetListenerType.GetMethod("RemoveViewWithLocalListener", BindingFlags.Static | BindingFlags.NonPublic)
		?? throw new MissingMethodException(InsetListenerType.FullName, "RemoveViewWithLocalListener");

	static readonly MethodInfo TrackViewMethod =
		InsetListenerType.GetMethod("TrackView", BindingFlags.Instance | BindingFlags.Public)
		?? throw new MissingMethodException(InsetListenerType.FullName, "TrackView");

	static readonly FieldInfo RegisteredViewsField =
		InsetListenerType.GetField("_registeredViews", BindingFlags.Static | BindingFlags.NonPublic)
		?? throw new MissingFieldException(InsetListenerType.FullName, "_registeredViews");

	public static async Task<ReproReport> RunAsync(IMauiContext mauiContext)
	{
		await Task.Yield();

		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			mauiContext,
			"control: remove old local inset listener before root replacement",
			removeOldListenerBeforeReplacement: true);

		var current = await RunScenarioAsync(
			mauiContext,
			"current: NavigationRootManager.Connect drops old listener registration",
			removeOldListenerBeforeReplacement: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(
		IMauiContext mauiContext,
		string name,
		bool removeOldListenerBeforeReplacement)
	{
		var rootRefs = new List<WeakReference<CoordinatorLayout>>(Attempts);
		var payloadViewRefs = new List<WeakReference<PayloadFrameLayout>>(Attempts);
		var payloadRefs = new List<WeakReference<Payload>>(Attempts);
		var registeredBefore = RegisteredViewCount;

		for (var i = 0; i < Attempts; i++)
		{
			CreateReplacedRoot(
				mauiContext,
				removeOldListenerBeforeReplacement,
				rootRefs,
				payloadViewRefs,
				payloadRefs,
				i);

			if (i % 10 == 0)
				await Task.Yield();
		}

		ForceFullGc();

		var aliveRoots = rootRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloadViews = payloadViewRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloads = payloadRefs.Count(static wr => wr.TryGetTarget(out _));
		var registeredAfter = RegisteredViewCount;

		return new RunStats(
			name,
			Attempts,
			aliveRoots,
			alivePayloadViews,
			alivePayloads,
			registeredAfter - registeredBefore,
			(long)alivePayloads * PayloadBytes);
	}

	static void CreateReplacedRoot(
		IMauiContext mauiContext,
		bool removeOldListenerBeforeReplacement,
		List<WeakReference<CoordinatorLayout>> rootRefs,
		List<WeakReference<PayloadFrameLayout>> payloadViewRefs,
		List<WeakReference<Payload>> payloadRefs,
		int index)
	{
		var manager = new NavigationRootManager(mauiContext);
		Connect(manager, mauiContext);

		var root = manager.RootView as CoordinatorLayout
			?? throw new InvalidOperationException($"Expected {nameof(CoordinatorLayout)} root.");

		rootRefs.Add(new WeakReference<CoordinatorLayout>(root));

		var context = mauiContext.Context ?? Android.App.Application.Context;
		var payload = new Payload(index, PayloadBytes);
		payloadRefs.Add(new WeakReference<Payload>(payload));

		var payloadView = new PayloadFrameLayout(context, payload)
		{
			LayoutParameters = new ViewGroup.LayoutParams(
				ViewGroup.LayoutParams.MatchParent,
				ViewGroup.LayoutParams.MatchParent)
		};
		payloadViewRefs.Add(new WeakReference<PayloadFrameLayout>(payloadView));

		root.AddView(payloadView);
		TrackSafeAreaPayload(payloadView);

		if (removeOldListenerBeforeReplacement)
		{
			RemoveViewWithLocalListener(root);
		}

		Connect(manager, mauiContext);
		manager.Disconnect();
	}

	static void Connect(NavigationRootManager manager, IMauiContext mauiContext)
	{
		ConnectMethod.Invoke(manager, new object?[] { null, mauiContext });
	}

	static void TrackSafeAreaPayload(AView view)
	{
		var listener = FindListenerForViewMethod.Invoke(null, new object?[] { view })
			?? throw new InvalidOperationException("No local inset listener was found for the payload view.");

		// This is the same strong-listener payload produced by SafeAreaExtensions after
		// nonzero insets are applied, without depending on emulator notch/status-bar shape.
		TrackViewMethod.Invoke(listener, new object?[] { view });
	}

	static void RemoveViewWithLocalListener(AView view)
	{
		RemoveViewWithLocalListenerMethod.Invoke(null, new object?[] { view });
	}

	static int RegisteredViewCount
	{
		get
		{
			var registered = RegisteredViewsField.GetValue(null) as ICollection;
			return registered?.Count ?? 0;
		}
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
		public PayloadFrameLayout(Context context, Payload payload)
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
