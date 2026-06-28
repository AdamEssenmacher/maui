#nullable enable

using System.Threading;
using CoreGraphics;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using UIKit;
using MauiButton = Microsoft.Maui.Controls.Button;
using MauiImage = Microsoft.Maui.Controls.Image;
using MauiImageButton = Microsoft.Maui.Controls.ImageButton;

namespace IosImageHandlerNativeImageRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int SourceImagePixels = 512;

	static readonly PropertyMapper<Microsoft.Maui.IImage, IImageHandler> EmptyImageMapper = new();
	static readonly PropertyMapper<IImageButton, IImageButtonHandler> EmptyImageButtonMapper = new();
	static readonly PropertyMapper<IButton, IButtonHandler> EmptyButtonMapper = new();
	static readonly List<RetainedPeer> RetainedNativePeers = new();

	public static readonly string ResultsPath =
		Path.Combine(Path.GetTempPath(), "ios-imagehandler-native-image-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			"control: clear native image slots and reset loaders before disconnect",
			context,
			clearNativeImageAndResetLoader: true);

		var current = await RunScenarioAsync(
			"current: MAUI disconnect leaves native image slots assigned",
			context,
			clearNativeImageAndResetLoader: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativePeers);

		return new ReproReport(
			Cycles,
			SourceImagePixels,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearNativeImageAndResetLoader)
	{
		var ledger = new ScenarioLedger(name);
		var retainedPeers = new List<RetainedPeer>(Cycles * 3);
		var tracked = new List<TrackedCycle>(Cycles * 3);

		for (var i = 0; i < Cycles; i++)
		{
			await CreateImageCycleAsync(context, ledger, i, retainedPeers, tracked, clearNativeImageAndResetLoader);
			await CreateImageButtonCycleAsync(context, ledger, i, retainedPeers, tracked, clearNativeImageAndResetLoader);
			await CreateButtonCycleAsync(context, ledger, i, retainedPeers, tracked, clearNativeImageAndResetLoader);
		}

		RetainedNativePeers.AddRange(retainedPeers);
		ForceFullGc();

		return ScenarioResult.From(name, ledger, retainedPeers, tracked);
	}

	static async Task CreateImageCycleAsync(
		IMauiContext context,
		ScenarioLedger ledger,
		int cycle,
		List<RetainedPeer> retainedPeers,
		List<TrackedCycle> tracked,
		bool clearNativeImageAndResetLoader)
	{
		using var pool = new NSAutoreleasePool();

		var source = new TrackingImageSource(ledger, "Image", cycle);
		var image = new MauiImage
		{
			Source = source,
			WidthRequest = 320,
			HeightRequest = 180
		};
		var handler = new ImageHandler(EmptyImageMapper);

		AttachHandler(image, handler, context);
		await ImageHandler.MapSourceAsync(handler, image);

		var platformView = handler.PlatformView;
		if (platformView.Image is null)
			throw new InvalidOperationException("Image did not assign a native UIImage.");

		if (clearNativeImageAndResetLoader)
		{
			platformView.Image = null;
			platformView.AnimationImages = null;
			handler.SourceLoader.Reset();
		}

		((IElementHandler)handler).DisconnectHandler();
		image.Source = null;
		image.Handler = null;

		retainedPeers.Add(new RetainedPeer("Image", platformView));
		tracked.Add(TrackedCycle.Create("Image", cycle, platformView, image, handler, source));
	}

	static async Task CreateImageButtonCycleAsync(
		IMauiContext context,
		ScenarioLedger ledger,
		int cycle,
		List<RetainedPeer> retainedPeers,
		List<TrackedCycle> tracked,
		bool clearNativeImageAndResetLoader)
	{
		using var pool = new NSAutoreleasePool();

		var source = new TrackingImageSource(ledger, "ImageButton", cycle);
		var imageButton = new MauiImageButton
		{
			Source = source,
			WidthRequest = 96,
			HeightRequest = 96
		};
		var handler = new ImageButtonHandler(EmptyImageButtonMapper);

		AttachHandler(imageButton, handler, context);
		await ImageHandler.MapSourceAsync(handler, imageButton);

		var platformView = handler.PlatformView;
		if (platformView.ImageForState(UIControlState.Normal) is null)
			throw new InvalidOperationException("ImageButton did not assign a native UIImage.");

		if (clearNativeImageAndResetLoader)
		{
			platformView.SetImage(null, UIControlState.Normal);
			handler.SourceLoader.Reset();
		}

		((IElementHandler)handler).DisconnectHandler();
		imageButton.Source = null;
		imageButton.Handler = null;

		retainedPeers.Add(new RetainedPeer("ImageButton", platformView));
		tracked.Add(TrackedCycle.Create("ImageButton", cycle, platformView, imageButton, handler, source));
	}

	static async Task CreateButtonCycleAsync(
		IMauiContext context,
		ScenarioLedger ledger,
		int cycle,
		List<RetainedPeer> retainedPeers,
		List<TrackedCycle> tracked,
		bool clearNativeImageAndResetLoader)
	{
		using var pool = new NSAutoreleasePool();

		var source = new TrackingImageSource(ledger, "Button", cycle);
		var button = new MauiButton
		{
			Text = $"Item {cycle:000}",
			ImageSource = source,
			WidthRequest = 320,
			HeightRequest = 56
		};
		var handler = new ButtonHandler(EmptyButtonMapper);

		AttachHandler(button, handler, context);
		await ButtonHandler.MapImageSourceAsync(handler, button);

		var platformView = handler.PlatformView;
		if (platformView.ImageForState(UIControlState.Normal) is null)
			throw new InvalidOperationException("Button did not assign a native UIImage.");

		if (clearNativeImageAndResetLoader)
		{
			platformView.SetImage(null, UIControlState.Normal);
			handler.ImageSourceLoader.Reset();
		}

		((IElementHandler)handler).DisconnectHandler();
		button.ImageSource = null;
		button.Handler = null;

		retainedPeers.Add(new RetainedPeer("Button", platformView));
		tracked.Add(TrackedCycle.Create("Button", cycle, platformView, button, handler, source));
	}

	static void AttachHandler(IElement view, IElementHandler handler, IMauiContext context)
	{
		handler.SetMauiContext(context);
		view.Handler = handler;
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			using var pool = new NSAutoreleasePool();
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(100);
		}
	}

	static UIImage? GetAssignedImage(NSObject peer) =>
		peer switch
		{
			UIImageView imageView => imageView.Image,
			UIButton button => button.ImageForState(UIControlState.Normal),
			_ => null
		};

	static long EstimateImageBytes(UIImage image)
	{
		var width = Math.Max(1, image.CGImage?.Width ?? (int)Math.Ceiling(image.Size.Width * image.CurrentScale));
		var height = Math.Max(1, image.CGImage?.Height ?? (int)Math.Ceiling(image.Size.Height * image.CurrentScale));
		return width * (long)height * 4;
	}

	internal sealed record RetainedPeer(string ControlType, NSObject Peer);

	internal sealed record TrackedCycle(
		string ControlType,
		int Cycle,
		WeakReference<NSObject> NativePeer,
		WeakReference<object> VirtualView,
		WeakReference<IElementHandler> Handler,
		WeakReference<TrackingImageSource> Source)
	{
		public static TrackedCycle Create(
			string controlType,
			int cycle,
			NSObject platformView,
			object virtualView,
			IElementHandler handler,
			TrackingImageSource source)
		{
			return new TrackedCycle(
				controlType,
				cycle,
				new WeakReference<NSObject>(platformView),
				new WeakReference<object>(virtualView),
				new WeakReference<IElementHandler>(handler),
				new WeakReference<TrackingImageSource>(source));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int ServiceResultsCreated,
		int ServiceResultsDisposed,
		int RetainedNativePeers,
		int NativePeersWithAssignedImages,
		long EstimatedAssignedImageBytes,
		int AliveNativePeers,
		int AliveVirtualViews,
		int AliveHandlers,
		int AliveSources,
		IReadOnlyDictionary<string, TypeResult> ByControlType)
	{
		internal static ScenarioResult From(
			string name,
			ScenarioLedger ledger,
			IReadOnlyList<RetainedPeer> retainedPeers,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var nativePeersWithAssignedImages = 0;
			long estimatedAssignedImageBytes = 0;
			var byType = new Dictionary<string, TypeCounter>(StringComparer.Ordinal);

			foreach (var retainedPeer in retainedPeers)
			{
				var counter = GetCounter(byType, retainedPeer.ControlType);
				counter.Tracked++;
				counter.RetainedNativePeers++;

				if (GetAssignedImage(retainedPeer.Peer) is UIImage image)
				{
					var bytes = EstimateImageBytes(image);
					nativePeersWithAssignedImages++;
					estimatedAssignedImageBytes += bytes;
					counter.NativePeersWithAssignedImages++;
					counter.EstimatedAssignedImageBytes += bytes;
				}
			}

			var aliveNativePeers = 0;
			var aliveVirtualViews = 0;
			var aliveHandlers = 0;
			var aliveSources = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativePeer.TryGetTarget(out _))
					aliveNativePeers++;

				if (cycle.VirtualView.TryGetTarget(out _))
					aliveVirtualViews++;

				if (cycle.Handler.TryGetTarget(out _))
					aliveHandlers++;

				if (cycle.Source.TryGetTarget(out _))
					aliveSources++;
			}

			foreach (var pair in ledger.ByControlType)
			{
				var counter = GetCounter(byType, pair.Key);
				counter.ServiceResultsCreated = pair.Value.Created;
				counter.ServiceResultsDisposed = pair.Value.Disposed;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				ledger.ResultsCreated,
				ledger.ResultsDisposed,
				retainedPeers.Count,
				nativePeersWithAssignedImages,
				estimatedAssignedImageBytes,
				aliveNativePeers,
				aliveVirtualViews,
				aliveHandlers,
				aliveSources,
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
		int ServiceResultsCreated,
		int ServiceResultsDisposed,
		int RetainedNativePeers,
		int NativePeersWithAssignedImages,
		long EstimatedAssignedImageBytes);

	sealed class TypeCounter
	{
		public int Tracked { get; set; }
		public int ServiceResultsCreated { get; set; }
		public int ServiceResultsDisposed { get; set; }
		public int RetainedNativePeers { get; set; }
		public int NativePeersWithAssignedImages { get; set; }
		public long EstimatedAssignedImageBytes { get; set; }

		public TypeResult ToResult() =>
			new(Tracked, ServiceResultsCreated, ServiceResultsDisposed, RetainedNativePeers, NativePeersWithAssignedImages, EstimatedAssignedImageBytes);
	}
}

internal sealed record ReproReport(
	int Cycles,
	int SourceImagePixels,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	int TotalCycles => Cycles * 3;

	public bool LeakProved =>
		Control.ServiceResultsCreated == TotalCycles &&
		Control.ServiceResultsDisposed == TotalCycles &&
		Control.NativePeersWithAssignedImages == 0 &&
		Current.ServiceResultsCreated == TotalCycles &&
		Current.ServiceResultsDisposed == Cycles * 2 &&
		Current.RetainedNativePeers == TotalCycles &&
		Current.NativePeersWithAssignedImages == TotalCycles &&
		Current.AliveVirtualViews == 0 &&
		Current.AliveHandlers == 0 &&
		Current.AliveSources == 0 &&
		Current.ByControlType.TryGetValue("Image", out var image) &&
		image.NativePeersWithAssignedImages == Cycles &&
		image.ServiceResultsDisposed == Cycles &&
		Current.ByControlType.TryGetValue("ImageButton", out var imageButton) &&
		imageButton.NativePeersWithAssignedImages == Cycles &&
		imageButton.ServiceResultsDisposed == Cycles &&
		Current.ByControlType.TryGetValue("Button", out var button) &&
		button.NativePeersWithAssignedImages == Cycles &&
		button.ServiceResultsDisposed == 0;

	public string ToText()
	{
		var retainedMiB = Current.EstimatedAssignedImageBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedAssignedImageBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosImageHandlerNativeImageRetentionRepro",
			$"Cycles per control type: {Cycles}",
			$"Total handler cycles per scenario: {TotalCycles}",
			$"Source image size: {SourceImagePixels} x {SourceImagePixels} pixels",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated assigned native image payload: {controlMiB:N1} MiB",
			$"Current estimated assigned native image payload: {retainedMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var nativeImageMiB = result.EstimatedAssignedImageBytes / 1024d / 1024d;
		var lines = new List<string>
		{
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  service results created/disposed: {result.ServiceResultsCreated}/{result.ServiceResultsDisposed}",
			$"  retained native peers: {result.RetainedNativePeers}/{result.TrackedCycles}",
			$"  native peers with assigned UIImages: {result.NativePeersWithAssignedImages}/{result.TrackedCycles}",
			$"  estimated assigned native image bytes: {result.EstimatedAssignedImageBytes:N0}",
			$"  estimated assigned native image MiB: {nativeImageMiB:N1}",
			$"  alive native peers: {result.AliveNativePeers}/{result.TrackedCycles}",
			$"  alive virtual views: {result.AliveVirtualViews}/{result.TrackedCycles}",
			$"  alive handlers: {result.AliveHandlers}/{result.TrackedCycles}",
			$"  alive image sources: {result.AliveSources}/{result.TrackedCycles}"
		};

		foreach (var pair in result.ByControlType.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
		{
			var value = pair.Value;
			lines.Add(
				$"  {pair.Key}: native={value.RetainedNativePeers}/{value.Tracked}, assignedImage={value.NativePeersWithAssignedImages}/{value.Tracked}, serviceResults={value.ServiceResultsCreated}/{value.ServiceResultsDisposed}, estimatedBytes={value.EstimatedAssignedImageBytes:N0}");
		}

		return string.Join(Environment.NewLine, lines);
	}
}

internal sealed class TrackingImageSource : ImageSource
{
	public TrackingImageSource(ScenarioLedger ledger, string controlType, int cycle)
	{
		Ledger = ledger;
		ControlType = controlType;
		Cycle = cycle;
	}

	public ScenarioLedger Ledger { get; }

	public string ControlType { get; }

	public int Cycle { get; }

	public override bool IsEmpty => false;
}

internal sealed class TrackingImageSourceService : ImageSourceService, IImageSourceService<TrackingImageSource>
{
	public override Task<IImageSourceServiceResult<UIImage>?> GetImageAsync(
		IImageSource imageSource,
		float scale = 1,
		CancellationToken cancellationToken = default)
	{
		if (imageSource is not TrackingImageSource trackingSource)
			return Task.FromResult<IImageSourceServiceResult<UIImage>?>(null);

		var image = CreateImage(trackingSource.Cycle);
		trackingSource.Ledger.RecordCreated(trackingSource.ControlType);

		var result = new ImageSourceServiceResult(
			image,
			dispose: () => trackingSource.Ledger.RecordDisposed(trackingSource.ControlType));

		return Task.FromResult<IImageSourceServiceResult<UIImage>?>(result);
	}

	static UIImage CreateImage(int cycle)
	{
		var format = new UIGraphicsImageRendererFormat
		{
			Opaque = true,
			Scale = 1
		};
		var renderer = new UIGraphicsImageRenderer(new CGSize(SourceImagePixelsForRender, SourceImagePixelsForRender), format);

		return renderer.CreateImage(context =>
		{
			UIColor.FromRGB(
				(nfloat)((cycle * 37) % 255) / 255f,
				(nfloat)((cycle * 67) % 255) / 255f,
				(nfloat)((cycle * 97) % 255) / 255f).SetFill();
			context.FillRect(new CGRect(0, 0, SourceImagePixelsForRender, SourceImagePixelsForRender));
		});
	}

	const int SourceImagePixelsForRender = 512;
}

internal sealed class ScenarioLedger
{
	readonly Dictionary<string, TypeLedger> _byControlType = new(StringComparer.Ordinal);

	public ScenarioLedger(string name)
	{
		Name = name;
	}

	public string Name { get; }

	public int ResultsCreated { get; private set; }

	public int ResultsDisposed { get; private set; }

	public IReadOnlyDictionary<string, TypeLedger> ByControlType => _byControlType;

	public void RecordCreated(string controlType)
	{
		ResultsCreated++;
		Get(controlType).Created++;
	}

	public void RecordDisposed(string controlType)
	{
		ResultsDisposed++;
		Get(controlType).Disposed++;
	}

	TypeLedger Get(string controlType)
	{
		if (!_byControlType.TryGetValue(controlType, out var ledger))
		{
			ledger = new TypeLedger();
			_byControlType.Add(controlType, ledger);
		}

		return ledger;
	}
}

internal sealed class TypeLedger
{
	public int Created { get; set; }

	public int Disposed { get; set; }
}
