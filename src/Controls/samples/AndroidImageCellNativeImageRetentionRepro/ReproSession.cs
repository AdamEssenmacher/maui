#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using AView = Android.Views.View;

namespace AndroidImageCellNativeImageRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 96;
	internal const int BitmapEdge = 512;
	const int BytesPerPixel = 4;
	internal const int EstimatedDrawableBytes = BitmapEdge * BitmapEdge * BytesPerPixel;
	const int SourcePayloadBytes = 256 * 1024;
	const int TotalPayloadBytesPerCycle = EstimatedDrawableBytes + SourcePayloadBytes;

	static readonly BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;
	static readonly FieldInfo CellField = typeof(BaseCellView).GetField("_cell", InstanceNonPublic)
		?? throw new MissingFieldException(typeof(BaseCellView).FullName, "_cell");
	static readonly FieldInfo ImageSourceField = typeof(BaseCellView).GetField("_imageSource", InstanceNonPublic)
		?? throw new MissingFieldException(typeof(BaseCellView).FullName, "_imageSource");
	static readonly FieldInfo ImageViewField = typeof(BaseCellView).GetField("_imageView", InstanceNonPublic)
		?? throw new MissingFieldException(typeof(BaseCellView).FullName, "_imageView");

	static readonly List<BaseCellView> RetainedNativeRows = new();

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		RetainedNativeRows.Clear();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			"control: clear BaseCellView image source and ImageView drawable after disconnect",
			context,
			clearNativeImageState: true);

		var current = await RunScenarioAsync(
			"current: MAUI disconnect leaves BaseCellView image source and ImageView drawable assigned",
			context,
			clearNativeImageState: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeRows);

		return new ReproReport(
			Cycles,
			BitmapEdge,
			EstimatedDrawableBytes,
			SourcePayloadBytes,
			TotalPayloadBytesPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearNativeImageState)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateCycle(context, i, tracked, clearNativeImageState);

			if (i % 16 == 0)
				await Task.Yield();
		}

		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	static void CreateCycle(
		IMauiContext context,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeImageState)
	{
		var imageSource = new PayloadImageSource(cycle, SourcePayloadBytes);
		var cell = new ImageCell
		{
			Text = $"Image row {cycle:D4}",
			Detail = "short detail",
			ImageSource = imageSource,
			BindingContext = new object()
		};

		var renderer = new ImageCellRenderer();
		renderer.ParentView = new Grid { FlowDirection = FlowDirection.LeftToRight };
		renderer.SetMauiContext(context);
		cell.Handler = renderer;
		renderer.SetVirtualView(cell);

		if (renderer.PlatformView is not BaseCellView nativeRow)
			throw new InvalidOperationException($"Expected {nameof(BaseCellView)}, got {renderer.PlatformView?.GetType().FullName ?? "<null>"}.");

		var drawable = GetPayloadDrawable(nativeRow);
		if (drawable is null)
			throw new InvalidOperationException("The ImageCell renderer did not assign the payload drawable.");

		((IElementHandler)renderer).DisconnectHandler();

		cell.ImageSource = null;
		cell.BindingContext = null;
		ClearKnownCellBackReference(nativeRow);

		if (clearNativeImageState)
			ClearNativeImageState(nativeRow);

		RetainedNativeRows.Add(nativeRow);
		tracked.Add(TrackedCycle.Create(cycle, nativeRow, cell, renderer, imageSource, drawable));
	}

	static void ClearKnownCellBackReference(BaseCellView nativeRow)
	{
		CellField.SetValue(nativeRow, null);
	}

	static void ClearNativeImageState(BaseCellView nativeRow)
	{
		ImageSourceField.SetValue(nativeRow, null);

		if (ImageViewField.GetValue(nativeRow) is ImageView imageView)
		{
			var drawable = imageView.Drawable;
			imageView.SetImageDrawable(null);
			drawable?.Dispose();
		}
	}

	static PayloadDrawable? GetPayloadDrawable(BaseCellView nativeRow)
	{
		if (ImageViewField.GetValue(nativeRow) is not ImageView imageView)
			return null;

		return imageView.Drawable as PayloadDrawable;
	}

	static PayloadImageSource? GetPayloadImageSource(BaseCellView nativeRow)
	{
		return ImageSourceField.GetValue(nativeRow) as PayloadImageSource;
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

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<BaseCellView> NativeRow,
		WeakReference<ImageCell> Cell,
		WeakReference<ImageCellRenderer> Renderer,
		WeakReference<PayloadImageSource> ImageSource,
		WeakReference<PayloadDrawable> Drawable)
	{
		public static TrackedCycle Create(
			int cycle,
			BaseCellView nativeRow,
			ImageCell cell,
			ImageCellRenderer renderer,
			PayloadImageSource imageSource,
			PayloadDrawable drawable)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<BaseCellView>(nativeRow),
				new WeakReference<ImageCell>(cell),
				new WeakReference<ImageCellRenderer>(renderer),
				new WeakReference<PayloadImageSource>(imageSource),
				new WeakReference<PayloadDrawable>(drawable));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeRows,
		int AliveCells,
		int AliveRenderers,
		int AliveImageSources,
		int AlivePayloadDrawables,
		int NativeRowsWithPayloadImageSourceField,
		int NativeRowsWithPayloadDrawable,
		long RetainedSourcePayloadBytes,
		long RetainedDrawablePayloadBytes,
		long RetainedEstimatedDrawableBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativeRows = 0;
			var aliveCells = 0;
			var aliveRenderers = 0;
			var aliveImageSources = 0;
			var alivePayloadDrawables = 0;
			var nativeRowsWithPayloadImageSourceField = 0;
			var nativeRowsWithPayloadDrawable = 0;
			long retainedSourcePayloadBytes = 0;
			long retainedDrawablePayloadBytes = 0;
			long retainedEstimatedDrawableBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativeRow.TryGetTarget(out var nativeRow))
				{
					aliveNativeRows++;

					if (GetPayloadImageSource(nativeRow) is { } retainedSource)
					{
						nativeRowsWithPayloadImageSourceField++;
						retainedSourcePayloadBytes += retainedSource.PayloadLength;
					}

					if (GetPayloadDrawable(nativeRow) is { } retainedDrawable)
					{
						nativeRowsWithPayloadDrawable++;
						retainedDrawablePayloadBytes += retainedDrawable.PayloadLength;
						retainedEstimatedDrawableBytes += retainedDrawable.EstimatedBitmapBytes;
					}
				}

				if (cycle.Cell.TryGetTarget(out _))
					aliveCells++;

				if (cycle.Renderer.TryGetTarget(out _))
					aliveRenderers++;

				if (cycle.ImageSource.TryGetTarget(out _))
					aliveImageSources++;

				if (cycle.Drawable.TryGetTarget(out _))
					alivePayloadDrawables++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeRows,
				aliveCells,
				aliveRenderers,
				aliveImageSources,
				alivePayloadDrawables,
				nativeRowsWithPayloadImageSourceField,
				nativeRowsWithPayloadDrawable,
				retainedSourcePayloadBytes,
				retainedDrawablePayloadBytes,
				retainedEstimatedDrawableBytes);
		}
	}
}

internal sealed class PayloadImageSource : ImageSource
{
	readonly byte[] _payload;

	public PayloadImageSource(int cycle, int payloadBytes)
	{
		Cycle = cycle;
		_payload = new byte[payloadBytes];
		Array.Fill(_payload, (byte)(cycle % 251));
	}

	public int Cycle { get; }

	public int PayloadLength => _payload.Length;
}

internal sealed class PayloadImageSourceService : IImageSourceService<PayloadImageSource>
{
	public Task<IImageSourceServiceResult?> LoadDrawableAsync(
		IImageSource imageSource,
		ImageView imageView,
		CancellationToken cancellationToken = default)
	{
		if (imageSource is not PayloadImageSource payloadSource)
			return Task.FromResult<IImageSourceServiceResult?>(new ImageSourceServiceLoadResult());

		var drawable = CreateDrawable(imageView.Context, payloadSource);
		imageView.SetImageDrawable(drawable);

		return Task.FromResult<IImageSourceServiceResult?>(
			new ImageSourceServiceLoadResult(dispose: drawable.Dispose));
	}

	public Task<IImageSourceServiceResult<Drawable>?> GetDrawableAsync(
		IImageSource imageSource,
		Context context,
		CancellationToken cancellationToken = default)
	{
		if (imageSource is not PayloadImageSource payloadSource)
			return Task.FromResult<IImageSourceServiceResult<Drawable>?>(null);

		var drawable = CreateDrawable(context, payloadSource);
		return Task.FromResult<IImageSourceServiceResult<Drawable>?>(
			new ImageSourceServiceResult(drawable, dispose: drawable.Dispose));
	}

	static PayloadDrawable CreateDrawable(Context? context, PayloadImageSource payloadSource)
	{
		var resources = context?.Resources ?? Resources.System;
		return new PayloadDrawable(resources, payloadSource.Cycle, ReproSession.BitmapEdge, ReproSession.EstimatedDrawableBytes);
	}
}

internal sealed class PayloadDrawable : BitmapDrawable
{
	readonly byte[] _payload;

	public PayloadDrawable(Resources? resources, int cycle, int edge, int estimatedBitmapBytes)
		: base(resources, CreateBitmap(cycle, edge))
	{
		Cycle = cycle;
		EstimatedBitmapBytes = estimatedBitmapBytes;
		_payload = new byte[estimatedBitmapBytes];
		Array.Fill(_payload, (byte)((cycle + 97) % 251));
	}

	public int Cycle { get; }

	public int PayloadLength => _payload.Length;

	public int EstimatedBitmapBytes { get; }

	static Bitmap CreateBitmap(int cycle, int edge)
	{
		var bitmap = Bitmap.CreateBitmap(edge, edge, Bitmap.Config.Argb8888!);
		var color = Android.Graphics.Color.Rgb((cycle * 53) % 255, (cycle * 97) % 255, (cycle * 193) % 255);
		bitmap.EraseColor(color);
		return bitmap;
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
			Bitmap?.Dispose();

		base.Dispose(disposing);
	}
}

internal sealed record ReproReport(
	int Cycles,
	int BitmapEdge,
	int EstimatedDrawableBytes,
	int SourcePayloadBytes,
	int TotalPayloadBytesPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.AliveNativeRows == Cycles &&
		Current.AliveNativeRows == Cycles &&
		Control.NativeRowsWithPayloadImageSourceField == 0 &&
		Control.NativeRowsWithPayloadDrawable == 0 &&
		Control.AliveImageSources == 0 &&
		Control.AlivePayloadDrawables == 0 &&
		Current.NativeRowsWithPayloadImageSourceField == Cycles &&
		Current.NativeRowsWithPayloadDrawable == Cycles &&
		Current.AliveImageSources == Cycles &&
		Current.AlivePayloadDrawables == Cycles &&
		Current.AliveCells == 0 &&
		Current.AliveRenderers == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var currentTotalBytes = Current.RetainedSourcePayloadBytes + Current.RetainedDrawablePayloadBytes;

		return string.Join(Environment.NewLine,
			"AndroidImageCellNativeImageRetentionRepro",
			$"Cycles: {Cycles}",
			$"Bitmap edge: {BitmapEdge}px",
			$"Estimated drawable bytes per cycle: {EstimatedDrawableBytes:N0}",
			$"Source payload bytes per cycle: {SourcePayloadBytes:N0}",
			$"Total payload bytes per cycle: {TotalPayloadBytesPerCycle:N0}",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained payload: {FormatBytes(Control.RetainedSourcePayloadBytes + Control.RetainedDrawablePayloadBytes)}",
			$"Current retained payload: {FormatBytes(currentTotalBytes)}",
			$"Current estimated retained bitmap bytes: {FormatBytes(Current.RetainedEstimatedDrawableBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  alive native rows: {result.AliveNativeRows}/{result.TrackedCycles}",
			$"  alive cells: {result.AliveCells}/{result.TrackedCycles}",
			$"  alive renderers: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  alive image sources: {result.AliveImageSources}/{result.TrackedCycles}",
			$"  alive payload drawables: {result.AlivePayloadDrawables}/{result.TrackedCycles}",
			$"  native rows with payload _imageSource: {result.NativeRowsWithPayloadImageSourceField}/{result.TrackedCycles}",
			$"  native rows with payload ImageView drawable: {result.NativeRowsWithPayloadDrawable}/{result.TrackedCycles}",
			$"  retained source payload bytes: {result.RetainedSourcePayloadBytes:N0}",
			$"  retained drawable payload bytes: {result.RetainedDrawablePayloadBytes:N0}",
			$"  estimated retained bitmap bytes: {result.RetainedEstimatedDrawableBytes:N0}");
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
