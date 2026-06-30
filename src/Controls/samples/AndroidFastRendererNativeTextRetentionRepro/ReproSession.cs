#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Android.Runtime;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.Android;
using Microsoft.Maui.Graphics;
using FastButtonRenderer = Microsoft.Maui.Controls.Compatibility.Platform.Android.FastRenderers.ButtonRenderer;
using FastLabelRenderer = Microsoft.Maui.Controls.Compatibility.Platform.Android.FastRenderers.LabelRenderer;

namespace AndroidFastRendererNativeTextRetentionRepro;

internal static class ReproSession
{
	internal const int CyclesPerRenderer = 512;
	const int TotalCycles = CyclesPerRenderer * 2;
	const int PayloadCharsPerSlot = 16 * 1024;
	const int PayloadBytesPerSlot = PayloadCharsPerSlot * sizeof(char);
	const string LabelKind = "FastLabelRenderer";
	const string ButtonKind = "FastButtonRenderer";

	static readonly List<object> RetainedNativePeerRoots = new();
	static readonly IntPtr TextViewClass = JNIEnv.FindClass("android/widget/TextView");
	static readonly IntPtr GetTextMethod = JNIEnv.GetMethodID(TextViewClass, "getText", "()Ljava/lang/CharSequence;");
	static readonly IntPtr CharSequenceClass = JNIEnv.FindClass("java/lang/CharSequence");
	static readonly IntPtr CharSequenceLengthMethod = JNIEnv.GetMethodID(CharSequenceClass, "length", "()I");

	static readonly FieldInfo FastLabelElementField =
		typeof(FastLabelRenderer).GetField("_element", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(nameof(FastLabelRenderer), "_element");

	static readonly FieldInfo FastLabelMotionEventHelperField =
		typeof(FastLabelRenderer).GetField("_motionEventHelper", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(nameof(FastLabelRenderer), "_motionEventHelper");

	static readonly FieldInfo FastButtonElementField =
		typeof(FastButtonRenderer).GetField("_button", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(nameof(FastButtonRenderer), "_button");

	static readonly FieldInfo MotionEventHelperElementField =
		typeof(FastLabelRenderer).Assembly
			.GetType("Microsoft.Maui.Controls.Compatibility.Platform.Android.MotionEventHelper")
			?.GetField("_element", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException("MotionEventHelper", "_element");

	public static async Task<ReproReport> RunAsync(IMauiContext context, Element contextRoot)
	{
		RetainedNativePeerRoots.Clear();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			"control: dispose FastRenderers after clearing native Text and known C114 element roots",
			context,
			contextRoot,
			clearNativeText: true);

		var current = await RunScenarioAsync(
			"current: dispose FastRenderers after clearing known C114 element roots only",
			context,
			contextRoot,
			clearNativeText: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(forceFullCollection: true);
		GC.KeepAlive(RetainedNativePeerRoots);

		return new ReproReport(
			CyclesPerRenderer,
			TotalCycles,
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
		var retainedNativeTextViews = new List<NativePeerRoot>(TotalCycles);
		var tracked = new List<TrackedCycle>(TotalCycles);

		for (var i = 0; i < CyclesPerRenderer; i++)
		{
			CreateLabelCycle(context, contextRoot, i, retainedNativeTextViews, tracked, clearNativeText);
			CreateButtonCycle(context, contextRoot, i, retainedNativeTextViews, tracked, clearNativeText);

			if (i % 32 == 0)
				await Task.Yield();
		}

		RetainedNativePeerRoots.Add(retainedNativeTextViews);
		await Task.Delay(250);
		ForceFullGc();
		GC.KeepAlive(retainedNativeTextViews);

		return ScenarioResult.From(name, tracked);
	}

	static void CreateLabelCycle(
		IMauiContext context,
		Element contextRoot,
		int cycle,
		List<NativePeerRoot> retainedNativeTextViews,
		List<TrackedCycle> tracked,
		bool clearNativeText)
	{
		var label = new Label
		{
			Text = CreatePayload(LabelKind, cycle),
			TextColor = cycle % 2 == 0 ? Colors.DarkBlue : Colors.DarkRed,
			WidthRequest = 240,
			HeightRequest = 24
		};

		var renderer = new FastLabelRenderer(context.Context ?? throw new InvalidOperationException("Android context is not available."));

		contextRoot.AddLogicalChild(label);
		try
		{
			((IVisualElementRenderer)renderer).SetElement(label);
		}
		finally
		{
			contextRoot.RemoveLogicalChild(label);
		}

		var assignedLengthBeforeCleanup = GetTextLength(renderer);
		var nativePeer = NativePeerRoot.Create(renderer, LabelKind);

		if (clearNativeText)
			renderer.Text = null;

		renderer.Dispose();
		ClearKnownFastLabelRoots(renderer);

		retainedNativeTextViews.Add(nativePeer);
		tracked.Add(TrackedCycle.Create(cycle, LabelKind, nativePeer, renderer, label, assignedLengthBeforeCleanup));
	}

	static void CreateButtonCycle(
		IMauiContext context,
		Element contextRoot,
		int cycle,
		List<NativePeerRoot> retainedNativeTextViews,
		List<TrackedCycle> tracked,
		bool clearNativeText)
	{
		var button = new Button
		{
			Text = CreatePayload(ButtonKind, cycle),
			TextColor = cycle % 2 == 0 ? Colors.DarkGreen : Colors.DarkMagenta,
			WidthRequest = 240,
			HeightRequest = 44
		};

		var renderer = new FastButtonRenderer(context.Context ?? throw new InvalidOperationException("Android context is not available."));

		contextRoot.AddLogicalChild(button);
		try
		{
			((IVisualElementRenderer)renderer).SetElement(button);
		}
		finally
		{
			contextRoot.RemoveLogicalChild(button);
		}

		var assignedLengthBeforeCleanup = GetTextLength(renderer);
		var nativePeer = NativePeerRoot.Create(renderer, ButtonKind);

		if (clearNativeText)
			renderer.Text = null;

		renderer.Tag = null;
		renderer.Dispose();
		ClearKnownFastButtonRoots(renderer);

		retainedNativeTextViews.Add(nativePeer);
		tracked.Add(TrackedCycle.Create(cycle, ButtonKind, nativePeer, renderer, button, assignedLengthBeforeCleanup));
	}

	static void ClearKnownFastLabelRoots(FastLabelRenderer renderer)
	{
		FastLabelElementField.SetValue(renderer, null);
		var helper = FastLabelMotionEventHelperField.GetValue(renderer)
			?? throw new InvalidOperationException("Fast LabelRenderer did not create a MotionEventHelper.");
		MotionEventHelperElementField.SetValue(helper, null);
	}

	static void ClearKnownFastButtonRoots(FastButtonRenderer renderer)
	{
		FastButtonElementField.SetValue(renderer, null);
	}

	static string CreatePayload(string kind, int cycle)
	{
		var prefix = $"android-fast-renderer-native-text-{kind}-{cycle:D4}-";
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

	internal sealed record NativePeerRoot(IntPtr GlobalRef, string Kind)
	{
		public static NativePeerRoot Create(Android.Widget.TextView textView, string kind)
		{
			if (textView.Handle == IntPtr.Zero)
				throw new InvalidOperationException($"Native {kind} handle was not available before renderer disposal.");

			var globalRef = JNIEnv.NewGlobalRef(textView.Handle);
			if (globalRef == IntPtr.Zero)
				throw new InvalidOperationException($"Failed to create a JNI global reference for the native {kind}.");

			return new NativePeerRoot(globalRef, kind);
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		string Kind,
		NativePeerRoot NativeTextView,
		WeakReference<object> ManagedRenderer,
		WeakReference<View> VirtualView,
		int AssignedLengthBeforeCleanup)
	{
		public static TrackedCycle Create(
			int cycle,
			string kind,
			NativePeerRoot nativeTextView,
			object renderer,
			View virtualView,
			int assignedLengthBeforeCleanup)
		{
			return new TrackedCycle(
				cycle,
				kind,
				nativeTextView,
				new WeakReference<object>(renderer),
				new WeakReference<View>(virtualView),
				assignedLengthBeforeCleanup);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeTextViews,
		int AliveManagedRenderers,
		int AliveVirtualViews,
		int AssignedBeforeCleanup,
		int AssignedTextSlots,
		int PayloadTextSlots,
		int LabelPayloadTextSlots,
		int ButtonPayloadTextSlots,
		long RetainedNativeTextBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativeTextViews = 0;
			var aliveManagedRenderers = 0;
			var aliveVirtualViews = 0;
			var assignedBeforeCleanup = 0;
			var assignedTextSlots = 0;
			var payloadTextSlots = 0;
			var labelPayloadTextSlots = 0;
			var buttonPayloadTextSlots = 0;
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
					{
						payloadTextSlots++;
						if (cycle.Kind == LabelKind)
							labelPayloadTextSlots++;
						else if (cycle.Kind == ButtonKind)
							buttonPayloadTextSlots++;
					}

					retainedNativeTextBytes += (long)textLength * sizeof(char);
				}

				if (cycle.ManagedRenderer.TryGetTarget(out _))
					aliveManagedRenderers++;

				if (cycle.VirtualView.TryGetTarget(out _))
					aliveVirtualViews++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeTextViews,
				aliveManagedRenderers,
				aliveVirtualViews,
				assignedBeforeCleanup,
				assignedTextSlots,
				payloadTextSlots,
				labelPayloadTextSlots,
				buttonPayloadTextSlots,
				retainedNativeTextBytes);
		}
	}
}

internal sealed record ReproReport(
	int CyclesPerRenderer,
	int TotalCycles,
	int PayloadCharsPerSlot,
	int PayloadBytesPerSlot,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.AssignedBeforeCleanup == TotalCycles &&
		Current.AssignedBeforeCleanup == TotalCycles &&
		Control.AliveNativeTextViews == TotalCycles &&
		Current.AliveNativeTextViews == TotalCycles &&
		Control.AliveVirtualViews == 0 &&
		Current.AliveVirtualViews == 0 &&
		Control.PayloadTextSlots == 0 &&
		Current.LabelPayloadTextSlots == CyclesPerRenderer &&
		Current.ButtonPayloadTextSlots == CyclesPerRenderer &&
		Current.RetainedNativeTextBytes >= 24L * 1024 * 1024;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidFastRendererNativeTextRetentionRepro",
			$"Cycles per renderer: {CyclesPerRenderer}",
			$"Total native text peers per run: {TotalCycles}",
			$"Payload chars per native Text slot: {PayloadCharsPerSlot:N0}",
			$"Payload bytes per native Text slot: {PayloadBytesPerSlot:N0}",
			"Source paths exercised: obsolete Android FastRenderers LabelRenderer.UpdateText -> TextView.Text; FastRenderers ButtonRenderer/ButtonLayoutManager.UpdateTextAndImage -> AppCompatButton.Text",
			"Known C114 FastRenderer element roots are cleared in both runs so retained Labels/Buttons do not explain the result",
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
			$"  payload native Text values assigned before cleanup: {result.AssignedBeforeCleanup}/{result.TrackedCycles}",
			$"  retained native text peers: {result.AliveNativeTextViews}/{result.TrackedCycles}",
			$"  alive managed renderer wrappers after full GC: {result.AliveManagedRenderers}/{result.TrackedCycles}",
			$"  alive Labels/Buttons after full GC: {result.AliveVirtualViews}/{result.TrackedCycles}",
			$"  assigned native Text slots: {result.AssignedTextSlots}/{result.TrackedCycles}",
			$"  payload-sized native Text slots: {result.PayloadTextSlots}/{result.TrackedCycles}",
			$"  payload-sized fast LabelRenderer Text slots: {result.LabelPayloadTextSlots}/{ReproSession.CyclesPerRenderer}",
			$"  payload-sized fast ButtonRenderer Text slots: {result.ButtonPayloadTextSlots}/{ReproSession.CyclesPerRenderer}",
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
