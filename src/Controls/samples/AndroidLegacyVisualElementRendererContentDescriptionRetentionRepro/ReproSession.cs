#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Android.Runtime;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.Android;
using Microsoft.Maui.Graphics;

namespace AndroidLegacyVisualElementRendererContentDescriptionRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 1024;
	const int PayloadCharsPerSlot = 16 * 1024;
	const int PayloadBytesPerSlot = PayloadCharsPerSlot * sizeof(char);

	static readonly List<object> RetainedNativePeerRoots = new();
	static readonly IntPtr ViewClass = JNIEnv.FindClass("android/view/View");
	static readonly IntPtr GetContentDescriptionMethod = JNIEnv.GetMethodID(ViewClass, "getContentDescription", "()Ljava/lang/CharSequence;");
	static readonly IntPtr CharSequenceClass = JNIEnv.FindClass("java/lang/CharSequence");
	static readonly IntPtr CharSequenceLengthMethod = JNIEnv.GetMethodID(CharSequenceClass, "length", "()I");

	static readonly FieldInfo MotionEventHelperField =
		typeof(BoxRenderer).GetField("_motionEventHelper", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(nameof(BoxRenderer), "_motionEventHelper");

	static readonly FieldInfo MotionEventHelperElementField =
		typeof(BoxRenderer).Assembly
			.GetType("Microsoft.Maui.Controls.Compatibility.Platform.Android.MotionEventHelper")
			?.GetField("_element", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException("MotionEventHelper", "_element");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		RetainedNativePeerRoots.Clear();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			"control: dispose BoxRenderer, clear known MotionEventHelper root, and clear native ContentDescription",
			context,
			clearNativeContentDescription: true);

		var current = await RunScenarioAsync(
			"current: dispose BoxRenderer and clear known MotionEventHelper root only",
			context,
			clearNativeContentDescription: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(forceFullCollection: true);
		GC.KeepAlive(RetainedNativePeerRoots);

		return new ReproReport(
			Cycles,
			PayloadCharsPerSlot,
			PayloadBytesPerSlot,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearNativeContentDescription)
	{
		var retainedNativePeers = new List<NativePeerRoot>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateCycle(
				context,
				i,
				retainedNativePeers,
				tracked,
				clearNativeContentDescription);

			if (i % 64 == 0)
				await Task.Yield();
		}

		RetainedNativePeerRoots.Add(retainedNativePeers);
		await Task.Delay(250);
		ForceFullGc();
		GC.KeepAlive(retainedNativePeers);

		return ScenarioResult.From(name, tracked);
	}

	static void CreateCycle(
		IMauiContext context,
		int cycle,
		List<NativePeerRoot> retainedNativePeers,
		List<TrackedCycle> tracked,
		bool clearNativeContentDescription)
	{
		var boxView = new BoxView
		{
			Color = cycle % 2 == 0 ? Colors.CornflowerBlue : Colors.OrangeRed,
			WidthRequest = 48,
			HeightRequest = 48,
			AutomationId = CreatePayload(cycle)
		};

		var renderer = new BoxRenderer(context.Context ?? throw new InvalidOperationException("Android context is not available."));

		((IVisualElementRenderer)renderer).SetElement(boxView);
		var assignedLengthBeforeCleanup = GetContentDescriptionLength(renderer);
		var nativePeer = NativePeerRoot.Create(renderer);

		ClearKnownMotionEventHelperRoot(renderer);

		if (clearNativeContentDescription)
			renderer.ContentDescription = null;

		renderer.Dispose();

		retainedNativePeers.Add(nativePeer);
		tracked.Add(TrackedCycle.Create(cycle, nativePeer, renderer, boxView, assignedLengthBeforeCleanup));
	}

	static void ClearKnownMotionEventHelperRoot(BoxRenderer renderer)
	{
		var helper = MotionEventHelperField.GetValue(renderer)
			?? throw new InvalidOperationException("BoxRenderer did not create a MotionEventHelper.");

		MotionEventHelperElementField.SetValue(helper, null);
	}

	static string CreatePayload(int cycle)
	{
		var prefix = $"android-legacy-visualelementrenderer-contentdescription-{cycle:D4}-";
		return prefix + new string((char)('A' + (cycle % 26)), PayloadCharsPerSlot - prefix.Length);
	}

	static int GetContentDescriptionLength(BoxRenderer renderer)
	{
		var contentDescription = renderer.ContentDescription;
		return contentDescription?.Length ?? 0;
	}

	static int GetContentDescriptionLength(NativePeerRoot nativePeer)
	{
		var contentDescription = JNIEnv.CallObjectMethod(nativePeer.GlobalRef, GetContentDescriptionMethod);
		if (contentDescription == IntPtr.Zero)
			return 0;

		try
		{
			return JNIEnv.CallIntMethod(contentDescription, CharSequenceLengthMethod);
		}
		finally
		{
			JNIEnv.DeleteLocalRef(contentDescription);
		}
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Java.Lang.JavaSystem.Gc();
			Thread.Sleep(100);
		}
	}

	internal sealed record NativePeerRoot(IntPtr GlobalRef)
	{
		public static NativePeerRoot Create(BoxRenderer renderer)
		{
			if (renderer.Handle == IntPtr.Zero)
				throw new InvalidOperationException("BoxRenderer native handle was not available before disposal.");

			var globalRef = JNIEnv.NewGlobalRef(renderer.Handle);
			if (globalRef == IntPtr.Zero)
				throw new InvalidOperationException("Failed to create a JNI global reference for the native BoxRenderer view.");

			return new NativePeerRoot(globalRef);
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		NativePeerRoot NativePeer,
		WeakReference<BoxRenderer> ManagedRenderer,
		WeakReference<BoxView> BoxView,
		int AssignedLengthBeforeCleanup)
	{
		public static TrackedCycle Create(
			int cycle,
			NativePeerRoot nativePeer,
			BoxRenderer renderer,
			BoxView boxView,
			int assignedLengthBeforeCleanup)
		{
			return new TrackedCycle(
				cycle,
				nativePeer,
				new WeakReference<BoxRenderer>(renderer),
				new WeakReference<BoxView>(boxView),
				assignedLengthBeforeCleanup);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeRenderers,
		int AliveManagedRenderers,
		int AliveBoxViews,
		int AssignedBeforeCleanup,
		int AssignedContentDescriptionSlots,
		int PayloadContentDescriptionSlots,
		long RetainedNativeStringBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativeRenderers = 0;
			var aliveManagedRenderers = 0;
			var aliveBoxViews = 0;
			var assignedBeforeCleanup = 0;
			var assignedContentDescriptionSlots = 0;
			var payloadContentDescriptionSlots = 0;
			long retainedNativeStringBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.AssignedLengthBeforeCleanup >= PayloadCharsPerSlot)
					assignedBeforeCleanup++;

				if (cycle.NativePeer.GlobalRef != IntPtr.Zero)
				{
					aliveNativeRenderers++;
					var contentDescriptionLength = GetContentDescriptionLength(cycle.NativePeer);

					if (contentDescriptionLength > 0)
						assignedContentDescriptionSlots++;
					if (contentDescriptionLength >= PayloadCharsPerSlot)
						payloadContentDescriptionSlots++;

					retainedNativeStringBytes += (long)contentDescriptionLength * sizeof(char);
				}

				if (cycle.ManagedRenderer.TryGetTarget(out _))
					aliveManagedRenderers++;

				if (cycle.BoxView.TryGetTarget(out _))
					aliveBoxViews++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeRenderers,
				aliveManagedRenderers,
				aliveBoxViews,
				assignedBeforeCleanup,
				assignedContentDescriptionSlots,
				payloadContentDescriptionSlots,
				retainedNativeStringBytes);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadCharsPerSlot,
	int PayloadBytesPerSlot,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.AssignedBeforeCleanup == Cycles &&
		Current.AssignedBeforeCleanup == Cycles &&
		Control.AliveNativeRenderers == Cycles &&
		Current.AliveNativeRenderers == Cycles &&
		Control.AliveBoxViews == 0 &&
		Current.AliveBoxViews == 0 &&
		Control.PayloadContentDescriptionSlots == 0 &&
		Current.PayloadContentDescriptionSlots == Cycles &&
		Current.RetainedNativeStringBytes >= 24L * 1024 * 1024;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidLegacyVisualElementRendererContentDescriptionRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload chars per native ContentDescription slot: {PayloadCharsPerSlot:N0}",
			$"Payload bytes per native ContentDescription slot: {PayloadBytesPerSlot:N0}",
			"Source path exercised: obsolete Android VisualElementRenderer<T>.SetElement -> AutomationPropertiesProvider.SetAutomationId/SetContentDescription -> View.ContentDescription",
			"Known BoxRenderer MotionEventHelper._element root is cleared in both runs so retained BoxViews do not explain the result",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained native ContentDescription payload: {FormatBytes(Control.RetainedNativeStringBytes)}",
			$"Current retained native ContentDescription payload: {FormatBytes(Current.RetainedNativeStringBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  payload ContentDescriptions assigned before cleanup: {result.AssignedBeforeCleanup}/{result.TrackedCycles}",
			$"  retained native renderer peers: {result.AliveNativeRenderers}/{result.TrackedCycles}",
			$"  alive managed renderer wrappers after full GC: {result.AliveManagedRenderers}/{result.TrackedCycles}",
			$"  alive BoxViews after full GC: {result.AliveBoxViews}/{result.TrackedCycles}",
			$"  assigned native ContentDescription slots: {result.AssignedContentDescriptionSlots}/{result.TrackedCycles}",
			$"  payload-sized native ContentDescription slots: {result.PayloadContentDescriptionSlots}/{result.TrackedCycles}",
			$"  retained native string bytes: {result.RetainedNativeStringBytes:N0}");
	}

	static string FormatBytes(long bytes)
	{
		if (bytes >= 1024 * 1024)
			return $"{bytes / 1024d / 1024d:N1} MiB";
		if (bytes >= 1024)
			return $"{bytes / 1024d:N1} KiB";
		return $"{bytes:N0} B";
	}
}
