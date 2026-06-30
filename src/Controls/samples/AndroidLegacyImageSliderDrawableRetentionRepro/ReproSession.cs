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
using Android.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.Android;
using AColor = Android.Graphics.Color;
using AView = Android.Views.View;
using LegacyImageRenderer = Microsoft.Maui.Controls.Compatibility.Platform.Android.ImageRenderer;
using LegacySliderRenderer = Microsoft.Maui.Controls.Compatibility.Platform.Android.SliderRenderer;
using MauiImage = Microsoft.Maui.Controls.Image;
using MauiSlider = Microsoft.Maui.Controls.Slider;

namespace AndroidLegacyImageSliderDrawableRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 96;
	const int ImagePayloadBytes = 1024 * 1024;
	const int SliderBitmapSide = 512;
	const int SliderBitmapBytes = SliderBitmapSide * SliderBitmapSide * 4;

	static readonly List<object> RetainedNativePeerRoots = new();
	static readonly IntPtr ImageViewClass = JNIEnv.FindClass("android/widget/ImageView");
	static readonly IntPtr AbsSeekBarClass = JNIEnv.FindClass("android/widget/AbsSeekBar");
	static readonly IntPtr DrawableClass = JNIEnv.FindClass("android/graphics/drawable/Drawable");
	static readonly IntPtr GetDrawableMethod = JNIEnv.GetMethodID(ImageViewClass, "getDrawable", "()Landroid/graphics/drawable/Drawable;");
	static readonly IntPtr GetThumbMethod = JNIEnv.GetMethodID(AbsSeekBarClass, "getThumb", "()Landroid/graphics/drawable/Drawable;");
	static readonly IntPtr GetIntrinsicWidthMethod = JNIEnv.GetMethodID(DrawableClass, "getIntrinsicWidth", "()I");
	static readonly IntPtr GetIntrinsicHeightMethod = JNIEnv.GetMethodID(DrawableClass, "getIntrinsicHeight", "()I");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		RetainedNativePeerRoots.Clear();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			"control: clear legacy ImageRenderer drawable and SliderRenderer thumb before disposal",
			context,
			clearNativeSlots: true);

		var current = await RunScenarioAsync(
			"current: dispose legacy renderers without clearing native drawable/thumb slots",
			context,
			clearNativeSlots: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(forceFullCollection: true);
		GC.KeepAlive(RetainedNativePeerRoots);

		return new ReproReport(
			Cycles,
			ImagePayloadBytes,
			SliderBitmapSide,
			SliderBitmapBytes,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearNativeSlots)
	{
		var retainedPeers = new List<NativePeerRoot>(Cycles * 2);
		var tracked = new List<TrackedCycle>(Cycles * 2);

		for (var i = 0; i < Cycles; i++)
		{
			await CreateImageCycleAsync(context, i, retainedPeers, tracked, clearNativeSlots);
			await CreateSliderCycleAsync(context, i, retainedPeers, tracked, clearNativeSlots);

			if (i % 12 == 0)
				await Task.Yield();
		}

		RetainedNativePeerRoots.Add(retainedPeers);
		await Task.Delay(250);
		ForceFullGc();
		GC.KeepAlive(retainedPeers);

		return ScenarioResult.From(name, tracked);
	}

	static async Task CreateImageCycleAsync(
		IMauiContext context,
		int cycle,
		List<NativePeerRoot> retainedPeers,
		List<TrackedCycle> tracked,
		bool clearNativeSlots)
	{
		var source = new TrackingImageSource("ImageRenderer", cycle, ImagePayloadBytes, SliderBitmapSide);
		var image = new MauiImage
		{
			Source = source,
			Aspect = Aspect.AspectFit,
			WidthRequest = 256,
			HeightRequest = 256
		};
		var renderer = new LegacyImageRenderer(context.Context ?? throw new InvalidOperationException("Android context is not available."));

		((IVisualElementRenderer)renderer).SetElement(image);
		await WaitForAsync(() => source.LoadedDrawable != null, "legacy ImageRenderer did not assign a drawable.");

		var nativeImageView = renderer.Control ?? throw new InvalidOperationException("ImageRenderer did not create an ImageView.");
		var nativePeer = NativePeerRoot.Create(nativeImageView, NativePeerKind.ImageView);
		var assignedBeforeCleanup = GetDrawableArea(nativePeer) > 0;
		var loadedDrawable = source.LoadedDrawable ?? throw new InvalidOperationException("ImageRenderer source did not retain its loaded drawable.");
		var payload = loadedDrawable.Payload;

		if (clearNativeSlots)
			nativeImageView.SetImageDrawable(null);

		renderer.Dispose();
		image.Source = null;

		retainedPeers.Add(nativePeer);
		tracked.Add(TrackedCycle.CreateImage(
			cycle,
			nativePeer,
			renderer,
			image,
			source,
			loadedDrawable,
			payload,
			assignedBeforeCleanup,
			ImagePayloadBytes));
	}

	static async Task CreateSliderCycleAsync(
		IMauiContext context,
		int cycle,
		List<NativePeerRoot> retainedPeers,
		List<TrackedCycle> tracked,
		bool clearNativeSlots)
	{
		var source = new TrackingImageSource("SliderRenderer", cycle, ImagePayloadBytes, SliderBitmapSide);
		var slider = new MauiSlider
		{
			ThumbImageSource = source,
			Minimum = 0,
			Maximum = 100,
			Value = cycle % 100,
			WidthRequest = 320
		};
		var renderer = new LegacySliderRenderer(context.Context ?? throw new InvalidOperationException("Android context is not available."));

		((IVisualElementRenderer)renderer).SetElement(slider);
		await WaitForAsync(() => source.BitmapLoads > 0, "legacy SliderRenderer did not request a thumb bitmap.");

		var nativeSeekBar = renderer.Control ?? throw new InvalidOperationException("SliderRenderer did not create a SeekBar.");
		var nativePeer = NativePeerRoot.Create(nativeSeekBar, NativePeerKind.SeekBar);
		var assignedBeforeCleanup = GetDrawableArea(nativePeer) >= SliderBitmapSide * SliderBitmapSide;

		if (clearNativeSlots)
			nativeSeekBar.SetThumb(null);

		renderer.Dispose();
		slider.ThumbImageSource = null;

		retainedPeers.Add(nativePeer);
		tracked.Add(TrackedCycle.CreateSlider(
			cycle,
			nativePeer,
			renderer,
			slider,
			source,
			assignedBeforeCleanup,
			SliderBitmapBytes));
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

	static long GetDrawableArea(NativePeerRoot nativePeer)
	{
		var drawable = JNIEnv.CallObjectMethod(nativePeer.GlobalRef, nativePeer.Kind == NativePeerKind.ImageView ? GetDrawableMethod : GetThumbMethod);
		if (drawable == IntPtr.Zero)
			return 0;

		try
		{
			var width = JNIEnv.CallIntMethod(drawable, GetIntrinsicWidthMethod);
			var height = JNIEnv.CallIntMethod(drawable, GetIntrinsicHeightMethod);

			if (width <= 0 || height <= 0)
				return 0;

			return (long)width * height;
		}
		finally
		{
			JNIEnv.DeleteLocalRef(drawable);
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

	internal enum NativePeerKind
	{
		ImageView,
		SeekBar
	}

	internal sealed record NativePeerRoot(IntPtr GlobalRef, NativePeerKind Kind)
	{
		public static NativePeerRoot Create(AView view, NativePeerKind kind)
		{
			if (view.Handle == IntPtr.Zero)
				throw new InvalidOperationException("Native peer handle was not available before renderer disposal.");

			var globalRef = JNIEnv.NewGlobalRef(view.Handle);
			if (globalRef == IntPtr.Zero)
				throw new InvalidOperationException("Failed to create a JNI global reference for the native peer.");

			return new NativePeerRoot(globalRef, kind);
		}
	}

	internal sealed record TrackedCycle(
		string ControlType,
		int Cycle,
		NativePeerRoot NativePeer,
		WeakReference<object> ManagedRenderer,
		WeakReference<object> VirtualView,
		WeakReference<TrackingImageSource> Source,
		WeakReference<TrackingDrawable>? Drawable,
		WeakReference<byte[]>? Payload,
		bool AssignedBeforeCleanup,
		long PayloadBytes)
	{
		public static TrackedCycle CreateImage(
			int cycle,
			NativePeerRoot nativePeer,
			LegacyImageRenderer renderer,
			MauiImage image,
			TrackingImageSource source,
			TrackingDrawable drawable,
			byte[] payload,
			bool assignedBeforeCleanup,
			long payloadBytes)
		{
			return new TrackedCycle(
				"ImageRenderer",
				cycle,
				nativePeer,
				new WeakReference<object>(renderer),
				new WeakReference<object>(image),
				new WeakReference<TrackingImageSource>(source),
				new WeakReference<TrackingDrawable>(drawable),
				new WeakReference<byte[]>(payload),
				assignedBeforeCleanup,
				payloadBytes);
		}

		public static TrackedCycle CreateSlider(
			int cycle,
			NativePeerRoot nativePeer,
			LegacySliderRenderer renderer,
			MauiSlider slider,
			TrackingImageSource source,
			bool assignedBeforeCleanup,
			long payloadBytes)
		{
			return new TrackedCycle(
				"SliderRenderer",
				cycle,
				nativePeer,
				new WeakReference<object>(renderer),
				new WeakReference<object>(slider),
				new WeakReference<TrackingImageSource>(source),
				null,
				null,
				assignedBeforeCleanup,
				payloadBytes);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativePeers,
		int AliveManagedRenderers,
		int AliveVirtualViews,
		int AliveSources,
		int AssignedBeforeCleanup,
		int AssignedNativeSlots,
		int PayloadSizedNativeSlots,
		int AliveImageDrawables,
		int AliveImagePayloads,
		long RetainedPayloadBytes,
		IReadOnlyDictionary<string, TypeResult> ByControlType)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativePeers = 0;
			var aliveManagedRenderers = 0;
			var aliveVirtualViews = 0;
			var aliveSources = 0;
			var assignedBeforeCleanup = 0;
			var assignedNativeSlots = 0;
			var payloadSizedNativeSlots = 0;
			var aliveImageDrawables = 0;
			var aliveImagePayloads = 0;
			long retainedPayloadBytes = 0;
			var byType = new Dictionary<string, TypeCounter>(StringComparer.Ordinal);

			foreach (var cycle in tracked)
			{
				var counter = GetCounter(byType, cycle.ControlType);
				counter.Tracked++;

				if (cycle.AssignedBeforeCleanup)
				{
					assignedBeforeCleanup++;
					counter.AssignedBeforeCleanup++;
				}

				if (cycle.NativePeer.GlobalRef != IntPtr.Zero)
				{
					aliveNativePeers++;
					counter.AliveNativePeers++;

					var area = GetDrawableArea(cycle.NativePeer);
					if (area > 0)
					{
						assignedNativeSlots++;
						counter.AssignedNativeSlots++;
					}

					if (area >= SliderBitmapSide * SliderBitmapSide)
					{
						payloadSizedNativeSlots++;
						counter.PayloadSizedNativeSlots++;
						retainedPayloadBytes += cycle.PayloadBytes;
						counter.RetainedPayloadBytes += cycle.PayloadBytes;
					}
				}

				if (cycle.ManagedRenderer.TryGetTarget(out _))
					aliveManagedRenderers++;
				if (cycle.VirtualView.TryGetTarget(out _))
					aliveVirtualViews++;
				if (cycle.Source.TryGetTarget(out _))
					aliveSources++;

				if (cycle.Drawable?.TryGetTarget(out _) == true)
				{
					aliveImageDrawables++;
					counter.AliveImageDrawables++;
				}

				if (cycle.Payload?.TryGetTarget(out _) == true)
				{
					aliveImagePayloads++;
					counter.AliveImagePayloads++;
				}
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativePeers,
				aliveManagedRenderers,
				aliveVirtualViews,
				aliveSources,
				assignedBeforeCleanup,
				assignedNativeSlots,
				payloadSizedNativeSlots,
				aliveImageDrawables,
				aliveImagePayloads,
				retainedPayloadBytes,
				byType.ToDictionary(pair => pair.Key, pair => pair.Value.ToResult(), StringComparer.Ordinal));
		}

		static TypeCounter GetCounter(Dictionary<string, TypeCounter> values, string controlType)
		{
			if (!values.TryGetValue(controlType, out var counter))
			{
				counter = new TypeCounter();
				values.Add(controlType, counter);
			}

			return counter;
		}
	}

	internal sealed record TypeResult(
		int Tracked,
		int AliveNativePeers,
		int AssignedBeforeCleanup,
		int AssignedNativeSlots,
		int PayloadSizedNativeSlots,
		int AliveImageDrawables,
		int AliveImagePayloads,
		long RetainedPayloadBytes);

	sealed class TypeCounter
	{
		public int Tracked { get; set; }
		public int AliveNativePeers { get; set; }
		public int AssignedBeforeCleanup { get; set; }
		public int AssignedNativeSlots { get; set; }
		public int PayloadSizedNativeSlots { get; set; }
		public int AliveImageDrawables { get; set; }
		public int AliveImagePayloads { get; set; }
		public long RetainedPayloadBytes { get; set; }

		public TypeResult ToResult() =>
			new(
				Tracked,
				AliveNativePeers,
				AssignedBeforeCleanup,
				AssignedNativeSlots,
				PayloadSizedNativeSlots,
				AliveImageDrawables,
				AliveImagePayloads,
				RetainedPayloadBytes);
	}
}

internal sealed record ReproReport(
	int Cycles,
	int ImagePayloadBytes,
	int SliderBitmapSide,
	int SliderBitmapBytes,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	int TotalCycles => Cycles * 2;

	public bool LeakProved =>
		Control.AssignedBeforeCleanup == TotalCycles &&
		Current.AssignedBeforeCleanup == TotalCycles &&
		Control.AliveNativePeers == TotalCycles &&
		Current.AliveNativePeers == TotalCycles &&
		Control.AliveManagedRenderers == 0 &&
		Current.AliveManagedRenderers == 0 &&
		Control.AliveVirtualViews == 0 &&
		Current.AliveVirtualViews == 0 &&
		Control.AliveSources == 0 &&
		Current.AliveSources == 0 &&
		Control.PayloadSizedNativeSlots == 0 &&
		Control.AliveImagePayloads == 0 &&
		Current.ByControlType.TryGetValue("ImageRenderer", out var image) &&
		image.PayloadSizedNativeSlots == Cycles &&
		image.AliveImagePayloads == Cycles &&
		Current.ByControlType.TryGetValue("SliderRenderer", out var slider) &&
		slider.PayloadSizedNativeSlots == Cycles &&
		Current.RetainedPayloadBytes >= 180L * 1024 * 1024;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidLegacyImageSliderDrawableRetentionRepro",
			$"Cycles per renderer type: {Cycles}",
			$"Total renderer cycles per scenario: {TotalCycles}",
			$"ImageRenderer payload per drawable: {FormatBytes(ImagePayloadBytes)}",
			$"SliderRenderer thumb bitmap size: {SliderBitmapSide}x{SliderBitmapSide} ARGB ({FormatBytes(SliderBitmapBytes)})",
			"Source paths exercised: obsolete Android ImageRenderer -> ImageView.SetImageDrawable and SliderRenderer -> SeekBar.SetThumb",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained drawable/thumb payload: {FormatBytes(Control.RetainedPayloadBytes)}",
			$"Current retained drawable/thumb payload: {FormatBytes(Current.RetainedPayloadBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var lines = new List<string>
		{
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  payload drawable/thumb slots assigned before cleanup: {result.AssignedBeforeCleanup}/{result.TrackedCycles}",
			$"  retained native child peers: {result.AliveNativePeers}/{result.TrackedCycles}",
			$"  alive managed renderers after full GC: {result.AliveManagedRenderers}/{result.TrackedCycles}",
			$"  alive virtual views after full GC: {result.AliveVirtualViews}/{result.TrackedCycles}",
			$"  alive image sources after full GC: {result.AliveSources}/{result.TrackedCycles}",
			$"  assigned native drawable/thumb slots: {result.AssignedNativeSlots}/{result.TrackedCycles}",
			$"  payload-sized native drawable/thumb slots: {result.PayloadSizedNativeSlots}/{result.TrackedCycles}",
			$"  alive ImageRenderer managed drawables: {result.AliveImageDrawables}",
			$"  alive ImageRenderer payload byte arrays: {result.AliveImagePayloads}",
			$"  retained drawable/thumb payload bytes: {result.RetainedPayloadBytes:N0}"
		};

		foreach (var pair in result.ByControlType.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
		{
			var value = pair.Value;
			lines.Add(
				$"  {pair.Key}: native={value.AliveNativePeers}/{value.Tracked}, assignedBefore={value.AssignedBeforeCleanup}/{value.Tracked}, assignedAfter={value.AssignedNativeSlots}/{value.Tracked}, payloadSlots={value.PayloadSizedNativeSlots}/{value.Tracked}, imagePayloads={value.AliveImagePayloads}/{value.Tracked}, retained={value.RetainedPayloadBytes:N0}");
		}

		return string.Join(Environment.NewLine, lines);
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
	public TrackingImageSource(string controlType, int cycle, int payloadBytes, int bitmapSide)
	{
		ControlType = controlType;
		Cycle = cycle;
		PayloadBytes = payloadBytes;
		BitmapSide = bitmapSide;
	}

	public string ControlType { get; }

	public int Cycle { get; }

	public int PayloadBytes { get; }

	public int BitmapSide { get; }

	public TrackingDrawable? LoadedDrawable { get; set; }

	public int BitmapLoads { get; set; }

	public override bool IsEmpty => false;
}

internal sealed class TrackingImageSourceHandler : IImageViewHandler, IImageSourceHandler
{
	public Task LoadImageAsync(ImageSource imagesource, ImageView imageView, CancellationToken cancellationToken = default)
	{
		if (imagesource is not TrackingImageSource source)
			return Task.CompletedTask;

		var drawable = new TrackingDrawable(
			source.ControlType,
			source.Cycle,
			source.PayloadBytes,
			source.BitmapSide);

		source.LoadedDrawable = drawable;
		imageView.SetImageDrawable(drawable);
		return Task.CompletedTask;
	}

	public Task<Bitmap> LoadImageAsync(ImageSource imagesource, Context context, CancellationToken cancelationToken = default)
	{
		if (imagesource is not TrackingImageSource source)
			return Task.FromResult<Bitmap>(null!);

		source.BitmapLoads++;
		var bitmap = Bitmap.CreateBitmap(source.BitmapSide, source.BitmapSide, Bitmap.Config.Argb8888!)
			?? throw new InvalidOperationException("Failed to create the slider thumb bitmap.");
		bitmap.EraseColor(AColor.Rgb((source.Cycle * 41) % 255, (source.Cycle * 71) % 255, (source.Cycle * 101) % 255));
		return Task.FromResult(bitmap);
	}
}

internal sealed class TrackingDrawable : ColorDrawable
{
	readonly int _side;

	public TrackingDrawable(string controlType, int cycle, int payloadBytes, int side)
		: base(AColor.Rgb((cycle * 37) % 255, (cycle * 67) % 255, (cycle * 97) % 255))
	{
		ControlType = controlType;
		Cycle = cycle;
		PayloadBytes = payloadBytes;
		_side = side;
		Payload = new byte[payloadBytes];

		for (var i = 0; i < Payload.Length; i += 4096)
			Payload[i] = (byte)(cycle + i + controlType.Length);
	}

	public string ControlType { get; }

	public int Cycle { get; }

	public int PayloadBytes { get; }

	public byte[] Payload { get; }

	public override int IntrinsicWidth => _side;

	public override int IntrinsicHeight => _side;
}
