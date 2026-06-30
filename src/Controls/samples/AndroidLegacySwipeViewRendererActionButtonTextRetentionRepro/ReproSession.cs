#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Android.Runtime;
using AndroidX.AppCompat.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.Android;
using Microsoft.Maui.Graphics;
using AView = Android.Views.View;
using AViewGroup = Android.Views.ViewGroup;

namespace AndroidLegacySwipeViewRendererActionButtonTextRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 1024;
	const int TextPayloadCharsPerSlot = 8 * 1024;
	const int AutomationPayloadCharsPerSlot = 8 * 1024;
	const int TextPayloadBytesPerSlot = TextPayloadCharsPerSlot * sizeof(char);
	const int AutomationPayloadBytesPerSlot = AutomationPayloadCharsPerSlot * sizeof(char);

	static readonly List<object> RetainedNativePeerRoots = new();
	static readonly IntPtr TextViewClass = JNIEnv.FindClass("android/widget/TextView");
	static readonly IntPtr ViewClass = JNIEnv.FindClass("android/view/View");
	static readonly IntPtr GetTextMethod = JNIEnv.GetMethodID(TextViewClass, "getText", "()Ljava/lang/CharSequence;");
	static readonly IntPtr GetContentDescriptionMethod = JNIEnv.GetMethodID(ViewClass, "getContentDescription", "()Ljava/lang/CharSequence;");
	static readonly IntPtr CharSequenceClass = JNIEnv.FindClass("java/lang/CharSequence");
	static readonly IntPtr CharSequenceLengthMethod = JNIEnv.GetMethodID(CharSequenceClass, "length", "()I");

	static readonly FieldInfo SwipeDirectionField =
		typeof(SwipeViewRenderer).GetField("_swipeDirection", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(nameof(SwipeViewRenderer), "_swipeDirection");

	static readonly FieldInfo ActionViewField =
		typeof(SwipeViewRenderer).GetField("_actionView", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(nameof(SwipeViewRenderer), "_actionView");

	static readonly MethodInfo UpdateSwipeItemsMethod =
		typeof(SwipeViewRenderer).GetMethod("UpdateSwipeItems", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(nameof(SwipeViewRenderer), "UpdateSwipeItems");

	public static async Task<ReproReport> RunAsync(IMauiContext context, Element contextRoot)
	{
		RetainedNativePeerRoots.Clear();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			"control: dispose SwipeViewRenderer after clearing native button Text and ContentDescription",
			context,
			contextRoot,
			clearNativeStrings: true);

		var current = await RunScenarioAsync(
			"current: dispose SwipeViewRenderer without clearing native action-button strings",
			context,
			contextRoot,
			clearNativeStrings: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(forceFullCollection: true);
		GC.KeepAlive(RetainedNativePeerRoots);

		return new ReproReport(
			Cycles,
			TextPayloadCharsPerSlot,
			AutomationPayloadCharsPerSlot,
			TextPayloadBytesPerSlot,
			AutomationPayloadBytesPerSlot,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		Element contextRoot,
		bool clearNativeStrings)
	{
		var retainedNativeButtons = new List<NativePeerRoot>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateCycle(context, contextRoot, i, retainedNativeButtons, tracked, clearNativeStrings);

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
		bool clearNativeStrings)
	{
		var swipeItem = new SwipeItem
		{
			Text = CreatePayload("text", cycle, TextPayloadCharsPerSlot),
			AutomationId = CreatePayload("automation", cycle, AutomationPayloadCharsPerSlot),
			BackgroundColor = cycle % 2 == 0 ? Colors.DarkBlue : Colors.DarkRed
		};

		var swipeView = new SwipeView
		{
			Content = new BoxView
			{
				Color = Colors.LightGray,
				WidthRequest = 240,
				HeightRequest = 72
			}
		};
		swipeView.RightItems.Add(swipeItem);

		var renderer = new SwipeViewRenderer(context.Context ?? throw new InvalidOperationException("Android context is not available."));

		contextRoot.AddLogicalChild(swipeView);
		try
		{
			((IVisualElementRenderer)renderer).SetElement(swipeView);
		}
		finally
		{
			contextRoot.RemoveLogicalChild(swipeView);
		}

		SwipeDirectionField.SetValue(renderer, SwipeDirection.Left);
		UpdateSwipeItemsMethod.Invoke(renderer, Array.Empty<object>());

		var nativeButton = GetNativeActionButton(renderer);
		var nativePeer = NativePeerRoot.Create(nativeButton);
		var assignedTextLengthBeforeCleanup = GetTextLength(nativePeer);
		var assignedContentDescriptionLengthBeforeCleanup = GetContentDescriptionLength(nativePeer);

		if (clearNativeStrings)
		{
			nativeButton.Text = null;
			nativeButton.ContentDescription = null;
		}

		renderer.Dispose();

		retainedNativeButtons.Add(nativePeer);
		tracked.Add(TrackedCycle.Create(
			cycle,
			nativePeer,
			renderer,
			swipeView,
			swipeItem,
			assignedTextLengthBeforeCleanup,
			assignedContentDescriptionLengthBeforeCleanup));
	}

	static AppCompatButton GetNativeActionButton(SwipeViewRenderer renderer)
	{
		var actionView = ActionViewField.GetValue(renderer) as AViewGroup
			?? throw new InvalidOperationException("SwipeViewRenderer did not create an action view.");

		if (actionView.ChildCount != 1)
			throw new InvalidOperationException($"Expected exactly one action button, found {actionView.ChildCount}.");

		return actionView.GetChildAt(0) as AppCompatButton
			?? throw new InvalidOperationException("SwipeViewRenderer action child was not an AppCompatButton.");
	}

	static string CreatePayload(string kind, int cycle, int length)
	{
		var prefix = $"android-legacy-swipeviewrenderer-actionbutton-{kind}-{cycle:D4}-";
		return prefix + new string((char)('A' + (cycle % 26)), length - prefix.Length);
	}

	static int GetTextLength(NativePeerRoot nativePeer)
		=> GetCharSequenceLength(nativePeer, GetTextMethod);

	static int GetContentDescriptionLength(NativePeerRoot nativePeer)
		=> GetCharSequenceLength(nativePeer, GetContentDescriptionMethod);

	static int GetCharSequenceLength(NativePeerRoot nativePeer, IntPtr method)
	{
		var text = JNIEnv.CallObjectMethod(nativePeer.GlobalRef, method);
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
		public static NativePeerRoot Create(AView view)
		{
			if (view.Handle == IntPtr.Zero)
				throw new InvalidOperationException("Native action button handle was not available before renderer disposal.");

			var globalRef = JNIEnv.NewGlobalRef(view.Handle);
			if (globalRef == IntPtr.Zero)
				throw new InvalidOperationException("Failed to create a JNI global reference for the native action button.");

			return new NativePeerRoot(globalRef);
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		NativePeerRoot NativeButton,
		WeakReference<SwipeViewRenderer> ManagedRenderer,
		WeakReference<SwipeView> SwipeView,
		WeakReference<SwipeItem> SwipeItem,
		int AssignedTextLengthBeforeCleanup,
		int AssignedContentDescriptionLengthBeforeCleanup)
	{
		public static TrackedCycle Create(
			int cycle,
			NativePeerRoot nativeButton,
			SwipeViewRenderer renderer,
			SwipeView swipeView,
			SwipeItem swipeItem,
			int assignedTextLengthBeforeCleanup,
			int assignedContentDescriptionLengthBeforeCleanup)
		{
			return new TrackedCycle(
				cycle,
				nativeButton,
				new WeakReference<SwipeViewRenderer>(renderer),
				new WeakReference<SwipeView>(swipeView),
				new WeakReference<SwipeItem>(swipeItem),
				assignedTextLengthBeforeCleanup,
				assignedContentDescriptionLengthBeforeCleanup);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeButtons,
		int AliveManagedRenderers,
		int AliveSwipeViews,
		int AliveSwipeItems,
		int AssignedTextBeforeCleanup,
		int AssignedContentDescriptionBeforeCleanup,
		int AssignedTextSlots,
		int AssignedContentDescriptionSlots,
		int PayloadTextSlots,
		int PayloadContentDescriptionSlots,
		long RetainedNativeTextBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativeButtons = 0;
			var aliveManagedRenderers = 0;
			var aliveSwipeViews = 0;
			var aliveSwipeItems = 0;
			var assignedTextBeforeCleanup = 0;
			var assignedContentDescriptionBeforeCleanup = 0;
			var assignedTextSlots = 0;
			var assignedContentDescriptionSlots = 0;
			var payloadTextSlots = 0;
			var payloadContentDescriptionSlots = 0;
			long retainedNativeTextBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.AssignedTextLengthBeforeCleanup >= TextPayloadCharsPerSlot)
					assignedTextBeforeCleanup++;
				if (cycle.AssignedContentDescriptionLengthBeforeCleanup >= AutomationPayloadCharsPerSlot)
					assignedContentDescriptionBeforeCleanup++;

				if (cycle.NativeButton.GlobalRef != IntPtr.Zero)
				{
					aliveNativeButtons++;
					var textLength = GetTextLength(cycle.NativeButton);
					var contentDescriptionLength = GetContentDescriptionLength(cycle.NativeButton);

					if (textLength > 0)
						assignedTextSlots++;
					if (contentDescriptionLength > 0)
						assignedContentDescriptionSlots++;
					if (textLength >= TextPayloadCharsPerSlot)
						payloadTextSlots++;
					if (contentDescriptionLength >= AutomationPayloadCharsPerSlot)
						payloadContentDescriptionSlots++;

					retainedNativeTextBytes += ((long)textLength + contentDescriptionLength) * sizeof(char);
				}

				if (cycle.ManagedRenderer.TryGetTarget(out _))
					aliveManagedRenderers++;
				if (cycle.SwipeView.TryGetTarget(out _))
					aliveSwipeViews++;
				if (cycle.SwipeItem.TryGetTarget(out _))
					aliveSwipeItems++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeButtons,
				aliveManagedRenderers,
				aliveSwipeViews,
				aliveSwipeItems,
				assignedTextBeforeCleanup,
				assignedContentDescriptionBeforeCleanup,
				assignedTextSlots,
				assignedContentDescriptionSlots,
				payloadTextSlots,
				payloadContentDescriptionSlots,
				retainedNativeTextBytes);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int TextPayloadCharsPerSlot,
	int AutomationPayloadCharsPerSlot,
	int TextPayloadBytesPerSlot,
	int AutomationPayloadBytesPerSlot,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.AssignedTextBeforeCleanup == Cycles &&
		Current.AssignedTextBeforeCleanup == Cycles &&
		Control.AssignedContentDescriptionBeforeCleanup == Cycles &&
		Current.AssignedContentDescriptionBeforeCleanup == Cycles &&
		Control.AliveNativeButtons == Cycles &&
		Current.AliveNativeButtons == Cycles &&
		Control.AliveManagedRenderers == 0 &&
		Current.AliveManagedRenderers == 0 &&
		Control.AliveSwipeViews == 0 &&
		Current.AliveSwipeViews == 0 &&
		Control.AliveSwipeItems == 0 &&
		Current.AliveSwipeItems == 0 &&
		Control.PayloadTextSlots == 0 &&
		Control.PayloadContentDescriptionSlots == 0 &&
		Current.PayloadTextSlots == Cycles &&
		Current.PayloadContentDescriptionSlots == Cycles &&
		Current.RetainedNativeTextBytes >= 28L * 1024 * 1024;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidLegacySwipeViewRendererActionButtonTextRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload chars per native action button Text slot: {TextPayloadCharsPerSlot:N0}",
			$"Payload chars per native action button ContentDescription slot: {AutomationPayloadCharsPerSlot:N0}",
			$"Payload bytes per native action button Text slot: {TextPayloadBytesPerSlot:N0}",
			$"Payload bytes per native action button ContentDescription slot: {AutomationPayloadBytesPerSlot:N0}",
			"Source path exercised: obsolete Android SwipeViewRenderer.UpdateSwipeItems/CreateSwipeItem -> AppCompatButton.Text/ContentDescription",
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
			$"  payload native Text values assigned before cleanup: {result.AssignedTextBeforeCleanup}/{result.TrackedCycles}",
			$"  payload native ContentDescription values assigned before cleanup: {result.AssignedContentDescriptionBeforeCleanup}/{result.TrackedCycles}",
			$"  retained native action buttons: {result.AliveNativeButtons}/{result.TrackedCycles}",
			$"  alive managed renderer wrappers after full GC: {result.AliveManagedRenderers}/{result.TrackedCycles}",
			$"  alive SwipeViews after full GC: {result.AliveSwipeViews}/{result.TrackedCycles}",
			$"  alive SwipeItems after full GC: {result.AliveSwipeItems}/{result.TrackedCycles}",
			$"  assigned native Text slots: {result.AssignedTextSlots}/{result.TrackedCycles}",
			$"  assigned native ContentDescription slots: {result.AssignedContentDescriptionSlots}/{result.TrackedCycles}",
			$"  payload-sized native Text slots: {result.PayloadTextSlots}/{result.TrackedCycles}",
			$"  payload-sized native ContentDescription slots: {result.PayloadContentDescriptionSlots}/{result.TrackedCycles}",
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
