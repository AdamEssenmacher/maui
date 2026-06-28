#nullable enable

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using CoreGraphics;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.iOS;
using ObjCRuntime;
using UIKit;
using CompatibilityImageButtonRenderer = Microsoft.Maui.Controls.Compatibility.Platform.iOS.ImageButtonRenderer;
using CompatibilityImageRenderer = Microsoft.Maui.Controls.Compatibility.Platform.iOS.ImageRenderer;
using MauiImage = Microsoft.Maui.Controls.Image;
using MauiImageButton = Microsoft.Maui.Controls.ImageButton;

namespace IosCompatImageRendererNativeImageRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 80;
	internal const int SourceImagePixels = 512;

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");
	static readonly List<IReadOnlyList<RetainedPeer>> RetainedNativePeers = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-compat-image-renderer-native-image-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext mauiContext)
	{
		RegisterTrackingImageSourceHandler();

		WriteProgress("Starting iOS compatibility image renderer native image retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear native image slots before renderer disposal",
			mauiContext,
			clearNativeImageBeforeDispose: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: renderer disposal leaves native image slots assigned",
			mauiContext,
			clearNativeImageBeforeDispose: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativePeers);

		return new ReproReport(Cycles, SourceImagePixels, baselineBytes, finalBytes, control, current);
	}

	static void RegisterTrackingImageSourceHandler()
	{
		Microsoft.Maui.Controls.Internals.Registrar.Registered.Register(
			typeof(TrackingImageSource),
			typeof(TrackingImageSourceHandler));
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext mauiContext,
		bool clearNativeImageBeforeDispose)
	{
		var ledger = new ScenarioLedger();
		var retainedPeers = new List<RetainedPeer>(Cycles * 2);
		var tracked = new List<TrackedCycle>(Cycles * 2);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 10 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			await CreateImageCycleAsync(i, mauiContext, ledger, retainedPeers, tracked, clearNativeImageBeforeDispose);
			await CreateImageButtonCycleAsync(i, mauiContext, ledger, retainedPeers, tracked, clearNativeImageBeforeDispose);
		}

		RetainedNativePeers.Add(retainedPeers);
		ForceFullGc();

		return ScenarioResult.From(name, ledger, retainedPeers, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static async Task CreateImageCycleAsync(
		int cycle,
		IMauiContext mauiContext,
		ScenarioLedger ledger,
		List<RetainedPeer> retainedPeers,
		List<TrackedCycle> tracked,
		bool clearNativeImageBeforeDispose)
	{
		using var pool = new NSAutoreleasePool();

		var source = new TrackingImageSource(ledger, "Image", cycle);
		var image = new MauiImage
		{
			AutomationId = $"compat-image-renderer-{cycle:000}",
			Source = source,
			WidthRequest = 320,
			HeightRequest = 180
		};
		image.Layout(new Rect(0, 0, 320, 180));

		var contextHandler = new ContextOnlyHandler(mauiContext);
		contextHandler.SetVirtualView(image);

		var renderer = new CompatibilityImageRenderer();
		SetRealisticBounds(renderer, 320, 180);
		renderer.SetElement(image);
		await WaitForAssignedImageAsync(() => renderer.Control?.Image, "ImageRenderer");

		var nativePeer = renderer.Control
			?? throw new InvalidOperationException("ImageRenderer did not create a native UIImageView.");
		var retainedPeer = RetainNativePeer("Image", nativePeer);

		if (clearNativeImageBeforeDispose)
			nativePeer.Image = null;

		renderer.Dispose();
		image.Source = null;
		image.BindingContext = null;
		contextHandler.DisconnectHandler();

		retainedPeers.Add(retainedPeer);
		tracked.Add(TrackedCycle.Create("Image", cycle, renderer, image, source));
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static async Task CreateImageButtonCycleAsync(
		int cycle,
		IMauiContext mauiContext,
		ScenarioLedger ledger,
		List<RetainedPeer> retainedPeers,
		List<TrackedCycle> tracked,
		bool clearNativeImageBeforeDispose)
	{
		using var pool = new NSAutoreleasePool();

		var source = new TrackingImageSource(ledger, "ImageButton", cycle);
		var imageButton = new MauiImageButton
		{
			AutomationId = $"compat-imagebutton-renderer-{cycle:000}",
			Source = source,
			WidthRequest = 96,
			HeightRequest = 96
		};
		imageButton.Layout(new Rect(0, 0, 96, 96));

		var contextHandler = new ContextOnlyHandler(mauiContext);
		contextHandler.SetVirtualView(imageButton);

		var renderer = new CompatibilityImageButtonRenderer();
		SetRealisticBounds(renderer, 96, 96);
		renderer.SetElement(imageButton);
		await WaitForAssignedImageAsync(() => renderer.Control?.ImageForState(UIControlState.Normal), "ImageButtonRenderer");

		var nativePeer = renderer.Control
			?? throw new InvalidOperationException("ImageButtonRenderer did not create a native UIButton.");
		var retainedPeer = RetainNativePeer("ImageButton", nativePeer);

		if (clearNativeImageBeforeDispose)
			nativePeer.SetImage(null, UIControlState.Normal);

		renderer.Dispose();
		imageButton.Source = null;
		imageButton.BindingContext = null;
		contextHandler.DisconnectHandler();

		retainedPeers.Add(retainedPeer);
		tracked.Add(TrackedCycle.Create("ImageButton", cycle, renderer, imageButton, source));
	}

	static void SetRealisticBounds(UIView renderer, int width, int height)
	{
		var bounds = new CGRect(0, 0, width, height);
		renderer.Frame = bounds;
		renderer.Bounds = bounds;
	}

	static async Task WaitForAssignedImageAsync(Func<UIImage?> getImage, string rendererName)
	{
		for (var i = 0; i < 80; i++)
		{
			using var pool = new NSAutoreleasePool();
			if (getImage() is not null)
				return;

			await Task.Delay(25);
		}

		throw new InvalidOperationException($"{rendererName} did not assign a native UIImage.");
	}

	static RetainedPeer RetainNativePeer(string controlType, NSObject peer)
	{
		var handle = peer.Handle;
		if (handle == IntPtr.Zero)
			throw new InvalidOperationException($"Cannot retain a native {controlType} peer with a zero handle.");

		var retained = IntPtr_objc_msgSend(handle, RetainSelector);
		if (retained == IntPtr.Zero)
			throw new InvalidOperationException("Objective-C retain returned a zero handle.");

		return new RetainedPeer(controlType, retained);
	}

	static UIImage? GetAssignedImage(RetainedPeer retainedPeer)
	{
		var peer = retainedPeer.TryGetPeer();
		return peer switch
		{
			UIImageView imageView => imageView.Image,
			UIButton button => button.ImageForState(UIControlState.Normal),
			_ => null
		};
	}

	static long EstimateImageBytes(UIImage image)
	{
		var width = Math.Max(1, image.CGImage?.Width ?? (int)Math.Ceiling(image.Size.Width * image.CurrentScale));
		var height = Math.Max(1, image.CGImage?.Height ?? (int)Math.Ceiling(image.Size.Height * image.CurrentScale));
		return width * (long)height * 4;
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

	static void WriteProgress(string message)
	{
		try
		{
			File.WriteAllText(ResultsPath, message + Environment.NewLine);
		}
		catch
		{
			// Progress output is diagnostic only; the final report write remains authoritative.
		}
	}

	[DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
	static extern IntPtr IntPtr_objc_msgSend(IntPtr receiver, IntPtr selector);

	internal sealed record TrackedCycle(
		string ControlType,
		int Cycle,
		WeakReference<object> Renderer,
		WeakReference<object> VirtualView,
		WeakReference<TrackingImageSource> Source)
	{
		public static TrackedCycle Create(
			string controlType,
			int cycle,
			object renderer,
			object virtualView,
			TrackingImageSource source)
		{
			return new TrackedCycle(
				controlType,
				cycle,
				new WeakReference<object>(renderer),
				new WeakReference<object>(virtualView),
				new WeakReference<TrackingImageSource>(source));
		}
	}

	internal sealed class RetainedPeer
	{
		public RetainedPeer(string controlType, IntPtr handle)
		{
			ControlType = controlType;
			Handle = handle;
		}

		public string ControlType { get; }

		public IntPtr Handle { get; }

		public NSObject? TryGetPeer()
		{
			if (Handle == IntPtr.Zero)
				return null;

			try
			{
				return ControlType == "Image"
					? Runtime.GetNSObject<UIImageView>(Handle, false)
					: Runtime.GetNSObject<UIButton>(Handle, false);
			}
			catch
			{
				return null;
			}
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int ImagesCreated,
		int RetainedNativePeers,
		int NativePeersWithAssignedImages,
		long EstimatedAssignedImageBytes,
		int AliveRenderers,
		int AliveVirtualViews,
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

				if (GetAssignedImage(retainedPeer) is UIImage image)
				{
					var bytes = EstimateImageBytes(image);
					nativePeersWithAssignedImages++;
					estimatedAssignedImageBytes += bytes;
					counter.NativePeersWithAssignedImages++;
					counter.EstimatedAssignedImageBytes += bytes;
				}
			}

			var aliveRenderers = 0;
			var aliveVirtualViews = 0;
			var aliveSources = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.Renderer.TryGetTarget(out _))
					aliveRenderers++;

				if (cycle.VirtualView.TryGetTarget(out _))
					aliveVirtualViews++;

				if (cycle.Source.TryGetTarget(out _))
					aliveSources++;
			}

			foreach (var pair in ledger.ByControlType)
			{
				var counter = GetCounter(byType, pair.Key);
				counter.ImagesCreated = pair.Value;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				ledger.ImagesCreated,
				retainedPeers.Count,
				nativePeersWithAssignedImages,
				estimatedAssignedImageBytes,
				aliveRenderers,
				aliveVirtualViews,
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
		int ImagesCreated,
		int RetainedNativePeers,
		int NativePeersWithAssignedImages,
		long EstimatedAssignedImageBytes);

	sealed class TypeCounter
	{
		public int Tracked { get; set; }
		public int ImagesCreated { get; set; }
		public int RetainedNativePeers { get; set; }
		public int NativePeersWithAssignedImages { get; set; }
		public long EstimatedAssignedImageBytes { get; set; }

		public TypeResult ToResult() =>
			new(Tracked, ImagesCreated, RetainedNativePeers, NativePeersWithAssignedImages, EstimatedAssignedImageBytes);
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
	int TotalCycles => Cycles * 2;

	public bool LeakProved =>
		Control.ImagesCreated == TotalCycles &&
		Control.RetainedNativePeers == TotalCycles &&
		Control.NativePeersWithAssignedImages == 0 &&
		Control.AliveVirtualViews == 0 &&
		Control.AliveSources == 0 &&
		Current.ImagesCreated == TotalCycles &&
		Current.RetainedNativePeers == TotalCycles &&
		Current.NativePeersWithAssignedImages == TotalCycles &&
		Current.EstimatedAssignedImageBytes > 0 &&
		Current.AliveVirtualViews == 0 &&
		Current.AliveSources == 0 &&
		Current.ByControlType.TryGetValue("Image", out var image) &&
		image.NativePeersWithAssignedImages == Cycles &&
		Current.ByControlType.TryGetValue("ImageButton", out var imageButton) &&
		imageButton.NativePeersWithAssignedImages == Cycles;

	public string ToText()
	{
		var retainedMiB = Current.EstimatedAssignedImageBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedAssignedImageBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosCompatImageRendererNativeImageRetentionRepro",
			$"Cycles per renderer type: {Cycles}",
			$"Total renderer cycles per scenario: {TotalCycles}",
			$"Generated source image size: {SourceImagePixels} x {SourceImagePixels} pixels",
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
			$"  generated UIImages: {result.ImagesCreated}",
			$"  retained native peers: {result.RetainedNativePeers}/{result.TrackedCycles}",
			$"  native peers with assigned UIImages: {result.NativePeersWithAssignedImages}/{result.TrackedCycles}",
			$"  estimated assigned native image bytes: {result.EstimatedAssignedImageBytes:N0}",
			$"  estimated assigned native image MiB: {nativeImageMiB:N1}",
			$"  alive renderers: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  alive virtual views: {result.AliveVirtualViews}/{result.TrackedCycles}",
			$"  alive image sources: {result.AliveSources}/{result.TrackedCycles}"
		};

		foreach (var pair in result.ByControlType.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
		{
			var value = pair.Value;
			lines.Add(
				$"  {pair.Key}: native={value.RetainedNativePeers}/{value.Tracked}, assignedImage={value.NativePeersWithAssignedImages}/{value.Tracked}, generated={value.ImagesCreated}, estimatedBytes={value.EstimatedAssignedImageBytes:N0}");
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

internal sealed class TrackingImageSourceHandler : IImageSourceHandler
{
	public Task<UIImage> LoadImageAsync(
		ImageSource imagesource,
		CancellationToken cancelationToken = default,
		float scale = 1)
	{
		if (imagesource is not TrackingImageSource source)
			return Task.FromResult<UIImage>(null!);

		source.Ledger.RecordCreated(source.ControlType);
		return Task.FromResult(CreateImage(source.Cycle, source.ControlType));
	}

	static UIImage CreateImage(int cycle, string controlType)
	{
		var format = new UIGraphicsImageRendererFormat
		{
			Opaque = true,
			Scale = 1
		};
		var renderer = new UIGraphicsImageRenderer(
			new CGSize(ReproSession.SourceImagePixels, ReproSession.SourceImagePixels),
			format);

		return renderer.CreateImage(context =>
		{
			var salt = controlType switch
			{
				"Image" => 17,
				"ImageButton" => 43,
				_ => 43
			};
			UIColor.FromRGB(
				(nfloat)(((cycle + salt) * 37) % 255) / 255f,
				(nfloat)(((cycle + salt) * 67) % 255) / 255f,
				(nfloat)(((cycle + salt) * 97) % 255) / 255f).SetFill();
			context.FillRect(new CGRect(0, 0, ReproSession.SourceImagePixels, ReproSession.SourceImagePixels));

			UIColor.FromRGBA(1, 1, 1, 0.35f).SetFill();
			context.FillRect(new CGRect(
				(cycle * 13) % ReproSession.SourceImagePixels,
				(cycle * 29) % ReproSession.SourceImagePixels,
				ReproSession.SourceImagePixels / 3,
				ReproSession.SourceImagePixels / 3));
		});
	}
}

internal sealed class ScenarioLedger
{
	readonly Dictionary<string, int> _byControlType = new(StringComparer.Ordinal);

	public int ImagesCreated { get; private set; }

	public IReadOnlyDictionary<string, int> ByControlType => _byControlType;

	public void RecordCreated(string controlType)
	{
		ImagesCreated++;
		_byControlType[controlType] = _byControlType.TryGetValue(controlType, out var count) ? count + 1 : 1;
	}
}

internal sealed class ContextOnlyHandler : IPlatformViewHandler
{
	readonly UIView _platformView = new();

	public ContextOnlyHandler(IMauiContext mauiContext)
	{
		MauiContext = mauiContext;
	}

	public object? PlatformView => _platformView;

	public bool HasContainer { get; set; }

	public object? ContainerView => null;

	UIView? IPlatformViewHandler.PlatformView => _platformView;

	UIView? IPlatformViewHandler.ContainerView => null;

	public UIViewController? ViewController => null;

	public IElement? VirtualView { get; private set; }

	IView? IViewHandler.VirtualView => VirtualView as IView;

	public IMauiContext? MauiContext { get; private set; }

	public void SetMauiContext(IMauiContext mauiContext) => MauiContext = mauiContext;

	public void SetVirtualView(IElement view)
	{
		VirtualView = view;
		if (view.Handler != this)
			view.Handler = this;
	}

	public void UpdateValue(string property)
	{
	}

	public void Invoke(string command, object? args = null)
	{
	}

	public void DisconnectHandler()
	{
		if (VirtualView?.Handler == this)
			VirtualView.Handler = null;

		VirtualView = null;
	}

	public Size GetDesiredSize(double widthConstraint, double heightConstraint) => Size.Zero;

	public void PlatformArrange(Rect frame)
	{
	}
}
