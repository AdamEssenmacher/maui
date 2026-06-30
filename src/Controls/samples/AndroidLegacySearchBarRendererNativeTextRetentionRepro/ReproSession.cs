#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.Runtime;
using Android.Views;
using AndroidX.AppCompat.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.Android;
using Microsoft.Maui.Controls.Platform;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Platform;

namespace AndroidLegacySearchBarRendererNativeTextRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 1024;
	const int TextPayloadCharsPerSlot = 4 * 1024;
	const int HintPayloadCharsPerSlot = 8 * 1024;
	const int TextPayloadBytesPerSlot = TextPayloadCharsPerSlot * sizeof(char);
	const int HintPayloadBytesPerSlot = HintPayloadCharsPerSlot * sizeof(char);

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
			"control: dispose SearchBarRenderer after clearing native EditText.Text and EditText.Hint",
			context,
			contextRoot,
			clearNativeText: true);

		var current = await RunScenarioAsync(
			"current: dispose SearchBarRenderer without clearing native text slots",
			context,
			contextRoot,
			clearNativeText: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(forceFullCollection: true);
		GC.KeepAlive(RetainedNativePeerRoots);

		return new ReproReport(
			Cycles,
			TextPayloadCharsPerSlot,
			HintPayloadCharsPerSlot,
			TextPayloadBytesPerSlot,
			HintPayloadBytesPerSlot,
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
		var searchBar = new SearchBar
		{
			Text = CreatePayload("search-text", cycle, TextPayloadCharsPerSlot),
			Placeholder = CreatePayload("search-placeholder", cycle, HintPayloadCharsPerSlot),
			MaxLength = TextPayloadCharsPerSlot,
			TextColor = cycle % 2 == 0 ? Colors.DarkGreen : Colors.DarkSlateGray,
			PlaceholderColor = cycle % 2 == 0 ? Colors.DarkRed : Colors.DarkOrange,
			WidthRequest = 280
		};

		var renderer = new ThemedSearchBarRenderer(context.Context ?? throw new InvalidOperationException("Android context is not available."));

		contextRoot.AddLogicalChild(searchBar);
		try
		{
			((IVisualElementRenderer)renderer).SetElement(searchBar);
		}
		finally
		{
			contextRoot.RemoveLogicalChild(searchBar);
		}

		var nativeSearchView = renderer.Control
			?? throw new InvalidOperationException("SearchBarRenderer did not create a native SearchView.");
		var nativeEditText = ((Android.Views.ViewGroup)nativeSearchView).GetChildrenOfType<AppCompatAutoCompleteTextView>().FirstOrDefault()
			?? throw new InvalidOperationException("SearchBarRenderer did not create an internal AppCompatAutoCompleteTextView.");
		var nativePeer = NativePeerRoot.Create(nativeEditText);
		var assignedTextLengthBeforeCleanup = GetTextLength(nativePeer);
		var assignedHintLengthBeforeCleanup = GetHintLength(nativePeer);

		if (clearNativeText)
		{
			nativeSearchView.SetQuery(string.Empty, false);
			nativeSearchView.QueryHint = null;
			nativeEditText.Text = null;
			nativeEditText.Hint = null;
		}

		renderer.Dispose();

		retainedNativeEditTexts.Add(nativePeer);
		tracked.Add(TrackedCycle.Create(
			cycle,
			nativePeer,
			renderer,
			searchBar,
			assignedTextLengthBeforeCleanup,
			assignedHintLengthBeforeCleanup));
	}

	static string CreatePayload(string prefix, int cycle, int length)
	{
		var textPrefix = $"android-legacy-searchbarrenderer-{prefix}-{cycle:D4}-";
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
		public static NativePeerRoot Create(AppCompatAutoCompleteTextView editText)
		{
			if (editText.Handle == IntPtr.Zero)
				throw new InvalidOperationException("Native AppCompatAutoCompleteTextView handle was not available before renderer disposal.");

			var globalRef = JNIEnv.NewGlobalRef(editText.Handle);
			if (globalRef == IntPtr.Zero)
				throw new InvalidOperationException("Failed to create a JNI global reference for the native AppCompatAutoCompleteTextView.");

			return new NativePeerRoot(globalRef);
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		NativePeerRoot NativeEditText,
		WeakReference<SearchBarRenderer> ManagedRenderer,
		WeakReference<SearchBar> SearchBar,
		int AssignedTextLengthBeforeCleanup,
		int AssignedHintLengthBeforeCleanup)
	{
		public static TrackedCycle Create(
			int cycle,
			NativePeerRoot nativeEditText,
			SearchBarRenderer renderer,
			SearchBar searchBar,
			int assignedTextLengthBeforeCleanup,
			int assignedHintLengthBeforeCleanup)
		{
			return new TrackedCycle(
				cycle,
				nativeEditText,
				new WeakReference<SearchBarRenderer>(renderer),
				new WeakReference<SearchBar>(searchBar),
				assignedTextLengthBeforeCleanup,
				assignedHintLengthBeforeCleanup);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeEditTexts,
		int AliveManagedRenderers,
		int AliveSearchBars,
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
			var aliveSearchBars = 0;
			var assignedTextBeforeCleanup = 0;
			var assignedHintBeforeCleanup = 0;
			var assignedTextSlots = 0;
			var assignedHintSlots = 0;
			var payloadTextSlots = 0;
			var payloadHintSlots = 0;
			long retainedNativeTextBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.AssignedTextLengthBeforeCleanup >= TextPayloadCharsPerSlot)
					assignedTextBeforeCleanup++;
				if (cycle.AssignedHintLengthBeforeCleanup >= HintPayloadCharsPerSlot)
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
					if (textLength >= TextPayloadCharsPerSlot)
						payloadTextSlots++;
					if (hintLength >= HintPayloadCharsPerSlot)
						payloadHintSlots++;

					retainedNativeTextBytes += ((long)textLength + hintLength) * sizeof(char);
				}

				if (cycle.ManagedRenderer.TryGetTarget(out _))
					aliveManagedRenderers++;

				if (cycle.SearchBar.TryGetTarget(out _))
					aliveSearchBars++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeEditTexts,
				aliveManagedRenderers,
				aliveSearchBars,
				assignedTextBeforeCleanup,
				assignedHintBeforeCleanup,
				assignedTextSlots,
				assignedHintSlots,
				payloadTextSlots,
				payloadHintSlots,
				retainedNativeTextBytes);
		}
	}

	sealed class ThemedSearchBarRenderer : SearchBarRenderer
	{
		public ThemedSearchBarRenderer(Android.Content.Context context)
			: base(context)
		{
		}

		protected override SearchView CreateNativeControl()
		{
			var themedContext = new ContextThemeWrapper(Context, Resource.Style.Theme_AppCompat_Light_NoActionBar);
			return new SearchView(themedContext);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int TextPayloadCharsPerSlot,
	int HintPayloadCharsPerSlot,
	int TextPayloadBytesPerSlot,
	int HintPayloadBytesPerSlot,
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
		Control.AliveSearchBars == 0 &&
		Current.AliveSearchBars == 0 &&
		Control.PayloadTextSlots == 0 &&
		Control.PayloadHintSlots == 0 &&
		Current.PayloadTextSlots == Cycles &&
		Current.PayloadHintSlots == Cycles &&
		Current.RetainedNativeTextBytes >= 20L * 1024 * 1024;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidLegacySearchBarRendererNativeTextRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload chars per native EditText.Text slot: {TextPayloadCharsPerSlot:N0}",
			$"Payload chars per native EditText.Hint slot: {HintPayloadCharsPerSlot:N0}",
			$"Payload bytes per native EditText.Text slot: {TextPayloadBytesPerSlot:N0}",
			$"Payload bytes per native EditText.Hint slot: {HintPayloadBytesPerSlot:N0}",
			"Source path exercised: obsolete Android SearchBarRenderer.UpdateText/UpdatePlaceholder -> internal EditText.Text/EditText.Hint",
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
			$"  alive SearchBars after full GC: {result.AliveSearchBars}/{result.TrackedCycles}",
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
