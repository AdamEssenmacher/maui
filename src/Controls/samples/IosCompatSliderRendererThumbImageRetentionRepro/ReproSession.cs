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
using CompatibilitySliderRenderer = Microsoft.Maui.Controls.Compatibility.Platform.iOS.SliderRenderer;
using MauiSlider = Microsoft.Maui.Controls.Slider;

namespace IosCompatSliderRendererThumbImageRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 240;
	internal const int SourceImagePixels = 256;

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");
	static readonly List<IReadOnlyList<RetainedPeer>> RetainedNativePeers = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-compat-slider-renderer-thumb-image-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext mauiContext)
	{
		RegisterTrackingImageSourceHandler();

		WriteProgress("Starting iOS compatibility SliderRenderer thumb image retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear native thumb image before renderer disposal",
			mauiContext,
			clearNativeThumbBeforeDispose: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: renderer disposal leaves native thumb image assigned",
			mauiContext,
			clearNativeThumbBeforeDispose: false);

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
		bool clearNativeThumbBeforeDispose)
	{
		var ledger = new ScenarioLedger();
		var retainedPeers = new List<RetainedPeer>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 20 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			await CreateSliderCycleAsync(i, mauiContext, ledger, retainedPeers, tracked, clearNativeThumbBeforeDispose);
		}

		RetainedNativePeers.Add(retainedPeers);
		ForceFullGc();

		return ScenarioResult.From(name, ledger, retainedPeers, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static async Task CreateSliderCycleAsync(
		int cycle,
		IMauiContext mauiContext,
		ScenarioLedger ledger,
		List<RetainedPeer> retainedPeers,
		List<TrackedCycle> tracked,
		bool clearNativeThumbBeforeDispose)
	{
		using var pool = new NSAutoreleasePool();

		var source = new TrackingImageSource(ledger, cycle);
		var slider = new MauiSlider
		{
			AutomationId = $"compat-slider-renderer-{cycle:000}",
			Minimum = 0,
			Maximum = 100,
			Value = cycle % 100,
			ThumbImageSource = source,
			WidthRequest = 320,
			HeightRequest = 44
		};
		slider.Layout(new Rect(0, 0, 320, 44));

		var contextHandler = new ContextOnlyHandler(mauiContext);
		contextHandler.SetVirtualView(slider);

		var renderer = new CompatibilitySliderRenderer();
		SetRealisticBounds(renderer, 320, 44);
		renderer.SetElement(slider);
		await WaitForAssignedThumbImageAsync(() => renderer.Control?.ThumbImage(UIControlState.Normal));

		var nativePeer = renderer.Control
			?? throw new InvalidOperationException("SliderRenderer did not create a native UISlider.");
		var retainedPeer = RetainNativePeer(nativePeer);

		if (clearNativeThumbBeforeDispose)
			nativePeer.SetThumbImage(null, UIControlState.Normal);

		renderer.Dispose();
		slider.ThumbImageSource = null;
		slider.BindingContext = null;
		contextHandler.DisconnectHandler();

		retainedPeers.Add(retainedPeer);
		tracked.Add(TrackedCycle.Create(cycle, renderer, slider, source));
	}

	static void SetRealisticBounds(UIView renderer, int width, int height)
	{
		var bounds = new CGRect(0, 0, width, height);
		renderer.Frame = bounds;
		renderer.Bounds = bounds;
	}

	static async Task WaitForAssignedThumbImageAsync(Func<UIImage?> getImage)
	{
		for (var i = 0; i < 80; i++)
		{
			using var pool = new NSAutoreleasePool();
			if (getImage() is not null)
				return;

			await Task.Delay(25);
		}

		throw new InvalidOperationException("SliderRenderer did not assign a native thumb UIImage.");
	}

	static RetainedPeer RetainNativePeer(NSObject peer)
	{
		var handle = peer.Handle;
		if (handle == IntPtr.Zero)
			throw new InvalidOperationException("Cannot retain a native UISlider peer with a zero handle.");

		var retained = IntPtr_objc_msgSend(handle, RetainSelector);
		if (retained == IntPtr.Zero)
			throw new InvalidOperationException("Objective-C retain returned a zero handle.");

		return new RetainedPeer(retained);
	}

	static UIImage? GetAssignedThumbImage(RetainedPeer retainedPeer)
	{
		return retainedPeer.TryGetPeer()?.ThumbImage(UIControlState.Normal);
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
		int Cycle,
		WeakReference<object> Renderer,
		WeakReference<object> VirtualView,
		WeakReference<TrackingImageSource> Source)
	{
		public static TrackedCycle Create(
			int cycle,
			object renderer,
			object virtualView,
			TrackingImageSource source)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<object>(renderer),
				new WeakReference<object>(virtualView),
				new WeakReference<TrackingImageSource>(source));
		}
	}

	internal sealed class RetainedPeer
	{
		public RetainedPeer(IntPtr handle)
		{
			Handle = handle;
		}

		public IntPtr Handle { get; }

		public UISlider? TryGetPeer()
		{
			if (Handle == IntPtr.Zero)
				return null;

			try
			{
				return Runtime.GetNSObject<UISlider>(Handle, false);
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
		int NativePeersWithAssignedThumbImages,
		long EstimatedAssignedThumbImageBytes,
		int AliveRenderers,
		int AliveVirtualViews,
		int AliveSources)
	{
		internal static ScenarioResult From(
			string name,
			ScenarioLedger ledger,
			IReadOnlyList<RetainedPeer> retainedPeers,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var nativePeersWithAssignedThumbImages = 0;
			long estimatedAssignedThumbImageBytes = 0;

			foreach (var retainedPeer in retainedPeers)
			{
				if (GetAssignedThumbImage(retainedPeer) is UIImage image)
				{
					nativePeersWithAssignedThumbImages++;
					estimatedAssignedThumbImageBytes += EstimateImageBytes(image);
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

			return new ScenarioResult(
				name,
				tracked.Count,
				ledger.ImagesCreated,
				retainedPeers.Count,
				nativePeersWithAssignedThumbImages,
				estimatedAssignedThumbImageBytes,
				aliveRenderers,
				aliveVirtualViews,
				aliveSources);
		}
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
	public bool LeakProved =>
		Control.ImagesCreated == Cycles &&
		Control.RetainedNativePeers == Cycles &&
		Control.NativePeersWithAssignedThumbImages == 0 &&
		Control.AliveVirtualViews == 0 &&
		Control.AliveSources == 0 &&
		Current.ImagesCreated == Cycles &&
		Current.RetainedNativePeers == Cycles &&
		Current.NativePeersWithAssignedThumbImages == Cycles &&
		Current.EstimatedAssignedThumbImageBytes >= Cycles * SourceImagePixels * SourceImagePixels * 4L &&
		Current.AliveVirtualViews == 0 &&
		Current.AliveSources == 0;

	public string ToText()
	{
		var retainedMiB = Current.EstimatedAssignedThumbImageBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedAssignedThumbImageBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosCompatSliderRendererThumbImageRetentionRepro",
			$"Cycles: {Cycles}",
			$"Generated thumb image size: {SourceImagePixels} x {SourceImagePixels} pixels",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated assigned native thumb image payload: {controlMiB:N1} MiB",
			$"Current estimated assigned native thumb image payload: {retainedMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var nativeImageMiB = result.EstimatedAssignedThumbImageBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  generated UIImages: {result.ImagesCreated}",
			$"  retained native peers: {result.RetainedNativePeers}/{result.TrackedCycles}",
			$"  native peers with assigned thumb UIImages: {result.NativePeersWithAssignedThumbImages}/{result.TrackedCycles}",
			$"  estimated assigned native thumb image bytes: {result.EstimatedAssignedThumbImageBytes:N0}",
			$"  estimated assigned native thumb image MiB: {nativeImageMiB:N1}",
			$"  alive renderers: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  alive virtual views: {result.AliveVirtualViews}/{result.TrackedCycles}",
			$"  alive image sources: {result.AliveSources}/{result.TrackedCycles}");
	}
}

internal sealed class TrackingImageSource : ImageSource
{
	public TrackingImageSource(ScenarioLedger ledger, int cycle)
	{
		Ledger = ledger;
		Cycle = cycle;
	}

	public ScenarioLedger Ledger { get; }

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

		source.Ledger.RecordCreated();
		return Task.FromResult(CreateImage(source.Cycle));
	}

	static UIImage CreateImage(int cycle)
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
			UIColor.FromRGB(
				(nfloat)((cycle * 37) % 255) / 255f,
				(nfloat)((cycle * 67) % 255) / 255f,
				(nfloat)((cycle * 97) % 255) / 255f).SetFill();
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
	public int ImagesCreated { get; private set; }

	public void RecordCreated() => ImagesCreated++;
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
