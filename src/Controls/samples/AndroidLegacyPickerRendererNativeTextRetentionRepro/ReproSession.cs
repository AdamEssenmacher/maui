#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.Runtime;
using AndroidX.AppCompat.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.Android;
using Microsoft.Maui.Controls.Compatibility.Platform.Android.AppCompat;
using Microsoft.Maui.Graphics;

namespace AndroidLegacyPickerRendererNativeTextRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 1024;
	const int ItemPayloadCharsPerSlot = 4 * 1024;
	const int TitlePayloadCharsPerSlot = 8 * 1024;
	const int ItemPayloadBytesPerSlot = ItemPayloadCharsPerSlot * sizeof(char);
	const int TitlePayloadBytesPerSlot = TitlePayloadCharsPerSlot * sizeof(char);

	static readonly List<object> RetainedNativePeerRoots = new();
	static readonly IntPtr TextViewClass = JNIEnv.FindClass("android/widget/TextView");
	static readonly IntPtr GetTextMethod = JNIEnv.GetMethodID(TextViewClass, "getText", "()Ljava/lang/CharSequence;");
	static readonly IntPtr GetHintMethod = JNIEnv.GetMethodID(TextViewClass, "getHint", "()Ljava/lang/CharSequence;");
	static readonly IntPtr ObjectClass = JNIEnv.FindClass("java/lang/Object");
	static readonly IntPtr ToStringMethod = JNIEnv.GetMethodID(ObjectClass, "toString", "()Ljava/lang/String;");

	public static async Task<ReproReport> RunAsync(IMauiContext context, Element contextRoot)
	{
		RetainedNativePeerRoots.Clear();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			"control: dispose PickerRenderer after clearing native EditText.Text and EditText.Hint",
			context,
			contextRoot,
			clearNativeText: true);

		var current = await RunScenarioAsync(
			"current: dispose PickerRenderer without clearing native text slots",
			context,
			contextRoot,
			clearNativeText: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(forceFullCollection: true);
		GC.KeepAlive(RetainedNativePeerRoots);

		return new ReproReport(
			Cycles,
			ItemPayloadCharsPerSlot,
			TitlePayloadCharsPerSlot,
			ItemPayloadBytesPerSlot,
			TitlePayloadBytesPerSlot,
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
		var retainedNativeEditTexts = new List<NativePeerRoot>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateCycle(
				context,
				contextRoot,
				i,
				retainedNativeEditTexts,
				tracked,
				clearNativeText);

			if (i % 64 == 0)
				await Task.Yield();
		}

		RetainedNativePeerRoots.Add(retainedNativeEditTexts);
		await Task.Delay(250);
		ForceFullGc();
		GC.KeepAlive(retainedNativeEditTexts);

		return ScenarioResult.From(name, tracked);
	}

	static void CreateCycle(
		IMauiContext context,
		Element contextRoot,
		int cycle,
		List<NativePeerRoot> retainedNativeEditTexts,
		List<TrackedCycle> tracked,
		bool clearNativeText)
	{
		var picker = new Picker
		{
			Title = CreatePayload("picker-title", cycle, TitlePayloadCharsPerSlot),
			TitleColor = cycle % 2 == 0 ? Colors.DarkRed : Colors.DarkOrange,
			TextColor = cycle % 2 == 0 ? Colors.DarkGreen : Colors.DarkSlateGray,
			WidthRequest = 280
		};
		picker.Items.Add(CreatePayload("picker-selected-item", cycle, ItemPayloadCharsPerSlot));
		picker.SelectedIndex = 0;

		var renderer = new PickerRenderer(context.Context ?? throw new InvalidOperationException("Android context is not available."));

		contextRoot.AddLogicalChild(picker);
		try
		{
			((IVisualElementRenderer)renderer).SetElement(picker);
		}
		finally
		{
			contextRoot.RemoveLogicalChild(picker);
		}

		var nativeEditText = renderer.Control
			?? throw new InvalidOperationException("PickerRenderer did not create a native AppCompatEditText.");
		var nativePeer = NativePeerRoot.Create(nativeEditText);
		var assignedTextLengthBeforeCleanup = GetTextLength(nativePeer);
		var assignedHintLengthBeforeCleanup = GetHintLength(nativePeer);

		if (clearNativeText)
		{
			nativeEditText.Text = null;
			nativeEditText.Hint = null;
		}

		renderer.Dispose();

		retainedNativeEditTexts.Add(nativePeer);
		tracked.Add(TrackedCycle.Create(
			cycle,
			nativePeer,
			renderer,
			picker,
			assignedTextLengthBeforeCleanup,
			assignedHintLengthBeforeCleanup));
	}

	static string CreatePayload(string prefix, int cycle, int length)
	{
		var textPrefix = $"android-legacy-pickerrenderer-{prefix}-{cycle:D4}-";
		return textPrefix + new string((char)('A' + (cycle % 26)), length - textPrefix.Length);
	}

	static int GetTextLength(NativePeerRoot nativePeer)
		=> GetCharSequenceLength(nativePeer, GetTextMethod);

	static int GetHintLength(NativePeerRoot nativePeer)
		=> GetCharSequenceLength(nativePeer, GetHintMethod);

	static int GetCharSequenceLength(NativePeerRoot nativePeer, IntPtr method)
	{
		var text = JNIEnv.CallObjectMethod(nativePeer.GlobalRef, method);
		if (text == IntPtr.Zero)
			return 0;

		try
		{
			var textString = JNIEnv.CallObjectMethod(text, ToStringMethod);
			if (textString == IntPtr.Zero)
				return 0;

			try
			{
				return JNIEnv.GetString(textString, JniHandleOwnership.DoNotTransfer)?.Length ?? 0;
			}
			finally
			{
				JNIEnv.DeleteLocalRef(textString);
			}
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
		public static NativePeerRoot Create(AppCompatEditText editText)
		{
			if (editText.Handle == IntPtr.Zero)
				throw new InvalidOperationException("Native AppCompatEditText handle was not available before renderer disposal.");

			var globalRef = JNIEnv.NewGlobalRef(editText.Handle);
			if (globalRef == IntPtr.Zero)
				throw new InvalidOperationException("Failed to create a JNI global reference for the native AppCompatEditText.");

			return new NativePeerRoot(globalRef);
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		NativePeerRoot NativeEditText,
		WeakReference<PickerRenderer> ManagedRenderer,
		WeakReference<Picker> Picker,
		int AssignedTextLengthBeforeCleanup,
		int AssignedHintLengthBeforeCleanup)
	{
		public static TrackedCycle Create(
			int cycle,
			NativePeerRoot nativeEditText,
			PickerRenderer renderer,
			Picker picker,
			int assignedTextLengthBeforeCleanup,
			int assignedHintLengthBeforeCleanup)
		{
			return new TrackedCycle(
				cycle,
				nativeEditText,
				new WeakReference<PickerRenderer>(renderer),
				new WeakReference<Picker>(picker),
				assignedTextLengthBeforeCleanup,
				assignedHintLengthBeforeCleanup);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeEditTexts,
		int AliveManagedRenderers,
		int AlivePickers,
		int AssignedTextBeforeCleanup,
		int AssignedHintBeforeCleanup,
		int AssignedTextSlots,
		int AssignedHintSlots,
		int PayloadTextSlots,
		int PayloadHintSlots,
		long RetainedNativeTextBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativeEditTexts = 0;
			var aliveManagedRenderers = 0;
			var alivePickers = 0;
			var assignedTextBeforeCleanup = 0;
			var assignedHintBeforeCleanup = 0;
			var assignedTextSlots = 0;
			var assignedHintSlots = 0;
			var payloadTextSlots = 0;
			var payloadHintSlots = 0;
			long retainedNativeTextBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.AssignedTextLengthBeforeCleanup >= ItemPayloadCharsPerSlot)
					assignedTextBeforeCleanup++;
				if (cycle.AssignedHintLengthBeforeCleanup >= TitlePayloadCharsPerSlot)
					assignedHintBeforeCleanup++;

				if (cycle.NativeEditText.GlobalRef != IntPtr.Zero)
				{
					aliveNativeEditTexts++;
					var textLength = GetTextLength(cycle.NativeEditText);
					var hintLength = GetHintLength(cycle.NativeEditText);

					if (textLength > 0)
						assignedTextSlots++;
					if (hintLength > 0)
						assignedHintSlots++;
					if (textLength >= ItemPayloadCharsPerSlot)
						payloadTextSlots++;
					if (hintLength >= TitlePayloadCharsPerSlot)
						payloadHintSlots++;

					retainedNativeTextBytes += ((long)textLength + hintLength) * sizeof(char);
				}

				if (cycle.ManagedRenderer.TryGetTarget(out _))
					aliveManagedRenderers++;

				if (cycle.Picker.TryGetTarget(out _))
					alivePickers++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeEditTexts,
				aliveManagedRenderers,
				alivePickers,
				assignedTextBeforeCleanup,
				assignedHintBeforeCleanup,
				assignedTextSlots,
				assignedHintSlots,
				payloadTextSlots,
				payloadHintSlots,
				retainedNativeTextBytes);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int ItemPayloadCharsPerSlot,
	int TitlePayloadCharsPerSlot,
	int ItemPayloadBytesPerSlot,
	int TitlePayloadBytesPerSlot,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.AssignedTextBeforeCleanup == Cycles &&
		Current.AssignedTextBeforeCleanup == Cycles &&
		Control.AssignedHintBeforeCleanup == Cycles &&
		Current.AssignedHintBeforeCleanup == Cycles &&
		Control.AliveNativeEditTexts == Cycles &&
		Current.AliveNativeEditTexts == Cycles &&
		Control.AliveManagedRenderers == 0 &&
		Current.AliveManagedRenderers == 0 &&
		Control.AlivePickers == 0 &&
		Current.AlivePickers == 0 &&
		Control.PayloadTextSlots == 0 &&
		Control.PayloadHintSlots == 0 &&
		Current.PayloadTextSlots == Cycles &&
		Current.PayloadHintSlots == Cycles &&
		Current.RetainedNativeTextBytes >= 20L * 1024 * 1024;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidLegacyPickerRendererNativeTextRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload chars per native EditText.Text slot: {ItemPayloadCharsPerSlot:N0}",
			$"Payload chars per native EditText.Hint slot: {TitlePayloadCharsPerSlot:N0}",
			$"Payload bytes per native EditText.Text slot: {ItemPayloadBytesPerSlot:N0}",
			$"Payload bytes per native EditText.Hint slot: {TitlePayloadBytesPerSlot:N0}",
			"Source path exercised: obsolete Android PickerRenderer.UpdatePicker/UpdatePlaceHolderText -> child EditText.Text/EditText.Hint",
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
			$"  payload EditText.Text values assigned before cleanup: {result.AssignedTextBeforeCleanup}/{result.TrackedCycles}",
			$"  payload EditText.Hint values assigned before cleanup: {result.AssignedHintBeforeCleanup}/{result.TrackedCycles}",
			$"  retained native EditTexts: {result.AliveNativeEditTexts}/{result.TrackedCycles}",
			$"  alive managed renderer wrappers after full GC: {result.AliveManagedRenderers}/{result.TrackedCycles}",
			$"  alive Pickers after full GC: {result.AlivePickers}/{result.TrackedCycles}",
			$"  assigned native EditText.Text slots: {result.AssignedTextSlots}/{result.TrackedCycles}",
			$"  assigned native EditText.Hint slots: {result.AssignedHintSlots}/{result.TrackedCycles}",
			$"  payload-sized native EditText.Text slots: {result.PayloadTextSlots}/{result.TrackedCycles}",
			$"  payload-sized native EditText.Hint slots: {result.PayloadHintSlots}/{result.TrackedCycles}",
			$"  retained native text bytes: {result.RetainedNativeTextBytes:N0}");
	}

	static string FormatBytes(long bytes)
	{
		if (bytes >= 1024L * 1024 * 1024)
			return $"{bytes / 1024d / 1024d / 1024d:N1} GiB";
		if (bytes >= 1024L * 1024)
			return $"{bytes / 1024d / 1024d:N1} MiB";
		if (bytes >= 1024)
			return $"{bytes / 1024d:N1} KiB";

		return $"{bytes:N0} B";
	}
}
