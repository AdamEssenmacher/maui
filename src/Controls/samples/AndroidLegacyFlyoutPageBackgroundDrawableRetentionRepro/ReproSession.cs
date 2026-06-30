#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.Android;
using Microsoft.Maui.Controls.Platform;
using AColor = Android.Graphics.Color;
using AView = Android.Views.View;
using LegacyFlyoutPageRenderer = Microsoft.Maui.Controls.Compatibility.Platform.Android.FlyoutPageRenderer;

namespace AndroidLegacyFlyoutPageBackgroundDrawableRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 96;
	const int BitmapSide = 512;
	const int BitmapBytes = BitmapSide * BitmapSide * 4;

	static readonly List<object> RetainedNativePeerRoots = new();
	static readonly IntPtr ViewClass = JNIEnv.FindClass("android/view/View");
	static readonly IntPtr DrawableClass = JNIEnv.FindClass("android/graphics/drawable/Drawable");
	static readonly IntPtr GetBackgroundMethod = JNIEnv.GetMethodID(ViewClass, "getBackground", "()Landroid/graphics/drawable/Drawable;");
	static readonly IntPtr GetIntrinsicWidthMethod = JNIEnv.GetMethodID(DrawableClass, "getIntrinsicWidth", "()I");
	static readonly IntPtr GetIntrinsicHeightMethod = JNIEnv.GetMethodID(DrawableClass, "getIntrinsicHeight", "()I");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		RetainedNativePeerRoots.Clear();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			"control: clear FlyoutPageRenderer native background before disposal",
			context,
			clearNativeBackground: true);

		var current = await RunScenarioAsync(
			"current: dispose FlyoutPageRenderer without clearing native background",
			context,
			clearNativeBackground: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(forceFullCollection: true);
		GC.KeepAlive(RetainedNativePeerRoots);

		return new ReproReport(Cycles, BitmapSide, BitmapBytes, baselineBytes, finalBytes, control, current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearNativeBackground)
	{
		var retainedPeers = new List<NativePeerRoot>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			await CreateFlyoutCycleAsync(context, i, retainedPeers, tracked, clearNativeBackground);

			if (i % 12 == 0)
				await Task.Yield();
		}

		RetainedNativePeerRoots.Add(retainedPeers);
		await Task.Delay(250);
		ForceFullGc();
		GC.KeepAlive(retainedPeers);

		return ScenarioResult.From(name, tracked);
	}

	static async Task CreateFlyoutCycleAsync(
		IMauiContext context,
		int cycle,
		List<NativePeerRoot> retainedPeers,
		List<TrackedCycle> tracked,
		bool clearNativeBackground)
	{
		var source = new TrackingImageSource(cycle, BitmapSide);
		var flyout = new ContentPage
		{
			Title = $"Menu {cycle:000}",
			Content = new Label { Text = $"Flyout menu {cycle:000}" }
		};
		var detail = new ContentPage
		{
			Title = $"Detail {cycle:000}",
			Content = new Label { Text = $"Detail content {cycle:000}" }
		};
		var page = new FlyoutPage
		{
			Title = $"Flyout Background {cycle:000}",
			Flyout = flyout,
			Detail = detail,
			BackgroundImageSource = source
		};
		var renderer = new LegacyFlyoutPageRenderer(context.Context ?? throw new InvalidOperationException("Android context is not available."));

		((IVisualElementRenderer)renderer).SetElement(page);
		await WaitForAsync(() => source.BitmapLoads > 0, "legacy FlyoutPageRenderer did not request a background bitmap.");

		var nativePeer = NativePeerRoot.Create(renderer);
		var assignedBeforeCleanup = GetBackgroundArea(nativePeer) >= BitmapSide * BitmapSide;

		if (clearNativeBackground)
			renderer.SetBackground(null);

		renderer.Dispose();
		page.BackgroundImageSource = null;

		retainedPeers.Add(nativePeer);
		tracked.Add(TrackedCycle.Create(cycle, nativePeer, renderer, page, flyout, detail, source, assignedBeforeCleanup, BitmapBytes));
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

	static long GetBackgroundArea(NativePeerRoot nativePeer)
	{
		var background = JNIEnv.CallObjectMethod(nativePeer.GlobalRef, GetBackgroundMethod);
		if (background == IntPtr.Zero)
			return 0;

		try
		{
			var width = JNIEnv.CallIntMethod(background, GetIntrinsicWidthMethod);
			var height = JNIEnv.CallIntMethod(background, GetIntrinsicHeightMethod);

			if (width <= 0 || height <= 0)
				return 0;

			return (long)width * height;
		}
		finally
		{
			JNIEnv.DeleteLocalRef(background);
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
				throw new InvalidOperationException("Native FlyoutPageRenderer handle was not available before disposal.");

			var globalRef = JNIEnv.NewGlobalRef(view.Handle);
			if (globalRef == IntPtr.Zero)
				throw new InvalidOperationException("Failed to create a JNI global reference for the native FlyoutPageRenderer peer.");

			return new NativePeerRoot(globalRef);
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		NativePeerRoot NativePeer,
		WeakReference<LegacyFlyoutPageRenderer> ManagedRenderer,
		WeakReference<FlyoutPage> FlyoutPage,
		WeakReference<ContentPage> FlyoutChildPage,
		WeakReference<ContentPage> DetailChildPage,
		WeakReference<TrackingImageSource> Source,
		bool AssignedBeforeCleanup,
		long PayloadBytes)
	{
		public static TrackedCycle Create(
			int cycle,
			NativePeerRoot nativePeer,
			LegacyFlyoutPageRenderer renderer,
			FlyoutPage page,
			ContentPage flyout,
			ContentPage detail,
			TrackingImageSource source,
			bool assignedBeforeCleanup,
			long payloadBytes)
		{
			return new TrackedCycle(
				cycle,
				nativePeer,
				new WeakReference<LegacyFlyoutPageRenderer>(renderer),
				new WeakReference<FlyoutPage>(page),
				new WeakReference<ContentPage>(flyout),
				new WeakReference<ContentPage>(detail),
				new WeakReference<TrackingImageSource>(source),
				assignedBeforeCleanup,
				payloadBytes);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativePeers,
		int AliveManagedRenderers,
		int AliveFlyoutPages,
		int AliveChildPages,
		int AliveSources,
		int AssignedBeforeCleanup,
		int AssignedNativeBackgrounds,
		int PayloadSizedNativeBackgrounds,
		long RetainedPayloadBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativePeers = 0;
			var aliveManagedRenderers = 0;
			var aliveFlyoutPages = 0;
			var aliveChildPages = 0;
			var aliveSources = 0;
			var assignedBeforeCleanup = 0;
			var assignedNativeBackgrounds = 0;
			var payloadSizedNativeBackgrounds = 0;
			long retainedPayloadBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.AssignedBeforeCleanup)
					assignedBeforeCleanup++;

				if (cycle.NativePeer.GlobalRef != IntPtr.Zero)
				{
					aliveNativePeers++;
					var area = GetBackgroundArea(cycle.NativePeer);
					if (area > 0)
						assignedNativeBackgrounds++;
					if (area >= BitmapSide * BitmapSide)
					{
						payloadSizedNativeBackgrounds++;
						retainedPayloadBytes += cycle.PayloadBytes;
					}
				}

				if (cycle.ManagedRenderer.TryGetTarget(out _))
					aliveManagedRenderers++;
				if (cycle.FlyoutPage.TryGetTarget(out _))
					aliveFlyoutPages++;
				if (cycle.FlyoutChildPage.TryGetTarget(out _))
					aliveChildPages++;
				if (cycle.DetailChildPage.TryGetTarget(out _))
					aliveChildPages++;
				if (cycle.Source.TryGetTarget(out _))
					aliveSources++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativePeers,
				aliveManagedRenderers,
				aliveFlyoutPages,
				aliveChildPages,
				aliveSources,
				assignedBeforeCleanup,
				assignedNativeBackgrounds,
				payloadSizedNativeBackgrounds,
				retainedPayloadBytes);
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
		Control.AliveNativePeers == Cycles &&
		Current.AliveNativePeers == Cycles &&
		Control.AliveManagedRenderers == 0 &&
		Current.AliveManagedRenderers == 0 &&
		Control.AliveFlyoutPages == 0 &&
		Current.AliveFlyoutPages == 0 &&
		Control.AliveChildPages == 0 &&
		Current.AliveChildPages == 0 &&
		Control.AliveSources == 0 &&
		Current.AliveSources == 0 &&
		Control.PayloadSizedNativeBackgrounds == 0 &&
		Current.PayloadSizedNativeBackgrounds == Cycles &&
		Current.RetainedPayloadBytes >= 90L * 1024 * 1024;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidLegacyFlyoutPageBackgroundDrawableRetentionRepro",
			$"Cycles: {Cycles}",
			$"Background bitmap size: {BitmapSide}x{BitmapSide} ARGB ({FormatBytes(BitmapBytes)})",
			"Source path exercised: obsolete Android FlyoutPageRenderer -> View.SetBackground(drawable)",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained background payload: {FormatBytes(Control.RetainedPayloadBytes)}",
			$"Current retained background payload: {FormatBytes(Current.RetainedPayloadBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  payload backgrounds assigned before cleanup: {result.AssignedBeforeCleanup}/{result.TrackedCycles}",
			$"  retained native FlyoutPageRenderer peers: {result.AliveNativePeers}/{result.TrackedCycles}",
			$"  alive managed renderer wrappers after full GC: {result.AliveManagedRenderers}/{result.TrackedCycles}",
			$"  alive FlyoutPages after full GC: {result.AliveFlyoutPages}/{result.TrackedCycles}",
			$"  alive child pages after full GC: {result.AliveChildPages}/{result.TrackedCycles * 2}",
			$"  alive image sources after full GC: {result.AliveSources}/{result.TrackedCycles}",
			$"  assigned native background slots: {result.AssignedNativeBackgrounds}/{result.TrackedCycles}",
			$"  payload-sized native background slots: {result.PayloadSizedNativeBackgrounds}/{result.TrackedCycles}",
			$"  retained background payload bytes: {result.RetainedPayloadBytes:N0}");
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
	public Task LoadImageAsync(ImageSource imagesource, ImageView imageView, CancellationToken cancellationToken = default)
	{
		return Task.CompletedTask;
	}

	public Task<Bitmap> LoadImageAsync(ImageSource imagesource, Context context, CancellationToken cancelationToken = default)
	{
		if (imagesource is not TrackingImageSource source)
			return Task.FromResult<Bitmap>(null!);

		source.BitmapLoads++;
		var bitmap = Bitmap.CreateBitmap(source.BitmapSide, source.BitmapSide, Bitmap.Config.Argb8888!)
			?? throw new InvalidOperationException("Failed to create the flyout page background bitmap.");
		bitmap.EraseColor(AColor.Rgb((source.Cycle * 41) % 255, (source.Cycle * 71) % 255, (source.Cycle * 101) % 255));
		return Task.FromResult(bitmap);
	}
}
