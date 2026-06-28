#nullable enable

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using CoreGraphics;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.iOS;
using Microsoft.Maui.Graphics;
using ObjCRuntime;
using UIKit;

namespace IosButtonRendererImageRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 80;
	internal const int ButtonWidthPoints = 320;
	internal const int ButtonHeightPoints = 88;
	internal const int SourceImagePixels = 512;
	internal const int PayloadMegabytesPerCycle = 1;

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");
	static readonly List<IReadOnlyList<RetainedButtonPeer>> RetainedNativeButtons = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-buttonrenderer-image-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext mauiContext)
	{
		RegisterTrackingImageSourceHandler();

		WriteProgress("Starting iOS ButtonRenderer image retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear native UIButton image before renderer disposal",
			clearNativeImageBeforeDispose: true,
			mauiContext);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: ButtonRenderer dispose leaves native UIButton image assigned",
			clearNativeImageBeforeDispose: false,
			mauiContext);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeButtons);

		return new ReproReport(
			Cycles,
			ButtonWidthPoints,
			ButtonHeightPoints,
			SourceImagePixels,
			PayloadMegabytesPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static void RegisterTrackingImageSourceHandler()
	{
		Microsoft.Maui.Controls.Internals.Registrar.Registered.Register(
			typeof(TrackingImageSource),
			typeof(TrackingImageSourceHandler));
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		bool clearNativeImageBeforeDispose,
		IMauiContext mauiContext)
	{
		var nativeButtons = new List<RetainedButtonPeer>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);
		var ledger = new ScenarioLedger();

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 10 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			await CreateDisposedRendererCycleAsync(i, nativeButtons, tracked, ledger, clearNativeImageBeforeDispose, mauiContext);
		}

		RetainedNativeButtons.Add(nativeButtons);
		ForceFullGc();

		return ScenarioResult.From(name, ledger, nativeButtons, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static async Task CreateDisposedRendererCycleAsync(
		int cycle,
		List<RetainedButtonPeer> nativeButtons,
		List<TrackedCycle> tracked,
		ScenarioLedger ledger,
		bool clearNativeImageBeforeDispose,
		IMauiContext mauiContext)
	{
		using var pool = new NSAutoreleasePool();

		var payload = new ButtonPayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
		var source = new TrackingImageSource(ledger, cycle);
		var button = new PayloadButton(cycle, payload)
		{
			ImageSource = source
		};
		var contextHandler = new ContextOnlyHandler(mauiContext);
		var renderer = new ButtonRenderer();

		button.Layout(new Rect(0, 0, ButtonWidthPoints, ButtonHeightPoints));
		contextHandler.SetVirtualView(button);
		SetRealisticBounds(renderer);
		renderer.SetElement(button);

		var nativeButton = renderer.Control
			?? throw new InvalidOperationException("ButtonRenderer did not create a native UIButton.");
		SetRealisticButtonBounds(nativeButton);

		await WaitForAssignedImageAsync(nativeButton);
		SetRealisticButtonBounds(nativeButton);

		var retainedNativeButton = RetainNativeButton(nativeButton);

		if (clearNativeImageBeforeDispose)
			nativeButton.SetImage(null, UIControlState.Normal);

		renderer.Dispose();
		button.ImageSource = null;
		button.BindingContext = null;
		((IElementController)button).EffectControlProvider = null;
		contextHandler.DisconnectHandler();

		nativeButtons.Add(retainedNativeButton);
		tracked.Add(TrackedCycle.Create(cycle, renderer, button, payload, source));
	}

	static void SetRealisticBounds(ButtonRenderer renderer)
	{
		var bounds = new CGRect(0, 0, ButtonWidthPoints, ButtonHeightPoints);
		renderer.Frame = bounds;
		renderer.Bounds = bounds;
	}

	static void SetRealisticButtonBounds(UIButton button)
	{
		var bounds = new CGRect(0, 0, ButtonWidthPoints, ButtonHeightPoints);
		button.Frame = bounds;
		button.Bounds = bounds;
		button.ContentEdgeInsets = UIEdgeInsets.Zero;
		button.ImageEdgeInsets = UIEdgeInsets.Zero;
		button.TitleEdgeInsets = UIEdgeInsets.Zero;
		button.LayoutIfNeeded();
	}

	static async Task WaitForAssignedImageAsync(UIButton button)
	{
		for (var i = 0; i < 80; i++)
		{
			using var pool = new NSAutoreleasePool();
			if (button.ImageForState(UIControlState.Normal) is not null)
				return;

			await Task.Delay(25);
		}

		throw new InvalidOperationException("ButtonRenderer did not assign a native UIButton image.");
	}

	static RetainedButtonPeer RetainNativeButton(UIButton button)
	{
		var handle = button.Handle;
		if (handle == IntPtr.Zero)
			throw new InvalidOperationException("Cannot retain a native UIButton with a zero handle.");

		var retained = IntPtr_objc_msgSend(handle, RetainSelector);
		if (retained == IntPtr.Zero)
			throw new InvalidOperationException("Objective-C retain returned a zero handle.");

		return new RetainedButtonPeer(retained);
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

	internal sealed class RetainedButtonPeer
	{
		public RetainedButtonPeer(IntPtr handle)
		{
			Handle = handle;
		}

		public IntPtr Handle { get; }

		public UIButton? TryGetButton()
		{
			if (Handle == IntPtr.Zero)
				return null;

			try
			{
				var button = Runtime.GetNSObject<UIButton>(Handle, false);
				return button?.Handle == IntPtr.Zero ? null : button;
			}
			catch
			{
				return null;
			}
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<ButtonRenderer> Renderer,
		WeakReference<Button> Button,
		WeakReference<ButtonPayload> Payload,
		WeakReference<TrackingImageSource> Source,
		long PayloadBytes)
	{
		public static TrackedCycle Create(
			int cycle,
			ButtonRenderer renderer,
			Button button,
			ButtonPayload payload,
			TrackingImageSource source)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<ButtonRenderer>(renderer),
				new WeakReference<Button>(button),
				new WeakReference<ButtonPayload>(payload),
				new WeakReference<TrackingImageSource>(source),
				payload.PayloadBytes);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int ImagesCreated,
		int RetainedNativeButtonPeers,
		int NativeButtonsWithAssignedImages,
		long EstimatedAssignedImageBytes,
		int AliveRenderers,
		int AliveButtons,
		int AlivePayloads,
		int AliveSources,
		long RetainedPayloadBytes)
	{
		internal static ScenarioResult From(
			string name,
			ScenarioLedger ledger,
			IReadOnlyList<RetainedButtonPeer> nativeButtons,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativeButtons = 0;
			var nativeButtonsWithAssignedImages = 0;
			long estimatedAssignedImageBytes = 0;

			foreach (var peer in nativeButtons)
			{
				var button = peer.TryGetButton();
				if (button is null)
					continue;

				aliveNativeButtons++;
				if (button.ImageForState(UIControlState.Normal) is UIImage image)
				{
					nativeButtonsWithAssignedImages++;
					estimatedAssignedImageBytes += EstimateImageBytes(image);
				}
			}

			var aliveRenderers = 0;
			var aliveButtons = 0;
			var alivePayloads = 0;
			var aliveSources = 0;
			long retainedPayloadBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.Renderer.TryGetTarget(out _))
					aliveRenderers++;

				if (cycle.Button.TryGetTarget(out _))
					aliveButtons++;

				if (cycle.Payload.TryGetTarget(out _))
				{
					alivePayloads++;
					retainedPayloadBytes += cycle.PayloadBytes;
				}

				if (cycle.Source.TryGetTarget(out _))
					aliveSources++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				ledger.ImagesCreated,
				aliveNativeButtons,
				nativeButtonsWithAssignedImages,
				estimatedAssignedImageBytes,
				aliveRenderers,
				aliveButtons,
				alivePayloads,
				aliveSources,
				retainedPayloadBytes);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int ButtonWidthPoints,
	int ButtonHeightPoints,
	int SourceImagePixels,
	int PayloadMegabytesPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.ImagesCreated == Cycles &&
		Control.RetainedNativeButtonPeers == Cycles &&
		Control.NativeButtonsWithAssignedImages == 0 &&
		Control.AliveButtons == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AliveSources == 0 &&
		Current.ImagesCreated == Cycles &&
		Current.RetainedNativeButtonPeers == Cycles &&
		Current.NativeButtonsWithAssignedImages == Cycles &&
		Current.EstimatedAssignedImageBytes >= Cycles * SourceImagePixels * SourceImagePixels * 4L &&
		Current.AliveButtons == 0 &&
		Current.AlivePayloads == 0 &&
		Current.AliveSources == 0;

	public string ToText()
	{
		var retainedMiB = Current.EstimatedAssignedImageBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedAssignedImageBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosButtonRendererImageRetentionRepro",
			$"Cycles: {Cycles}",
			$"Button size: {ButtonWidthPoints} x {ButtonHeightPoints} points",
			$"Generated source image size: {SourceImagePixels} x {SourceImagePixels} pixels",
			$"Payload size per Button: {PayloadMegabytesPerCycle} MiB",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated assigned native button image payload: {controlMiB:N1} MiB",
			$"Current estimated assigned native button image payload: {retainedMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var retainedMiB = result.EstimatedAssignedImageBytes / 1024d / 1024d;
		var payloadMiB = result.RetainedPayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  generated UIImages: {result.ImagesCreated}",
			$"  retained native UIButton peers: {result.RetainedNativeButtonPeers}/{result.TrackedCycles}",
			$"  native buttons with assigned UIImages: {result.NativeButtonsWithAssignedImages}/{result.TrackedCycles}",
			$"  estimated assigned native image bytes: {result.EstimatedAssignedImageBytes:N0}",
			$"  estimated assigned native image MiB: {retainedMiB:N1}",
			$"  alive renderers: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  alive Buttons: {result.AliveButtons}/{result.TrackedCycles}",
			$"  alive payloads: {result.AlivePayloads}/{result.TrackedCycles}",
			$"  alive image sources: {result.AliveSources}/{result.TrackedCycles}",
			$"  retained managed payload MiB: {payloadMiB:N1}");
	}
}

internal sealed class PayloadButton : Button
{
	public PayloadButton(int cycle, ButtonPayload payload)
	{
		Cycle = cycle;
		Payload = payload;
		Text = string.Empty;
		BindingContext = payload;
		WidthRequest = ReproSession.ButtonWidthPoints;
		HeightRequest = ReproSession.ButtonHeightPoints;
		BackgroundColor = Colors.Transparent;
	}

	public int Cycle { get; }

	public ButtonPayload Payload { get; }
}

internal sealed class ButtonPayload
{
	readonly byte[] _bytes;

	public ButtonPayload(int cycle, long bytes)
	{
		_bytes = new byte[checked((int)bytes)];
		for (var i = 0; i < _bytes.Length; i += 4096)
			_bytes[i] = (byte)((cycle + i) % 251);
	}

	public long PayloadBytes => _bytes.LongLength;
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
				(nfloat)((cycle * 73) % 255) / 255f,
				(nfloat)((cycle * 109) % 255) / 255f).SetFill();
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
