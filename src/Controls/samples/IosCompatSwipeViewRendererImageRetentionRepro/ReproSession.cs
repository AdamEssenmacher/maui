#nullable enable

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using CoreGraphics;
using Foundation;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.iOS;
using Microsoft.Maui.Controls.Internals;
using Microsoft.Maui.Graphics;
using ObjCRuntime;
using UIKit;

namespace IosCompatSwipeViewRendererImageRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 80;
	internal const int ContentSizePoints = 384;
	internal const int SourceImagePixels = 1024;
	internal const int PayloadMegabytesPerCycle = 1;

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");
	static readonly FieldInfo SwipeDirectionField =
		typeof(SwipeViewRenderer).GetField("_swipeDirection", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(SwipeViewRenderer).FullName, "_swipeDirection");
	static readonly FieldInfo ContentViewField =
		typeof(SwipeViewRenderer).GetField("_contentView", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(SwipeViewRenderer).FullName, "_contentView");
	static readonly FieldInfo ActionViewField =
		typeof(SwipeViewRenderer).GetField("_actionView", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(SwipeViewRenderer).FullName, "_actionView");
	static readonly MethodInfo UpdateSwipeItemsMethod =
		typeof(SwipeViewRenderer).GetMethod("UpdateSwipeItems", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(SwipeViewRenderer).FullName, "UpdateSwipeItems");
	static readonly MethodInfo DisposeSwipeItemsMethod =
		typeof(SwipeViewRenderer).GetMethod("DisposeSwipeItems", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(SwipeViewRenderer).FullName, "DisposeSwipeItems");

	static readonly List<IReadOnlyList<RetainedButtonPeer>> RetainedNativeButtons = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-compat-swipeviewrenderer-image-retention-results.txt");

	public static async Task<ReproReport> RunAsync()
	{
		RegisterTrackingImageSourceHandler();

		WriteProgress("Starting iOS compatibility SwipeViewRenderer image retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear native image and swipe item state before renderer disposal",
			clearSwipeStateBeforeDispose: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: SwipeViewRenderer dispose leaves swipe item state and native image assigned",
			clearSwipeStateBeforeDispose: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeButtons);

		return new ReproReport(
			Cycles,
			ContentSizePoints,
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

	static async Task<ScenarioResult> RunScenarioAsync(string name, bool clearSwipeStateBeforeDispose)
	{
		var nativeButtons = new List<RetainedButtonPeer>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);
		var ledger = new ScenarioLedger();

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 10 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			await CreateDisposedRendererCycleAsync(i, nativeButtons, tracked, ledger, clearSwipeStateBeforeDispose);
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
		bool clearSwipeStateBeforeDispose)
	{
		using var pool = new NSAutoreleasePool();

		var payload = new SwipePayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
		var source = new TrackingImageSource(ledger, cycle);
		var swipeItem = new SwipeItem
		{
			Text = $"Action {cycle:000}",
			BackgroundColor = Color.FromRgb((cycle * 37) % 255, 64, 132),
			CommandParameter = payload,
			IconImageSource = source
		};
		var swipeView = new SwipeView
		{
			WidthRequest = ContentSizePoints,
			HeightRequest = ContentSizePoints,
			Content = CreateContent(cycle, "initial")
		};
		var renderer = new SwipeViewRenderer();

		swipeView.TopItems = new SwipeItems { swipeItem };
		swipeView.Layout(new Rect(0, 0, ContentSizePoints, ContentSizePoints));
		SetRealisticBounds(renderer);
		renderer.SetElement(swipeView);

		swipeView.Content = CreateContent(cycle, "active");
		SetRealisticBounds(renderer);
		renderer.LayoutSubviews();
		var contentView = GetContentView(renderer);
		contentView.Frame = new CGRect(0, 0, ContentSizePoints, ContentSizePoints);

		SwipeDirectionField.SetValue(renderer, SwipeDirection.Down);
		UpdateSwipeItemsMethod.Invoke(renderer, Array.Empty<object>());

		var actionButton = await WaitForActionButtonWithImageAsync(renderer);
		var retainedNativeButton = RetainNativeButton(actionButton);

		if (clearSwipeStateBeforeDispose)
		{
			actionButton.SetImage(null, UIControlState.Normal);
			DisposeSwipeItemsMethod.Invoke(renderer, Array.Empty<object>());
		}

		renderer.Dispose();
		swipeView.Content = null;

		nativeButtons.Add(retainedNativeButton);
		tracked.Add(TrackedCycle.Create(cycle, renderer, swipeView, swipeItem, payload, source));
	}

	static View CreateContent(int cycle, string suffix)
	{
		return new Grid
		{
			WidthRequest = ContentSizePoints,
			HeightRequest = ContentSizePoints,
			BindingContext = $"content-{suffix}-{cycle:000}",
			Children =
			{
				new Label
				{
					Text = $"Swipe row {cycle:000}",
					HorizontalOptions = LayoutOptions.Center,
					VerticalOptions = LayoutOptions.Center
				}
			}
		};
	}

	static void SetRealisticBounds(SwipeViewRenderer renderer)
	{
		var bounds = new CGRect(0, 0, ContentSizePoints, ContentSizePoints);
		renderer.Frame = bounds;
		renderer.Bounds = bounds;
	}

	static UIView GetContentView(SwipeViewRenderer renderer)
	{
		return (UIView?)ContentViewField.GetValue(renderer)
			?? throw new InvalidOperationException("SwipeViewRenderer did not create a native content view.");
	}

	static async Task<UIButton> WaitForActionButtonWithImageAsync(SwipeViewRenderer renderer)
	{
		for (var i = 0; i < 120; i++)
		{
			using var pool = new NSAutoreleasePool();
			if (ActionViewField.GetValue(renderer) is UIStackView actionView)
			{
				foreach (var subview in actionView.Subviews)
				{
					if (subview is UIButton button &&
						button.ImageForState(UIControlState.Normal) is not null)
					{
						return button;
					}
				}
			}

			await Task.Delay(25);
		}

		throw new InvalidOperationException("SwipeViewRenderer did not assign a native swipe action button image.");
	}

	static RetainedButtonPeer RetainNativeButton(UIButton button)
	{
		var handle = button.Handle;
		if (handle == IntPtr.Zero)
			throw new InvalidOperationException("Cannot retain a native swipe action UIButton with a zero handle.");

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
		WeakReference<SwipeViewRenderer> Renderer,
		WeakReference<SwipeView> SwipeView,
		WeakReference<SwipeItem> SwipeItem,
		WeakReference<SwipePayload> Payload,
		WeakReference<TrackingImageSource> Source,
		long PayloadBytes)
	{
		public static TrackedCycle Create(
			int cycle,
			SwipeViewRenderer renderer,
			SwipeView swipeView,
			SwipeItem swipeItem,
			SwipePayload payload,
			TrackingImageSource source)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<SwipeViewRenderer>(renderer),
				new WeakReference<SwipeView>(swipeView),
				new WeakReference<SwipeItem>(swipeItem),
				new WeakReference<SwipePayload>(payload),
				new WeakReference<TrackingImageSource>(source),
				payload.PayloadBytes);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int ImagesCreated,
		int RetainedNativeButtons,
		int NativeButtonsWithAssignedImages,
		long EstimatedAssignedImageBytes,
		int AliveRenderers,
		int AliveSwipeViews,
		int AliveSwipeItems,
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
			var aliveSwipeViews = 0;
			var aliveSwipeItems = 0;
			var alivePayloads = 0;
			var aliveSources = 0;
			long retainedPayloadBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.Renderer.TryGetTarget(out _))
					aliveRenderers++;

				if (cycle.SwipeView.TryGetTarget(out _))
					aliveSwipeViews++;

				if (cycle.SwipeItem.TryGetTarget(out _))
					aliveSwipeItems++;

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
				aliveSwipeViews,
				aliveSwipeItems,
				alivePayloads,
				aliveSources,
				retainedPayloadBytes);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int ContentSizePoints,
	int SourceImagePixels,
	int PayloadMegabytesPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.ImagesCreated == Cycles &&
		Control.RetainedNativeButtons == Cycles &&
		Control.NativeButtonsWithAssignedImages == 0 &&
		Control.AliveSwipeViews == 0 &&
		Control.AliveSwipeItems == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AliveSources == 0 &&
		Current.ImagesCreated == Cycles &&
		Current.RetainedNativeButtons == Cycles &&
		Current.NativeButtonsWithAssignedImages == Cycles &&
		Current.EstimatedAssignedImageBytes > 0 &&
		Current.AliveSwipeViews == 0 &&
		Current.AliveRenderers == Cycles &&
		Current.AliveSwipeItems == Cycles &&
		Current.AlivePayloads == Cycles &&
		Current.AliveSources == Cycles;

	public string ToText()
	{
		var retainedMiB = Current.EstimatedAssignedImageBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedAssignedImageBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosCompatSwipeViewRendererImageRetentionRepro",
			$"Cycles: {Cycles}",
			$"Swipe content size: {ContentSizePoints} x {ContentSizePoints} points",
			$"Generated source image size: {SourceImagePixels} x {SourceImagePixels} pixels",
			$"Payload size per SwipeItem: {PayloadMegabytesPerCycle} MiB",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated assigned native swipe action image payload: {controlMiB:N1} MiB",
			$"Current estimated assigned native swipe action image payload: {retainedMiB:N1} MiB",
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
			$"  retained native swipe action buttons: {result.RetainedNativeButtons}/{result.TrackedCycles}",
			$"  native buttons with assigned UIImages: {result.NativeButtonsWithAssignedImages}/{result.TrackedCycles}",
			$"  estimated assigned native image bytes: {result.EstimatedAssignedImageBytes:N0}",
			$"  estimated assigned native image MiB: {retainedMiB:N1}",
			$"  alive renderers: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  alive SwipeViews: {result.AliveSwipeViews}/{result.TrackedCycles}",
			$"  alive SwipeItems: {result.AliveSwipeItems}/{result.TrackedCycles}",
			$"  alive payloads: {result.AlivePayloads}/{result.TrackedCycles}",
			$"  alive image sources: {result.AliveSources}/{result.TrackedCycles}",
			$"  retained managed payload MiB: {payloadMiB:N1}");
	}
}

internal sealed class SwipePayload
{
	readonly byte[] _bytes;

	public SwipePayload(int cycle, long bytes)
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
				(nfloat)((cycle * 29) % 255) / 255f,
				(nfloat)((cycle * 71) % 255) / 255f,
				(nfloat)((cycle * 113) % 255) / 255f).SetFill();
			context.FillRect(new CGRect(0, 0, ReproSession.SourceImagePixels, ReproSession.SourceImagePixels));

			UIColor.FromRGBA(1, 1, 1, 0.35f).SetFill();
			context.FillRect(new CGRect(
				(cycle * 17) % ReproSession.SourceImagePixels,
				(cycle * 31) % ReproSession.SourceImagePixels,
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
