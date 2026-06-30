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

namespace AndroidLegacyRadioButtonRendererNativeTextRetentionRepro;

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

	static readonly PropertyInfo RendererElementProperty =
		typeof(RadioButtonRenderer).GetProperty("Element", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(nameof(RadioButtonRenderer), "Element");

	public static async Task<ReproReport> RunAsync(IMauiContext context, Element contextRoot)
	{
		RetainedNativePeerRoots.Clear();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			"control: dispose RadioButtonRenderer after clearing native AppCompatRadioButton.Text and known Element root",
			context,
			contextRoot,
			clearNativeText: true);

		var current = await RunScenarioAsync(
			"current: dispose RadioButtonRenderer after clearing known Element root only",
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
		var retainedNativeRadioButtons = new List<NativePeerRoot>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateCycle(
				context,
				contextRoot,
				i,
				retainedNativeRadioButtons,
				tracked,
				clearNativeText);

			if (i % 64 == 0)
				await Task.Yield();
		}

		RetainedNativePeerRoots.Add(retainedNativeRadioButtons);
		await Task.Delay(250);
		ForceFullGc();
		GC.KeepAlive(retainedNativeRadioButtons);

		return ScenarioResult.From(name, tracked);
	}

	static void CreateCycle(
		IMauiContext context,
		Element contextRoot,
		int cycle,
		List<NativePeerRoot> retainedNativeRadioButtons,
		List<TrackedCycle> tracked,
		bool clearNativeText)
	{
		var radioButton = new RadioButton
		{
			Content = CreatePayload(cycle),
			TextColor = cycle % 2 == 0 ? Colors.DarkBlue : Colors.DarkRed,
			IsChecked = cycle % 2 == 0,
			WidthRequest = 240,
			HeightRequest = 44
		};

		var renderer = new RadioButtonRenderer(context.Context ?? throw new InvalidOperationException("Android context is not available."));

		contextRoot.AddLogicalChild(radioButton);
		try
		{
			((IVisualElementRenderer)renderer).SetElement(radioButton);
		}
		finally
		{
			contextRoot.RemoveLogicalChild(radioButton);
		}

		var assignedLengthBeforeCleanup = GetTextLength(renderer);
		var nativePeer = NativePeerRoot.Create(renderer);

		if (clearNativeText)
			renderer.Text = null;

		renderer.Tag = null;
		renderer.Dispose();
		ClearKnownElementRoot(renderer);

		retainedNativeRadioButtons.Add(nativePeer);
		tracked.Add(TrackedCycle.Create(cycle, nativePeer, renderer, radioButton, assignedLengthBeforeCleanup));
	}

	static void ClearKnownElementRoot(RadioButtonRenderer renderer)
	{
		RendererElementProperty.SetValue(renderer, null);
	}

	static string CreatePayload(int cycle)
	{
		var prefix = $"android-legacy-radiobuttonrenderer-native-text-{cycle:D4}-";
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
		public static NativePeerRoot Create(RadioButtonRenderer renderer)
		{
			if (renderer.Handle == IntPtr.Zero)
				throw new InvalidOperationException("Native AppCompatRadioButton handle was not available before renderer disposal.");

			var globalRef = JNIEnv.NewGlobalRef(renderer.Handle);
			if (globalRef == IntPtr.Zero)
				throw new InvalidOperationException("Failed to create a JNI global reference for the native AppCompatRadioButton.");

			return new NativePeerRoot(globalRef);
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		NativePeerRoot NativeRadioButton,
		WeakReference<RadioButtonRenderer> ManagedRenderer,
		WeakReference<RadioButton> RadioButton,
		int AssignedLengthBeforeCleanup)
	{
		public static TrackedCycle Create(
			int cycle,
			NativePeerRoot nativeRadioButton,
			RadioButtonRenderer renderer,
			RadioButton radioButton,
			int assignedLengthBeforeCleanup)
		{
			return new TrackedCycle(
				cycle,
				nativeRadioButton,
				new WeakReference<RadioButtonRenderer>(renderer),
				new WeakReference<RadioButton>(radioButton),
				assignedLengthBeforeCleanup);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeRadioButtons,
		int AliveManagedRenderers,
		int AliveRadioButtons,
		int AssignedBeforeCleanup,
		int AssignedTextSlots,
		int PayloadTextSlots,
		long RetainedNativeTextBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativeRadioButtons = 0;
			var aliveManagedRenderers = 0;
			var aliveRadioButtons = 0;
			var assignedBeforeCleanup = 0;
			var assignedTextSlots = 0;
			var payloadTextSlots = 0;
			long retainedNativeTextBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.AssignedLengthBeforeCleanup >= PayloadCharsPerSlot)
					assignedBeforeCleanup++;

				if (cycle.NativeRadioButton.GlobalRef != IntPtr.Zero)
				{
					aliveNativeRadioButtons++;
					var textLength = GetTextLength(cycle.NativeRadioButton);

					if (textLength > 0)
						assignedTextSlots++;
					if (textLength >= PayloadCharsPerSlot)
						payloadTextSlots++;

					retainedNativeTextBytes += (long)textLength * sizeof(char);
				}

				if (cycle.ManagedRenderer.TryGetTarget(out _))
					aliveManagedRenderers++;

				if (cycle.RadioButton.TryGetTarget(out _))
					aliveRadioButtons++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeRadioButtons,
				aliveManagedRenderers,
				aliveRadioButtons,
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
		Control.AliveNativeRadioButtons == Cycles &&
		Current.AliveNativeRadioButtons == Cycles &&
		Control.AliveRadioButtons == 0 &&
		Current.AliveRadioButtons == 0 &&
		Control.PayloadTextSlots == 0 &&
		Current.PayloadTextSlots == Cycles &&
		Current.RetainedNativeTextBytes >= 24L * 1024 * 1024;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidLegacyRadioButtonRendererNativeTextRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload chars per native AppCompatRadioButton.Text slot: {PayloadCharsPerSlot:N0}",
			$"Payload bytes per native AppCompatRadioButton.Text slot: {PayloadBytesPerSlot:N0}",
			"Source path exercised: obsolete Android RadioButtonRenderer.UpdateContent -> AppCompatRadioButton.Text",
			"Known C115 RadioButtonRenderer.Element root and self Tag are cleared in both runs so retained RadioButtons do not explain the result",
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
			$"  payload AppCompatRadioButton.Text values assigned before cleanup: {result.AssignedBeforeCleanup}/{result.TrackedCycles}",
			$"  retained native AppCompatRadioButtons: {result.AliveNativeRadioButtons}/{result.TrackedCycles}",
			$"  alive managed renderer wrappers after full GC: {result.AliveManagedRenderers}/{result.TrackedCycles}",
			$"  alive RadioButtons after full GC: {result.AliveRadioButtons}/{result.TrackedCycles}",
			$"  assigned native AppCompatRadioButton.Text slots: {result.AssignedTextSlots}/{result.TrackedCycles}",
			$"  payload-sized native AppCompatRadioButton.Text slots: {result.PayloadTextSlots}/{result.TrackedCycles}",
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
