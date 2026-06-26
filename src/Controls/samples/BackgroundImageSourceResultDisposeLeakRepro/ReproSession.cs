using System.Runtime.InteropServices;
using System.Threading;
using CoreAnimation;
using CoreGraphics;
using Microsoft.Maui;
using Microsoft.Maui.Platform;
using UIKit;

namespace BackgroundImageSourceResultDisposeLeakRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadMegabytesPerBackground = 2;
	const long PayloadSizeBytes = PayloadMegabytesPerBackground * 1024L * 1024L;

	public static ReproReport Run()
	{
		ForceFullGc();
		var baselineManagedBytes = GC.GetTotalMemory(true);

		var control = RunControl();
		var leak = RunCurrentBackgroundImagePath();

		ForceFullGc();
		var finalManagedBytes = GC.GetTotalMemory(true);

		return new ReproReport(
			Cycles,
			PayloadMegabytesPerBackground,
			baselineManagedBytes,
			finalManagedBytes,
			control,
			leak);
	}

	static ScenarioResult RunControl()
	{
		var ledger = new ScenarioLedger("control: background image helper disposes each image-service result");
		var provider = new TrackingImageSourceServiceProvider(ledger);

		for (var i = 0; i < Cycles; i++)
		{
			using var view = CreateHostView();
			UpdateBackgroundImageSourceAndDisposeResultAsync(view, new TrackingImageSource(i), provider)
				.GetAwaiter()
				.GetResult();
			view.RemoveBackgroundLayer();
		}

		ForceFullGc();
		return ledger.ToResult();
	}

	static ScenarioResult RunCurrentBackgroundImagePath()
	{
		var ledger = new ScenarioLedger("leak: current UIView.UpdateBackgroundImageSourceAsync never disposes image-service results");
		var provider = new TrackingImageSourceServiceProvider(ledger);

		for (var i = 0; i < Cycles; i++)
		{
			using var view = CreateHostView();
			view.UpdateBackgroundImageSourceAsync(new TrackingImageSource(i), provider)
				.GetAwaiter()
				.GetResult();
			view.RemoveBackgroundLayer();
		}

		ForceFullGc();
		return ledger.ToResult();
	}

	static async Task UpdateBackgroundImageSourceAndDisposeResultAsync(
		UIView platformView,
		IImageSource imageSource,
		IImageSourceServiceProvider provider)
	{
		platformView.RemoveBackgroundLayer();

		var service = provider.GetRequiredImageSourceService(imageSource);
		var result = await service.GetImageAsync(imageSource, scale: 1);
		try
		{
			var backgroundImage = result?.Value;
			if (backgroundImage is null)
				return;

			var cgImage = backgroundImage.CGImage;
			if (cgImage is null)
				return;

			var imageLayer = new CALayer
			{
				Name = "BackgroundLayer",
				Contents = cgImage,
				Frame = platformView.Bounds,
				ContentsGravity = CoreAnimation.CALayer.GravityResizeAspectFill,
				MasksToBounds = true
			};

			platformView.Layer.InsertSublayer(imageLayer, 0);
		}
		finally
		{
			result?.Dispose();
		}
	}

	static UIView CreateHostView()
	{
		return new UIView(new CGRect(0, 0, 120, 80))
		{
			BackgroundColor = UIColor.White
		};
	}

	static UIImage CreateImage(int cycle)
	{
		var renderer = new UIGraphicsImageRenderer(new CGSize(8, 8));
		return renderer.CreateImage(context =>
		{
			context.CGContext.SetFillColor(UIColor.FromRGB(
				(nfloat)((cycle * 37) % 255) / 255,
				(nfloat)((cycle * 67) % 255) / 255,
				(nfloat)((cycle * 97) % 255) / 255).CGColor);
			context.CGContext.FillRect(new CGRect(0, 0, 8, 8));
		});
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
		}
	}

	sealed class TrackingImageSource : IImageSource
	{
		public TrackingImageSource(int cycle)
		{
			Cycle = cycle;
		}

		public int Cycle { get; }

		public bool IsEmpty => false;
	}

	sealed class TrackingImageSourceServiceProvider : IImageSourceServiceProvider
	{
		readonly TrackingImageSourceService _service;

		public TrackingImageSourceServiceProvider(ScenarioLedger ledger)
		{
			_service = new TrackingImageSourceService(ledger);
		}

		public IServiceProvider HostServiceProvider => this;

		public object? GetService(Type serviceType) => null;

		public IImageSourceService? GetImageSourceService(Type imageSource) =>
			imageSource == typeof(TrackingImageSource) ? _service : null;
	}

	sealed class TrackingImageSourceService : IImageSourceService<TrackingImageSource>
	{
		readonly ScenarioLedger _ledger;

		public TrackingImageSourceService(ScenarioLedger ledger)
		{
			_ledger = ledger;
		}

		public Task<IImageSourceServiceResult<UIImage>?> GetImageAsync(
			IImageSource imageSource,
			float scale = 1,
			CancellationToken cancellationToken = default)
		{
			var source = (TrackingImageSource)imageSource;
			var payload = new NativeImagePayload(_ledger, source.Cycle, PayloadSizeBytes);
			var image = CreateImage(source.Cycle);
			var result = new ImageSourceServiceResult(image, dispose: () =>
			{
				payload.Dispose();
				image.Dispose();
			});

			return Task.FromResult<IImageSourceServiceResult<UIImage>?>(result);
		}
	}

	sealed class NativeImagePayload : IDisposable
	{
		readonly ScenarioLedger _ledger;
		readonly long _bytes;
		IntPtr _buffer;
		bool _disposed;

		public NativeImagePayload(ScenarioLedger ledger, int cycle, long bytes)
		{
			_ledger = ledger;
			_bytes = bytes;
			_buffer = Marshal.AllocHGlobal(checked((nint)bytes));

			unsafe
			{
				var span = new Span<byte>((void*)_buffer, checked((int)bytes));
				for (var i = 0; i < span.Length; i += 4096)
					span[i] = (byte)(cycle + i);
			}

			_ledger.RecordAllocated(bytes);
		}

		public void Dispose()
		{
			if (_disposed)
				return;

			_disposed = true;
			Marshal.FreeHGlobal(_buffer);
			_buffer = IntPtr.Zero;
			_ledger.RecordDisposed(_bytes);
		}
	}

	sealed class ScenarioLedger
	{
		readonly string _name;
		long _allocatedBytes;
		long _disposedBytes;
		int _allocatedCount;
		int _disposedCount;

		public ScenarioLedger(string name)
		{
			_name = name;
		}

		public void RecordAllocated(long bytes)
		{
			_allocatedCount++;
			_allocatedBytes += bytes;
		}

		public void RecordDisposed(long bytes)
		{
			_disposedCount++;
			_disposedBytes += bytes;
		}

		public ScenarioResult ToResult()
		{
			return new ScenarioResult(
				_name,
				_allocatedCount,
				_disposedCount,
				_allocatedBytes,
				_disposedBytes);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int AllocatedCount,
		int DisposedCount,
		long AllocatedBytes,
		long DisposedBytes)
	{
		public long LeakedBytes => AllocatedBytes - DisposedBytes;
	}

	internal sealed record ReproReport(
		int Cycles,
		int PayloadMegabytesPerBackground,
		long BaselineManagedBytes,
		long FinalManagedBytes,
		ScenarioResult Control,
		ScenarioResult Leak)
	{
		public bool LeakProved =>
			Control.AllocatedCount == Cycles &&
			Control.DisposedCount == Cycles &&
			Control.LeakedBytes == 0 &&
			Leak.AllocatedCount == Cycles &&
			Leak.DisposedCount == 0 &&
			Leak.LeakedBytes == Cycles * PayloadMegabytesPerBackground * 1024L * 1024L;

		public string ToText()
		{
			return string.Join(Environment.NewLine,
				"BackgroundImageSourceResultDisposeLeakRepro",
				$"Cycles: {Cycles}",
				$"Native-like payload per background image result: {PayloadMegabytesPerBackground} MiB",
				"Leak shape: direct background image helpers await image-service results but never dispose them",
				$"Leak proved: {LeakProved}",
				string.Empty,
				FormatScenario(Control),
				string.Empty,
				FormatScenario(Leak),
				string.Empty,
				$"Managed heap baseline: {FormatBytes(BaselineManagedBytes)}",
				$"Managed heap final: {FormatBytes(FinalManagedBytes)}",
				$"Managed heap delta: {FormatBytes(FinalManagedBytes - BaselineManagedBytes)}");
		}

		static string FormatScenario(ScenarioResult result)
		{
			return string.Join(Environment.NewLine,
				$"Run: {result.Name}",
				$"  image-service results allocated/disposed: {result.AllocatedCount}/{result.DisposedCount}",
				$"  native-like bytes allocated: {FormatBytes(result.AllocatedBytes)}",
				$"  native-like bytes disposed: {FormatBytes(result.DisposedBytes)}",
				$"  native-like bytes leaked: {FormatBytes(result.LeakedBytes)}");
		}

		static string FormatBytes(long bytes)
		{
			var sign = bytes < 0 ? "-" : string.Empty;
			var value = Math.Abs(bytes);

			if (value >= 1024L * 1024L * 1024L)
				return $"{sign}{value / 1024d / 1024d / 1024d:0.0} GiB";

			if (value >= 1024L * 1024L)
				return $"{sign}{value / 1024d / 1024d:0.0} MiB";

			if (value >= 1024L)
				return $"{sign}{value / 1024d:0.0} KiB";

			return $"{sign}{value} B";
		}
	}
}
