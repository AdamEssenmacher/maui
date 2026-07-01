#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Graphics;
using Android.Runtime;
using Android.Widget;
using AndroidX.AppCompat.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.Android;
using Microsoft.Maui.Graphics;
using AColor = Android.Graphics.Color;
using AImageView = Android.Widget.ImageView;
using AView = Android.Views.View;
using AViewGroup = Android.Views.ViewGroup;

namespace AndroidLegacySwipeViewRendererActionButtonImageRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 96;
	const int BitmapSide = 512;
	const int BitmapBytes = BitmapSide * BitmapSide * 4;

	static readonly List<object> RetainedNativePeerRoots = new();
	static readonly IntPtr TextViewClass = JNIEnv.FindClass("android/widget/TextView");
	static readonly IntPtr DrawableClass = JNIEnv.FindClass("android/graphics/drawable/Drawable");
	static readonly IntPtr GetCompoundDrawablesMethod = JNIEnv.GetMethodID(TextViewClass, "getCompoundDrawables", "()[Landroid/graphics/drawable/Drawable;");
	static readonly IntPtr GetIntrinsicWidthMethod = JNIEnv.GetMethodID(DrawableClass, "getIntrinsicWidth", "()I");
	static readonly IntPtr GetIntrinsicHeightMethod = JNIEnv.GetMethodID(DrawableClass, "getIntrinsicHeight", "()I");

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
			"control: clear native action-button compound drawables before SwipeViewRenderer disposal",
			context,
			contextRoot,
			clearNativeDrawable: true);

		var current = await RunScenarioAsync(
			"current: dispose SwipeViewRenderer without clearing native action-button compound drawables",
			context,
			contextRoot,
			clearNativeDrawable: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(forceFullCollection: true);
		GC.KeepAlive(RetainedNativePeerRoots);

		return new ReproReport(Cycles, BitmapSide, BitmapBytes, baselineBytes, finalBytes, control, current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		Element contextRoot,
		bool clearNativeDrawable)
	{
		var retainedNativeButtons = new List<NativePeerRoot>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			await CreateCycleAsync(context, contextRoot, i, retainedNativeButtons, tracked, clearNativeDrawable);

			if (i % 12 == 0)
				await Task.Yield();
		}

		RetainedNativePeerRoots.Add(retainedNativeButtons);
		await Task.Delay(250);
		ForceFullGc();
		GC.KeepAlive(retainedNativeButtons);

		return ScenarioResult.From(name, tracked);
	}

	static async Task CreateCycleAsync(
		IMauiContext context,
		Element contextRoot,
		int cycle,
		List<NativePeerRoot> retainedNativeButtons,
		List<TrackedCycle> tracked,
		bool clearNativeDrawable)
	{
		var source = new TrackingImageSource(cycle, BitmapSide);
		var swipeItem = new SwipeItem
		{
			Text = string.Empty,
			IconImageSource = source,
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
		await WaitForAsync(() => source.BitmapLoads > 0, "legacy SwipeViewRenderer did not request a swipe item icon bitmap.");

		var nativeButton = GetNativeActionButton(renderer);
		var nativePeer = NativePeerRoot.Create(nativeButton);
		var assignedBeforeCleanup = CountDrawableSlots(nativePeer).PayloadSized > 0;

		if (clearNativeDrawable)
			nativeButton.SetCompoundDrawables(null, null, null, null);

		renderer.Dispose();
		swipeItem.IconImageSource = null;

		retainedNativeButtons.Add(nativePeer);
		tracked.Add(TrackedCycle.Create(
			cycle,
			nativePeer,
			renderer,
			swipeView,
			swipeItem,
			source,
			assignedBeforeCleanup));
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

	static async Task WaitForAsync(Func<bool> predicate, string failureMessage)
	{
		for (var i = 0; i < 100; i++)
		{
			if (predicate())
				return;

			await Task.Delay(25);
		}

		throw new InvalidOperationException(failureMessage);
	}

	static DrawableSlotCount CountDrawableSlots(NativePeerRoot nativePeer)
	{
		var drawables = JNIEnv.CallObjectMethod(nativePeer.GlobalRef, GetCompoundDrawablesMethod);
		if (drawables == IntPtr.Zero)
			return new DrawableSlotCount(0, 0);

		try
		{
			var assigned = 0;
			var payloadSized = 0;
			var length = JNIEnv.GetArrayLength(drawables);

			for (var i = 0; i < length; i++)
			{
				var drawable = JNIEnv.GetObjectArrayElement(drawables, i);
				if (drawable == IntPtr.Zero)
					continue;

				try
				{
					assigned++;
					if (GetDrawableArea(drawable) >= BitmapSide * BitmapSide)
						payloadSized++;
				}
				finally
				{
					JNIEnv.DeleteLocalRef(drawable);
				}
			}

			return new DrawableSlotCount(assigned, payloadSized);
		}
		finally
		{
			JNIEnv.DeleteLocalRef(drawables);
		}
	}

	static long GetDrawableArea(IntPtr drawable)
	{
		var width = JNIEnv.CallIntMethod(drawable, GetIntrinsicWidthMethod);
		var height = JNIEnv.CallIntMethod(drawable, GetIntrinsicHeightMethod);
		if (width <= 0 || height <= 0)
			return 0;

		return (long)width * height;
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

	internal sealed record DrawableSlotCount(int Assigned, int PayloadSized);

	internal sealed record TrackedCycle(
		int Cycle,
		NativePeerRoot NativeButton,
		WeakReference<SwipeViewRenderer> ManagedRenderer,
		WeakReference<SwipeView> SwipeView,
		WeakReference<SwipeItem> SwipeItem,
		WeakReference<TrackingImageSource> Source,
		bool AssignedBeforeCleanup)
	{
		public static TrackedCycle Create(
			int cycle,
			NativePeerRoot nativeButton,
			SwipeViewRenderer renderer,
			SwipeView swipeView,
			SwipeItem swipeItem,
			TrackingImageSource source,
			bool assignedBeforeCleanup)
		{
			return new TrackedCycle(
				cycle,
				nativeButton,
				new WeakReference<SwipeViewRenderer>(renderer),
				new WeakReference<SwipeView>(swipeView),
				new WeakReference<SwipeItem>(swipeItem),
				new WeakReference<TrackingImageSource>(source),
				assignedBeforeCleanup);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeButtons,
		int AliveManagedRenderers,
		int AliveSwipeViews,
		int AliveSwipeItems,
		int AliveSources,
		int AssignedBeforeCleanup,
		int AssignedNativeDrawableSlots,
		int PayloadSizedNativeDrawableSlots,
		long RetainedDrawablePayloadBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativeButtons = 0;
			var aliveManagedRenderers = 0;
			var aliveSwipeViews = 0;
			var aliveSwipeItems = 0;
			var aliveSources = 0;
			var assignedBeforeCleanup = 0;
			var assignedNativeDrawableSlots = 0;
			var payloadSizedNativeDrawableSlots = 0;
			long retainedDrawablePayloadBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.AssignedBeforeCleanup)
					assignedBeforeCleanup++;

				if (cycle.NativeButton.GlobalRef != IntPtr.Zero)
				{
					aliveNativeButtons++;
					var slotCount = CountDrawableSlots(cycle.NativeButton);
					assignedNativeDrawableSlots += slotCount.Assigned;
					payloadSizedNativeDrawableSlots += slotCount.PayloadSized;
					retainedDrawablePayloadBytes += (long)slotCount.PayloadSized * BitmapBytes;
				}

				if (cycle.ManagedRenderer.TryGetTarget(out _))
					aliveManagedRenderers++;
				if (cycle.SwipeView.TryGetTarget(out _))
					aliveSwipeViews++;
				if (cycle.SwipeItem.TryGetTarget(out _))
					aliveSwipeItems++;
				if (cycle.Source.TryGetTarget(out _))
					aliveSources++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeButtons,
				aliveManagedRenderers,
				aliveSwipeViews,
				aliveSwipeItems,
				aliveSources,
				assignedBeforeCleanup,
				assignedNativeDrawableSlots,
				payloadSizedNativeDrawableSlots,
				retainedDrawablePayloadBytes);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int BitmapSide,
	int BitmapBytes,
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
		Control.AliveSwipeViews == 0 &&
		Current.AliveSwipeViews == 0 &&
		Control.AliveSwipeItems == 0 &&
		Current.AliveSwipeItems == 0 &&
		Control.AliveSources == 0 &&
		Current.AliveSources == 0 &&
		Control.PayloadSizedNativeDrawableSlots == 0 &&
		Current.PayloadSizedNativeDrawableSlots == Cycles &&
		Current.RetainedDrawablePayloadBytes >= 90L * 1024 * 1024;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidLegacySwipeViewRendererActionButtonImageRetentionRepro",
			$"Cycles: {Cycles}",
			$"Swipe item icon bitmap size: {BitmapSide}x{BitmapSide} ARGB ({FormatBytes(BitmapBytes)})",
			"Source path exercised: obsolete Android SwipeViewRenderer.CreateSwipeItem -> AppCompatButton compound drawables",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained drawable payload: {FormatBytes(Control.RetainedDrawablePayloadBytes)}",
			$"Current retained drawable payload: {FormatBytes(Current.RetainedDrawablePayloadBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  payload drawables assigned before cleanup: {result.AssignedBeforeCleanup}/{result.TrackedCycles}",
			$"  retained native action buttons: {result.AliveNativeButtons}/{result.TrackedCycles}",
			$"  alive managed renderer wrappers after full GC: {result.AliveManagedRenderers}/{result.TrackedCycles}",
			$"  alive SwipeViews after full GC: {result.AliveSwipeViews}/{result.TrackedCycles}",
			$"  alive SwipeItems after full GC: {result.AliveSwipeItems}/{result.TrackedCycles}",
			$"  alive image sources after full GC: {result.AliveSources}/{result.TrackedCycles}",
			$"  assigned native compound drawable slots: {result.AssignedNativeDrawableSlots}",
			$"  payload-sized native compound drawable slots: {result.PayloadSizedNativeDrawableSlots}/{result.TrackedCycles}",
			$"  retained drawable payload bytes: {result.RetainedDrawablePayloadBytes:N0}");
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

internal sealed class TrackingImageSource : ImageSource
{
	public TrackingImageSource(int cycle, int bitmapSide)
	{
		Cycle = cycle;
		BitmapSide = bitmapSide;
	}

	public int Cycle { get; }

	public int BitmapSide { get; }

	public int BitmapLoads { get; set; }

	public override bool IsEmpty => false;
}

internal sealed class TrackingImageSourceHandler : IImageViewHandler, IImageSourceHandler
{
	public Task LoadImageAsync(ImageSource imagesource, AImageView imageView, CancellationToken cancellationToken = default)
	{
		return Task.CompletedTask;
	}

	public Task<Bitmap> LoadImageAsync(ImageSource imagesource, Context context, CancellationToken cancelationToken = default)
	{
		if (imagesource is not TrackingImageSource source)
			return Task.FromResult<Bitmap>(null!);

		source.BitmapLoads++;
		var bitmap = Bitmap.CreateBitmap(source.BitmapSide, source.BitmapSide, Bitmap.Config.Argb8888!)
			?? throw new InvalidOperationException("Failed to create the swipe item icon bitmap.");
		bitmap.EraseColor(AColor.Rgb((source.Cycle * 41) % 255, (source.Cycle * 71) % 255, (source.Cycle * 101) % 255));
		return Task.FromResult(bitmap);
	}
}
