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

namespace AndroidLegacyLabelRendererNativeTextRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 1024;
	const int PayloadCharsPerSlot = 16 * 1024;
	const int PayloadBytesPerSlot = PayloadCharsPerSlot * sizeof(char);

	static readonly List<object> RetainedNativePeerRoots = new();
	static readonly IntPtr TextViewClass = JNIEnv.FindClass("android/widget/TextView");
	static readonly IntPtr GetTextMethod = JNIEnv.GetMethodID(TextViewClass, "getText", "()Ljava/lang/CharSequence;");
	static readonly IntPtr CharSequenceClass = JNIEnv.FindClass("java/lang/CharSequence");
	static readonly IntPtr CharSequenceLengthMethod = JNIEnv.GetMethodID(CharSequenceClass, "length", "()I");

	static readonly FieldInfo MotionEventHelperField =
		typeof(LabelRenderer).GetField("_motionEventHelper", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(nameof(LabelRenderer), "_motionEventHelper");

	static readonly FieldInfo MotionEventHelperElementField =
		typeof(LabelRenderer).Assembly
			.GetType("Microsoft.Maui.Controls.Compatibility.Platform.Android.MotionEventHelper")
			?.GetField("_element", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException("MotionEventHelper", "_element");

	public static async Task<ReproReport> RunAsync(IMauiContext context, Element contextRoot)
	{
		RetainedNativePeerRoots.Clear();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			"control: dispose LabelRenderer after clearing known MotionEventHelper root and native TextView.Text",
			context,
			contextRoot,
			clearNativeText: true);

		var current = await RunScenarioAsync(
			"current: dispose LabelRenderer after clearing known MotionEventHelper root only",
			context,
			contextRoot,
			clearNativeText: false);

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
		Element contextRoot,
		bool clearNativeText)
	{
		var retainedNativeTextViews = new List<NativePeerRoot>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateCycle(
				context,
				contextRoot,
				i,
				retainedNativeTextViews,
				tracked,
				clearNativeText);

			if (i % 64 == 0)
				await Task.Yield();
		}

		RetainedNativePeerRoots.Add(retainedNativeTextViews);
		await Task.Delay(250);
		ForceFullGc();
		GC.KeepAlive(retainedNativeTextViews);

		return ScenarioResult.From(name, tracked);
	}

	static void CreateCycle(
		IMauiContext context,
		Element contextRoot,
		int cycle,
		List<NativePeerRoot> retainedNativeTextViews,
		List<TrackedCycle> tracked,
		bool clearNativeText)
	{
		var label = new Label
		{
			Text = CreatePayload(cycle),
			TextColor = cycle % 2 == 0 ? Colors.DarkBlue : Colors.DarkRed,
			WidthRequest = 240,
			HeightRequest = 24
		};

		var renderer = new LabelRenderer(context.Context ?? throw new InvalidOperationException("Android context is not available."));

		contextRoot.AddLogicalChild(label);
		try
		{
			((IVisualElementRenderer)renderer).SetElement(label);
		}
		finally
		{
			contextRoot.RemoveLogicalChild(label);
		}

		var nativeTextView = renderer.Control
			?? throw new InvalidOperationException("LabelRenderer did not create a native TextView.");
		var assignedLengthBeforeCleanup = GetTextLength(nativeTextView);
		var nativePeer = NativePeerRoot.Create(nativeTextView);

		ClearKnownMotionEventHelperRoot(renderer);

		if (clearNativeText)
			nativeTextView.Text = null;

		renderer.Dispose();

		retainedNativeTextViews.Add(nativePeer);
		tracked.Add(TrackedCycle.Create(cycle, nativePeer, renderer, label, assignedLengthBeforeCleanup));
	}

	static void ClearKnownMotionEventHelperRoot(LabelRenderer renderer)
	{
		var helper = MotionEventHelperField.GetValue(renderer)
			?? throw new InvalidOperationException("LabelRenderer did not create a MotionEventHelper.");

		MotionEventHelperElementField.SetValue(helper, null);
	}

	static string CreatePayload(int cycle)
	{
		var prefix = $"android-legacy-labelrenderer-native-text-{cycle:D4}-";
		return prefix + new string((char)('A' + (cycle % 26)), PayloadCharsPerSlot - prefix.Length);
	}

	static int GetTextLength(Android.Widget.TextView textView)
	{
		var text = textView.Text;
		return text?.Length ?? 0;
	}

	static int GetTextLength(NativePeerRoot nativePeer)
	{
		var text = JNIEnv.CallObjectMethod(nativePeer.GlobalRef, GetTextMethod);
		if (text == IntPtr.Zero)
			return 0;

		try
		{
			return JNIEnv.CallIntMethod(text, CharSequenceLengthMethod);
		}
		finally
		{
			JNIEnv.DeleteLocalRef(text);
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
		public static NativePeerRoot Create(Android.Widget.TextView textView)
		{
			if (textView.Handle == IntPtr.Zero)
				throw new InvalidOperationException("Native TextView handle was not available before renderer disposal.");

			var globalRef = JNIEnv.NewGlobalRef(textView.Handle);
			if (globalRef == IntPtr.Zero)
				throw new InvalidOperationException("Failed to create a JNI global reference for the native TextView.");

			return new NativePeerRoot(globalRef);
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		NativePeerRoot NativeTextView,
		WeakReference<LabelRenderer> ManagedRenderer,
		WeakReference<Label> Label,
		int AssignedLengthBeforeCleanup)
	{
		public static TrackedCycle Create(
			int cycle,
			NativePeerRoot nativeTextView,
			LabelRenderer renderer,
			Label label,
			int assignedLengthBeforeCleanup)
		{
			return new TrackedCycle(
				cycle,
				nativeTextView,
				new WeakReference<LabelRenderer>(renderer),
				new WeakReference<Label>(label),
				assignedLengthBeforeCleanup);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeTextViews,
		int AliveManagedRenderers,
		int AliveLabels,
		int AssignedBeforeCleanup,
		int AssignedTextSlots,
		int PayloadTextSlots,
		long RetainedNativeTextBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativeTextViews = 0;
			var aliveManagedRenderers = 0;
			var aliveLabels = 0;
			var assignedBeforeCleanup = 0;
			var assignedTextSlots = 0;
			var payloadTextSlots = 0;
			long retainedNativeTextBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.AssignedLengthBeforeCleanup >= PayloadCharsPerSlot)
					assignedBeforeCleanup++;

				if (cycle.NativeTextView.GlobalRef != IntPtr.Zero)
				{
					aliveNativeTextViews++;
					var textLength = GetTextLength(cycle.NativeTextView);

					if (textLength > 0)
						assignedTextSlots++;
					if (textLength >= PayloadCharsPerSlot)
						payloadTextSlots++;

					retainedNativeTextBytes += (long)textLength * sizeof(char);
				}

				if (cycle.ManagedRenderer.TryGetTarget(out _))
					aliveManagedRenderers++;

				if (cycle.Label.TryGetTarget(out _))
					aliveLabels++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeTextViews,
				aliveManagedRenderers,
				aliveLabels,
				assignedBeforeCleanup,
				assignedTextSlots,
				payloadTextSlots,
				retainedNativeTextBytes);
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
		Control.AliveNativeTextViews == Cycles &&
		Current.AliveNativeTextViews == Cycles &&
		Control.AliveManagedRenderers == 0 &&
		Current.AliveManagedRenderers == 0 &&
		Control.AliveLabels == 0 &&
		Current.AliveLabels == 0 &&
		Control.PayloadTextSlots == 0 &&
		Current.PayloadTextSlots == Cycles &&
		Current.RetainedNativeTextBytes >= 24L * 1024 * 1024;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidLegacyLabelRendererNativeTextRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload chars per native TextView.Text slot: {PayloadCharsPerSlot:N0}",
			$"Payload bytes per native TextView.Text slot: {PayloadBytesPerSlot:N0}",
			"Source path exercised: obsolete Android LabelRenderer.UpdateText -> child TextView.Text",
			"Known LabelRenderer MotionEventHelper._element root is cleared in both runs so retained Labels do not explain the result",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained native text payload: {FormatBytes(Control.RetainedNativeTextBytes)}",
			$"Current retained native text payload: {FormatBytes(Current.RetainedNativeTextBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  payload TextView.Text values assigned before cleanup: {result.AssignedBeforeCleanup}/{result.TrackedCycles}",
			$"  retained native TextViews: {result.AliveNativeTextViews}/{result.TrackedCycles}",
			$"  alive managed renderer wrappers after full GC: {result.AliveManagedRenderers}/{result.TrackedCycles}",
			$"  alive Labels after full GC: {result.AliveLabels}/{result.TrackedCycles}",
			$"  assigned native TextView.Text slots: {result.AssignedTextSlots}/{result.TrackedCycles}",
			$"  payload-sized native TextView.Text slots: {result.PayloadTextSlots}/{result.TrackedCycles}",
			$"  retained native text bytes: {result.RetainedNativeTextBytes:N0}");
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
