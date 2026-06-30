#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Runtime;
using AndroidX.AppCompat.Widget;
using AndroidX.Core.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.Android;
using Microsoft.Maui.Controls.Compatibility.Platform.Android.AppCompat;
using Microsoft.Maui.Graphics;
using AColor = Android.Graphics.Color;
using AImageView = Android.Widget.ImageView;

namespace AndroidLegacyButtonRendererImageDrawableRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 96;
	const int BitmapSide = 512;
	const int BitmapBytes = BitmapSide * BitmapSide * 4;

	static readonly List<object> RetainedNativePeerRoots = new();
	static readonly IntPtr TextViewClass = JNIEnv.FindClass("android/widget/TextView");
	static readonly IntPtr DrawableClass = JNIEnv.FindClass("android/graphics/drawable/Drawable");
	static readonly IntPtr GetCompoundDrawablesRelativeMethod = JNIEnv.GetMethodID(TextViewClass, "getCompoundDrawablesRelative", "()[Landroid/graphics/drawable/Drawable;");
	static readonly IntPtr GetCompoundDrawablesMethod = JNIEnv.GetMethodID(TextViewClass, "getCompoundDrawables", "()[Landroid/graphics/drawable/Drawable;");
	static readonly IntPtr GetIntrinsicWidthMethod = JNIEnv.GetMethodID(DrawableClass, "getIntrinsicWidth", "()I");
	static readonly IntPtr GetIntrinsicHeightMethod = JNIEnv.GetMethodID(DrawableClass, "getIntrinsicHeight", "()I");

	public static async Task<ReproReport> RunAsync(IMauiContext context, Element contextRoot)
	{
		RetainedNativePeerRoots.Clear();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			"control: clear AppCompatButton compound drawables before ButtonRenderer disposal",
			context,
			contextRoot,
			clearNativeDrawable: true);

		var current = await RunScenarioAsync(
			"current: dispose ButtonRenderer without clearing AppCompatButton compound drawables",
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
			await CreateCycleAsync(
				context,
				contextRoot,
				i,
				retainedNativeButtons,
				tracked,
				clearNativeDrawable);

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
		var button = new Button
		{
			Text = string.Empty,
			ImageSource = source,
			ContentLayout = new Button.ButtonContentLayout(Button.ButtonContentLayout.ImagePosition.Left, 0),
			WidthRequest = 96,
			HeightRequest = 96
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

		await WaitForAsync(() => source.BitmapLoads > 0, "legacy ButtonRenderer did not request a button image bitmap.");

		var nativeButton = renderer.Control
			?? throw new InvalidOperationException("ButtonRenderer did not create a native AppCompatButton.");
		var assignedBeforeCleanup = CountPayloadSizedDrawables(nativeButton) > 0;
		var nativePeer = NativePeerRoot.Create(nativeButton);

		if (clearNativeDrawable)
			TextViewCompat.SetCompoundDrawablesRelativeWithIntrinsicBounds(nativeButton, null, null, null, null);

		renderer.Dispose();
		button.ImageSource = null;

		retainedNativeButtons.Add(nativePeer);
		tracked.Add(TrackedCycle.Create(cycle, nativePeer, renderer, button, source, assignedBeforeCleanup));
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

	static int CountPayloadSizedDrawables(AppCompatButton button)
	{
		var count = 0;
		var drawables = TextViewCompat.GetCompoundDrawablesRelative(button);
		if (drawables == null)
			return 0;

		foreach (var drawable in drawables)
		{
			if (GetDrawableArea(drawable) >= BitmapSide * BitmapSide)
				count++;
		}

		return count;
	}

	static DrawableSlotCount CountDrawableSlots(NativePeerRoot nativePeer)
	{
		var drawables = JNIEnv.CallObjectMethod(nativePeer.GlobalRef, GetCompoundDrawablesRelativeMethod);
		if (drawables == IntPtr.Zero)
			drawables = JNIEnv.CallObjectMethod(nativePeer.GlobalRef, GetCompoundDrawablesMethod);
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

	static long GetDrawableArea(Drawable? drawable)
	{
		if (drawable == null)
			return 0;

		var width = drawable.IntrinsicWidth;
		var height = drawable.IntrinsicHeight;
		if (width <= 0 || height <= 0)
			return 0;

		return (long)width * height;
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
		WeakReference<TrackingImageSource> Source,
		bool AssignedBeforeCleanup)
	{
		public static TrackedCycle Create(
			int cycle,
			NativePeerRoot nativeButton,
			ButtonRenderer renderer,
			Button button,
			TrackingImageSource source,
			bool assignedBeforeCleanup)
		{
			return new TrackedCycle(
				cycle,
				nativeButton,
				new WeakReference<ButtonRenderer>(renderer),
				new WeakReference<Button>(button),
				new WeakReference<TrackingImageSource>(source),
				assignedBeforeCleanup);
		}
	}

	internal sealed record DrawableSlotCount(int Assigned, int PayloadSized);

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeButtons,
		int AliveManagedRenderers,
		int AliveButtons,
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
			var aliveButtons = 0;
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

				if (cycle.Button.TryGetTarget(out _))
					aliveButtons++;

				if (cycle.Source.TryGetTarget(out _))
					aliveSources++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeButtons,
				aliveManagedRenderers,
				aliveButtons,
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
		Control.AliveButtons == 0 &&
		Current.AliveButtons == 0 &&
		Control.AliveSources == 0 &&
		Current.AliveSources == 0 &&
		Control.PayloadSizedNativeDrawableSlots == 0 &&
		Current.PayloadSizedNativeDrawableSlots == Cycles &&
		Current.RetainedDrawablePayloadBytes >= 90L * 1024 * 1024;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidLegacyButtonRendererImageDrawableRetentionRepro",
			$"Cycles: {Cycles}",
			$"Button image bitmap size: {BitmapSide}x{BitmapSide} ARGB ({FormatBytes(BitmapBytes)})",
			"Source path exercised: obsolete Android ButtonLayoutManager.UpdateImage -> AppCompatButton compound drawables",
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
			$"  retained native AppCompatButtons: {result.AliveNativeButtons}/{result.TrackedCycles}",
			$"  alive managed renderer wrappers after full GC: {result.AliveManagedRenderers}/{result.TrackedCycles}",
			$"  alive Buttons after full GC: {result.AliveButtons}/{result.TrackedCycles}",
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
			?? throw new InvalidOperationException("Failed to create the button image bitmap.");
		bitmap.EraseColor(AColor.Rgb((source.Cycle * 41) % 255, (source.Cycle * 71) % 255, (source.Cycle * 101) % 255));
		return Task.FromResult(bitmap);
	}
}
