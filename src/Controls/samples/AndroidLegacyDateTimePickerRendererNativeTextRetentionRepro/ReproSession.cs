#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Android.Runtime;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.Android;
using Microsoft.Maui.Graphics;
using AEditText = Android.Widget.EditText;
using ATextView = Android.Widget.TextView;
using LegacyDatePickerRenderer = Microsoft.Maui.Controls.Compatibility.Platform.Android.DatePickerRenderer;
using LegacyTimePickerRenderer = Microsoft.Maui.Controls.Compatibility.Platform.Android.TimePickerRenderer;

namespace AndroidLegacyDateTimePickerRendererNativeTextRetentionRepro;

internal static class ReproSession
{
	internal const int CyclesPerRenderer = 1024;
	const int TotalCycles = CyclesPerRenderer * 2;
	const int PayloadCharsPerSlot = 4 * 1024;
	const int PayloadBytesPerSlot = PayloadCharsPerSlot * sizeof(char);
	const string DateKind = "DatePickerRenderer";
	const string TimeKind = "TimePickerRenderer";

	static readonly List<object> RetainedNativePeerRoots = new();
	static readonly IntPtr TextViewClass = JNIEnv.FindClass("android/widget/TextView");
	static readonly IntPtr GetTextMethod = JNIEnv.GetMethodID(TextViewClass, "getText", "()Ljava/lang/CharSequence;");
	static readonly IntPtr CharSequenceClass = JNIEnv.FindClass("java/lang/CharSequence");
	static readonly IntPtr CharSequenceLengthMethod = JNIEnv.GetMethodID(CharSequenceClass, "length", "()I");

	public static async Task<ReproReport> RunAsync(IMauiContext context, Element contextRoot)
	{
		RetainedNativePeerRoots.Clear();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			"control: dispose DatePickerRenderer/TimePickerRenderer after clearing native EditText.Text",
			context,
			contextRoot,
			clearNativeText: true);

		var current = await RunScenarioAsync(
			"current: dispose DatePickerRenderer/TimePickerRenderer without clearing native Text",
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
		var retainedNativeEditTexts = new List<NativePeerRoot>(TotalCycles);
		var tracked = new List<TrackedCycle>(TotalCycles);

		for (var i = 0; i < CyclesPerRenderer; i++)
		{
			CreateDateCycle(context, contextRoot, i, retainedNativeEditTexts, tracked, clearNativeText);
			CreateTimeCycle(context, contextRoot, i, retainedNativeEditTexts, tracked, clearNativeText);

			if (i % 32 == 0)
				await Task.Yield();
		}

		RetainedNativePeerRoots.Add(retainedNativeEditTexts);
		await Task.Delay(250);
		ForceFullGc();
		GC.KeepAlive(retainedNativeEditTexts);

		return ScenarioResult.From(name, tracked);
	}

	static void CreateDateCycle(
		IMauiContext context,
		Element contextRoot,
		int cycle,
		List<NativePeerRoot> retainedNativeEditTexts,
		List<TrackedCycle> tracked,
		bool clearNativeText)
	{
		var datePicker = new Microsoft.Maui.Controls.DatePicker
		{
			Date = new DateTime(2034, (cycle % 12) + 1, (cycle % 27) + 1),
			Format = CreateLiteralFormat(DateKind, cycle),
			TextColor = cycle % 2 == 0 ? Colors.DarkBlue : Colors.DarkRed,
			WidthRequest = 280
		};

		var renderer = new LegacyDatePickerRenderer(context.Context ?? throw new InvalidOperationException("Android context is not available."));

		contextRoot.AddLogicalChild(datePicker);
		try
		{
			((IVisualElementRenderer)renderer).SetElement(datePicker);
		}
		finally
		{
			contextRoot.RemoveLogicalChild(datePicker);
		}

		var nativeEditText = renderer.Control
			?? throw new InvalidOperationException("DatePickerRenderer did not create a native EditText.");
		var assignedLengthBeforeCleanup = GetTextLength(nativeEditText);
		var nativePeer = NativePeerRoot.Create(nativeEditText, DateKind);

		if (clearNativeText)
			nativeEditText.Text = null;

		renderer.Dispose();

		retainedNativeEditTexts.Add(nativePeer);
		tracked.Add(TrackedCycle.Create(cycle, DateKind, nativePeer, renderer, datePicker, assignedLengthBeforeCleanup));
	}

	static void CreateTimeCycle(
		IMauiContext context,
		Element contextRoot,
		int cycle,
		List<NativePeerRoot> retainedNativeEditTexts,
		List<TrackedCycle> tracked,
		bool clearNativeText)
	{
		var timePicker = new Microsoft.Maui.Controls.TimePicker
		{
			Time = new TimeSpan(cycle % 24, cycle % 60, 0),
			Format = CreateLiteralFormat(TimeKind, cycle),
			TextColor = cycle % 2 == 0 ? Colors.DarkGreen : Colors.DarkMagenta,
			WidthRequest = 280
		};

		var renderer = new LegacyTimePickerRenderer(context.Context ?? throw new InvalidOperationException("Android context is not available."));

		contextRoot.AddLogicalChild(timePicker);
		try
		{
			((IVisualElementRenderer)renderer).SetElement(timePicker);
		}
		finally
		{
			contextRoot.RemoveLogicalChild(timePicker);
		}

		var nativeEditText = renderer.Control
			?? throw new InvalidOperationException("TimePickerRenderer did not create a native EditText.");
		var assignedLengthBeforeCleanup = GetTextLength(nativeEditText);
		var nativePeer = NativePeerRoot.Create(nativeEditText, TimeKind);

		if (clearNativeText)
			nativeEditText.Text = null;

		renderer.Dispose();

		retainedNativeEditTexts.Add(nativePeer);
		tracked.Add(TrackedCycle.Create(cycle, TimeKind, nativePeer, renderer, timePicker, assignedLengthBeforeCleanup));
	}

	static string CreateLiteralFormat(string kind, int cycle)
	{
		var prefix = $"android-legacy-datetimepicker-native-text-{kind}-{cycle:D4}-";
		var payload = prefix + new string((char)('A' + (cycle % 26)), PayloadCharsPerSlot - prefix.Length);
		return $"'{payload}'";
	}

	static int GetTextLength(ATextView textView)
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
		public static NativePeerRoot Create(AEditText editText, string kind)
		{
			if (editText.Handle == IntPtr.Zero)
				throw new InvalidOperationException($"Native {kind} EditText handle was not available before renderer disposal.");

			var globalRef = JNIEnv.NewGlobalRef(editText.Handle);
			if (globalRef == IntPtr.Zero)
				throw new InvalidOperationException($"Failed to create a JNI global reference for the native {kind} EditText.");

			return new NativePeerRoot(globalRef, kind);
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		string Kind,
		NativePeerRoot NativeEditText,
		WeakReference<object> ManagedRenderer,
		WeakReference<View> VirtualView,
		int AssignedLengthBeforeCleanup)
	{
		public static TrackedCycle Create(
			int cycle,
			string kind,
			NativePeerRoot nativeEditText,
			object renderer,
			View virtualView,
			int assignedLengthBeforeCleanup)
		{
			return new TrackedCycle(
				cycle,
				kind,
				nativeEditText,
				new WeakReference<object>(renderer),
				new WeakReference<View>(virtualView),
				assignedLengthBeforeCleanup);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeEditTexts,
		int AliveManagedRenderers,
		int AliveVirtualViews,
		int AssignedBeforeCleanup,
		int AssignedTextSlots,
		int PayloadTextSlots,
		int DatePayloadTextSlots,
		int TimePayloadTextSlots,
		long RetainedNativeTextBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativeEditTexts = 0;
			var aliveManagedRenderers = 0;
			var aliveVirtualViews = 0;
			var assignedBeforeCleanup = 0;
			var assignedTextSlots = 0;
			var payloadTextSlots = 0;
			var datePayloadTextSlots = 0;
			var timePayloadTextSlots = 0;
			long retainedNativeTextBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.AssignedLengthBeforeCleanup >= PayloadCharsPerSlot)
					assignedBeforeCleanup++;

				if (cycle.NativeEditText.GlobalRef != IntPtr.Zero)
				{
					aliveNativeEditTexts++;
					var textLength = GetTextLength(cycle.NativeEditText);

					if (textLength > 0)
						assignedTextSlots++;
					if (textLength >= PayloadCharsPerSlot)
					{
						payloadTextSlots++;
						if (cycle.Kind == DateKind)
							datePayloadTextSlots++;
						else if (cycle.Kind == TimeKind)
							timePayloadTextSlots++;
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
				aliveNativeEditTexts,
				aliveManagedRenderers,
				aliveVirtualViews,
				assignedBeforeCleanup,
				assignedTextSlots,
				payloadTextSlots,
				datePayloadTextSlots,
				timePayloadTextSlots,
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
		Control.AliveNativeEditTexts == TotalCycles &&
		Current.AliveNativeEditTexts == TotalCycles &&
		Control.AliveManagedRenderers == 0 &&
		Current.AliveManagedRenderers == 0 &&
		Control.AliveVirtualViews == 0 &&
		Current.AliveVirtualViews == 0 &&
		Control.PayloadTextSlots == 0 &&
		Current.DatePayloadTextSlots == CyclesPerRenderer &&
		Current.TimePayloadTextSlots == CyclesPerRenderer &&
		Current.RetainedNativeTextBytes >= 12L * 1024 * 1024;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidLegacyDateTimePickerRendererNativeTextRetentionRepro",
			$"Cycles per renderer: {CyclesPerRenderer}",
			$"Total native EditText peers per run: {TotalCycles}",
			$"Payload chars per native EditText.Text slot: {PayloadCharsPerSlot:N0}",
			$"Payload bytes per native EditText.Text slot: {PayloadBytesPerSlot:N0}",
			"Source paths exercised: obsolete Android DatePickerRenderer.SetDate -> EditText.Text; TimePickerRenderer.SetTime -> EditText.Text",
			"Payloads use realistic generated literal display format strings so real renderer formatting assigns the native Text slots",
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
			$"  retained native EditText peers: {result.AliveNativeEditTexts}/{result.TrackedCycles}",
			$"  alive managed renderer wrappers after full GC: {result.AliveManagedRenderers}/{result.TrackedCycles}",
			$"  alive DatePickers/TimePickers after full GC: {result.AliveVirtualViews}/{result.TrackedCycles}",
			$"  assigned native Text slots: {result.AssignedTextSlots}/{result.TrackedCycles}",
			$"  payload-sized native Text slots: {result.PayloadTextSlots}/{result.TrackedCycles}",
			$"  payload-sized DatePickerRenderer Text slots: {result.DatePayloadTextSlots}/{ReproSession.CyclesPerRenderer}",
			$"  payload-sized TimePickerRenderer Text slots: {result.TimePayloadTextSlots}/{ReproSession.CyclesPerRenderer}",
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
