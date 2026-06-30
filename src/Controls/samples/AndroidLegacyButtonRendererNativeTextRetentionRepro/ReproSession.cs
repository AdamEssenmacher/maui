#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Android.Runtime;
using AndroidX.AppCompat.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.Android;
using Microsoft.Maui.Controls.Compatibility.Platform.Android.AppCompat;
using Microsoft.Maui.Graphics;

namespace AndroidLegacyButtonRendererNativeTextRetentionRepro;

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

	public static async Task<ReproReport> RunAsync(IMauiContext context, Element contextRoot)
	{
		RetainedNativePeerRoots.Clear();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			"control: dispose ButtonRenderer after clearing native AppCompatButton.Text",
			context,
			contextRoot,
			clearNativeText: true);

		var current = await RunScenarioAsync(
			"current: dispose ButtonRenderer without clearing native AppCompatButton.Text",
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
		var retainedNativeButtons = new List<NativePeerRoot>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateCycle(
				context,
				contextRoot,
				i,
				retainedNativeButtons,
				tracked,
				clearNativeText);

			if (i % 64 == 0)
				await Task.Yield();
		}

		RetainedNativePeerRoots.Add(retainedNativeButtons);
		await Task.Delay(250);
		ForceFullGc();
		GC.KeepAlive(retainedNativeButtons);

		return ScenarioResult.From(name, tracked);
	}

	static void CreateCycle(
		IMauiContext context,
		Element contextRoot,
		int cycle,
		List<NativePeerRoot> retainedNativeButtons,
		List<TrackedCycle> tracked,
		bool clearNativeText)
	{
		var button = new Button
		{
			Text = CreatePayload(cycle),
			TextColor = cycle % 2 == 0 ? Colors.DarkBlue : Colors.DarkRed,
			WidthRequest = 240,
			HeightRequest = 44
		};

		var renderer = new ButtonRenderer(context.Context ?? throw new InvalidOperationException("Android context is not available."));

		contextRoot.AddLogicalChild(button);
		try
		{
			((IVisualElementRenderer)renderer).SetElement(button);
		}
		finally
		{
			contextRoot.RemoveLogicalChild(button);
		}

		var nativeButton = renderer.Control
			?? throw new InvalidOperationException("ButtonRenderer did not create a native AppCompatButton.");
		var assignedLengthBeforeCleanup = GetTextLength(nativeButton);
		var nativePeer = NativePeerRoot.Create(nativeButton);

		if (clearNativeText)
			nativeButton.Text = null;

		renderer.Dispose();

		retainedNativeButtons.Add(nativePeer);
		tracked.Add(TrackedCycle.Create(cycle, nativePeer, renderer, button, assignedLengthBeforeCleanup));
	}

	static string CreatePayload(int cycle)
	{
		var prefix = $"android-legacy-buttonrenderer-native-text-{cycle:D4}-";
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
		public static NativePeerRoot Create(AppCompatButton button)
		{
			if (button.Handle == IntPtr.Zero)
				throw new InvalidOperationException("Native AppCompatButton handle was not available before renderer disposal.");

			var globalRef = JNIEnv.NewGlobalRef(button.Handle);
			if (globalRef == IntPtr.Zero)
				throw new InvalidOperationException("Failed to create a JNI global reference for the native AppCompatButton.");

			return new NativePeerRoot(globalRef);
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		NativePeerRoot NativeButton,
		WeakReference<ButtonRenderer> ManagedRenderer,
		WeakReference<Button> Button,
		int AssignedLengthBeforeCleanup)
	{
		public static TrackedCycle Create(
			int cycle,
			NativePeerRoot nativeButton,
			ButtonRenderer renderer,
			Button button,
			int assignedLengthBeforeCleanup)
		{
			return new TrackedCycle(
				cycle,
				nativeButton,
				new WeakReference<ButtonRenderer>(renderer),
				new WeakReference<Button>(button),
				assignedLengthBeforeCleanup);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeButtons,
		int AliveManagedRenderers,
		int AliveButtons,
		int AssignedBeforeCleanup,
		int AssignedTextSlots,
		int PayloadTextSlots,
		long RetainedNativeTextBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativeButtons = 0;
			var aliveManagedRenderers = 0;
			var aliveButtons = 0;
			var assignedBeforeCleanup = 0;
			var assignedTextSlots = 0;
			var payloadTextSlots = 0;
			long retainedNativeTextBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.AssignedLengthBeforeCleanup >= PayloadCharsPerSlot)
					assignedBeforeCleanup++;

				if (cycle.NativeButton.GlobalRef != IntPtr.Zero)
				{
					aliveNativeButtons++;
					var textLength = GetTextLength(cycle.NativeButton);

					if (textLength > 0)
						assignedTextSlots++;
					if (textLength >= PayloadCharsPerSlot)
						payloadTextSlots++;

					retainedNativeTextBytes += (long)textLength * sizeof(char);
				}

				if (cycle.ManagedRenderer.TryGetTarget(out _))
					aliveManagedRenderers++;

				if (cycle.Button.TryGetTarget(out _))
					aliveButtons++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeButtons,
				aliveManagedRenderers,
				aliveButtons,
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
		Control.AliveNativeButtons == Cycles &&
		Current.AliveNativeButtons == Cycles &&
		Control.AliveManagedRenderers == 0 &&
		Current.AliveManagedRenderers == 0 &&
		Control.AliveButtons == 0 &&
		Current.AliveButtons == 0 &&
		Control.PayloadTextSlots == 0 &&
		Current.PayloadTextSlots == Cycles &&
		Current.RetainedNativeTextBytes >= 24L * 1024 * 1024;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidLegacyButtonRendererNativeTextRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload chars per native AppCompatButton.Text slot: {PayloadCharsPerSlot:N0}",
			$"Payload bytes per native AppCompatButton.Text slot: {PayloadBytesPerSlot:N0}",
			"Source path exercised: obsolete Android ButtonLayoutManager.UpdateTextAndImage -> AppCompatButton.Text",
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
			$"  payload AppCompatButton.Text values assigned before cleanup: {result.AssignedBeforeCleanup}/{result.TrackedCycles}",
			$"  retained native AppCompatButtons: {result.AliveNativeButtons}/{result.TrackedCycles}",
			$"  alive managed renderer wrappers after full GC: {result.AliveManagedRenderers}/{result.TrackedCycles}",
			$"  alive Buttons after full GC: {result.AliveButtons}/{result.TrackedCycles}",
			$"  assigned native AppCompatButton.Text slots: {result.AssignedTextSlots}/{result.TrackedCycles}",
			$"  payload-sized native AppCompatButton.Text slots: {result.PayloadTextSlots}/{result.TrackedCycles}",
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
