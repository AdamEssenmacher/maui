#nullable enable

using System.Reflection;
using System.Threading;
using CoreGraphics;
using Foundation;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.iOS;
using UIKit;

namespace IosTabletFlyoutPageBackgroundPatternRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 12;
	internal const int SourceImagePixels = 768;
	internal const int PayloadMegabytesPerCycle = 1;

	static readonly List<RetainedPeer> RetainedNativePeers = new();
	static readonly FieldInfo EventsField =
		typeof(TabletFlyoutPageRenderer).GetField("_events", BindingFlags.NonPublic | BindingFlags.Instance)
		?? throw new InvalidOperationException("Could not find TabletFlyoutPageRenderer._events.");
	static readonly MethodInfo UpdateBackgroundMethod =
		typeof(TabletFlyoutPageRenderer).GetMethod("UpdateBackground", BindingFlags.NonPublic | BindingFlags.Instance)
		?? throw new InvalidOperationException("Could not find TabletFlyoutPageRenderer.UpdateBackground.");

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-tabletflyout-background-pattern-retention-results.txt");

	public static async Task<ReproReport> RunAsync()
	{
		RegisterLegacyImageHandlers();

		WriteProgress("Starting TabletFlyoutPageRenderer background pattern retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear native pattern background after disposal",
			clearNativeBackground: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: TabletFlyoutPageRenderer dispose leaves native pattern background assigned",
			clearNativeBackground: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativePeers);

		return new ReproReport(
			Cycles,
			SourceImagePixels,
			PayloadMegabytesPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		bool clearNativeBackground)
	{
		var retainedPeers = new List<RetainedPeer>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 3 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var result = await RunCycleAsync(i, clearNativeBackground);
			retainedPeers.Add(result.RetainedPeer);
			tracked.Add(result.Tracked);
		}

		RetainedNativePeers.AddRange(retainedPeers);
		ForceFullGc();

		return ScenarioResult.From(name, retainedPeers, tracked);
	}

	static async Task<CycleResult> RunCycleAsync(
		int cycle,
		bool clearNativeBackground)
	{
			var payload = new FlyoutPayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
			var pngBytes = CreatePngBytes(cycle);
			var source = ImageSource.FromStream(() => new MemoryStream(pngBytes));
		await VerifyLegacyImageHandlerAsync(source);
		var flyoutPage = new PayloadFlyoutPage(cycle, payload);
		var renderer = new TabletFlyoutPageRenderer();

		EventsField.SetValue(renderer, new EventTracker(renderer));
		renderer.SetElement(flyoutPage);
		renderer.LoadViewIfNeeded();
		var nativePeer = renderer.View ?? throw new InvalidOperationException("TabletFlyoutPageRenderer did not create a UIView.");
		nativePeer.Frame = new CGRect(0, 0, SourceImagePixels, SourceImagePixels);
		nativePeer.Bounds = nativePeer.Frame;
		flyoutPage.BackgroundImageSource = source;
		UpdateBackgroundMethod.Invoke(renderer, null);
		await DrainMainQueueAsync();

		if (!HasPatternBackground(nativePeer))
			throw new InvalidOperationException("TabletFlyoutPageRenderer did not assign a pattern-image background.");

		renderer.Dispose();
		((IElementController)flyoutPage).EffectControlProvider = null;
		((IElementController)flyoutPage.Flyout).EffectControlProvider = null;
		((IElementController)flyoutPage.Detail).EffectControlProvider = null;
		flyoutPage.Handler = null;
		flyoutPage.Flyout.Handler = null;
		flyoutPage.Detail.Handler = null;

		if (clearNativeBackground)
			nativePeer.BackgroundColor = UIColor.White;

		return new CycleResult(
			new RetainedPeer(nativePeer),
			TrackedCycle.Create(cycle, nativePeer, renderer, flyoutPage, payload, source));
	}

	static void RegisterLegacyImageHandlers()
	{
		Microsoft.Maui.Controls.Internals.Registrar.Registered.Register(
			typeof(StreamImageSource),
			typeof(StreamImagesourceHandler));
	}

	static async Task VerifyLegacyImageHandlerAsync(ImageSource source)
	{
		var handler = Microsoft.Maui.Controls.Internals.Registrar.Registered
			.GetHandlerForObject<IImageSourceHandler>(source);

		if (handler is null)
			throw new InvalidOperationException("Legacy StreamImageSource handler was not registered.");

		using var image = await handler.LoadImageAsync(source, scale: (float)UIScreen.MainScreen.Scale);
		if (image is null)
			throw new InvalidOperationException("Legacy StreamImageSource handler returned no UIImage.");
	}

	static byte[] CreatePngBytes(int cycle)
	{
		var size = new CGSize(SourceImagePixels, SourceImagePixels);
		var renderer = new UIGraphicsImageRenderer(size, new UIGraphicsImageRendererFormat
		{
			Opaque = true,
			Scale = 1
		});

		using var image = renderer.CreateImage(context =>
		{
			var cg = context.CGContext;
			var colorA = UIColor.FromRGB((nfloat)((cycle * 29) % 255) / 255f, 0.23f, 0.58f);
			var colorB = UIColor.FromRGB(0.12f, (nfloat)((cycle * 47) % 255) / 255f, 0.74f);
			var colorC = UIColor.FromRGB(0.91f, 0.36f, (nfloat)((cycle * 83) % 255) / 255f);

			colorA.SetFill();
			cg.FillRect(new CGRect(0, 0, SourceImagePixels, SourceImagePixels));
			colorB.SetFill();
			cg.FillEllipseInRect(new CGRect(96, 96, 420, 420));
			colorC.SetFill();
			cg.FillRect(new CGRect(280, 280, 360, 360));
		});

		using var data = image.AsPNG()
			?? throw new InvalidOperationException("Failed to encode generated background image.");
		return data.ToArray();
	}

	internal static async Task DrainMainQueueAsync()
	{
		for (var i = 0; i < 20; i++)
		{
			await Task.Delay(25);
			NSRunLoop.Main.RunUntil(NSDate.FromTimeIntervalSinceNow(0.005));
		}
	}

	static bool HasPatternBackground(UIView view)
	{
		var color = view.BackgroundColor;
		if (color is null)
			return false;

		try
		{
			if (color.CGColor?.Pattern is not null)
				return true;
		}
		catch
		{
			return true;
		}

		nfloat red;
		nfloat green;
		nfloat blue;
		nfloat alpha;

		try
		{
			color.GetRGBA(out red, out green, out blue, out alpha);
		}
		catch
		{
			return true;
		}

		const double tolerance = 0.001;
		return Math.Abs(red - 1) > tolerance ||
			Math.Abs(green - 1) > tolerance ||
			Math.Abs(blue - 1) > tolerance ||
			Math.Abs(alpha - 1) > tolerance;
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

	internal sealed record RetainedPeer(UIView Peer);

	internal sealed record CycleResult(RetainedPeer RetainedPeer, TrackedCycle Tracked);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<UIView> NativePeer,
		WeakReference<TabletFlyoutPageRenderer> Renderer,
		WeakReference<FlyoutPage> FlyoutPage,
		WeakReference<FlyoutPayload> Payload,
		WeakReference<ImageSource> Source,
		long PayloadBytes)
	{
		public static TrackedCycle Create(
			int cycle,
			UIView nativePeer,
			TabletFlyoutPageRenderer renderer,
			FlyoutPage flyoutPage,
			FlyoutPayload payload,
			ImageSource source)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<UIView>(nativePeer),
				new WeakReference<TabletFlyoutPageRenderer>(renderer),
				new WeakReference<FlyoutPage>(flyoutPage),
				new WeakReference<FlyoutPayload>(payload),
				new WeakReference<ImageSource>(source),
				payload.PayloadBytes);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativePeers,
		int NativePeersWithPatternBackground,
		long EstimatedPatternImageBytes,
		int AliveNativePeers,
		int AliveRenderers,
		int AliveFlyoutPages,
		int AlivePayloads,
		int AliveSources,
		long RetainedPayloadBytes)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedPeer> retainedPeers,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var peersWithPatternBackground = retainedPeers.Count(peer => HasPatternBackground(peer.Peer));
			var aliveNativePeers = 0;
			var aliveRenderers = 0;
			var aliveFlyoutPages = 0;
			var alivePayloads = 0;
			var aliveSources = 0;
			long retainedPayloadBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativePeer.TryGetTarget(out _))
					aliveNativePeers++;

				if (cycle.Renderer.TryGetTarget(out _))
					aliveRenderers++;

				if (cycle.FlyoutPage.TryGetTarget(out _))
					aliveFlyoutPages++;

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
				retainedPeers.Count,
				peersWithPatternBackground,
				peersWithPatternBackground * EstimatePatternImageBytes(),
				aliveNativePeers,
				aliveRenderers,
				aliveFlyoutPages,
				alivePayloads,
				aliveSources,
				retainedPayloadBytes);
		}
	}

	static long EstimatePatternImageBytes() => SourceImagePixels * (long)SourceImagePixels * 4;
}

internal sealed record ReproReport(
	int Cycles,
	int SourceImagePixels,
	int PayloadMegabytesPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedNativePeers == Cycles &&
		Control.NativePeersWithPatternBackground == 0 &&
		Control.AliveFlyoutPages == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AliveSources == 0 &&
		Current.RetainedNativePeers == Cycles &&
		Current.NativePeersWithPatternBackground == Cycles &&
		Current.EstimatedPatternImageBytes > 0 &&
		Current.AliveFlyoutPages == 0 &&
		Current.AlivePayloads == 0 &&
		Current.AliveSources == 0;

	public string ToText()
	{
		var retainedMiB = Current.EstimatedPatternImageBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedPatternImageBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosTabletFlyoutPageBackgroundPatternRetentionRepro",
			$"Cycles: {Cycles}",
			$"Source image: {SourceImagePixels} x {SourceImagePixels} pixels",
			$"Payload size per FlyoutPage: {PayloadMegabytesPerCycle} MiB",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated assigned native pattern image payload: {controlMiB:N1} MiB",
			$"Current estimated assigned native pattern image payload: {retainedMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var retainedMiB = result.EstimatedPatternImageBytes / 1024d / 1024d;
		var payloadMiB = result.RetainedPayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native UIView peers: {result.RetainedNativePeers}/{result.TrackedCycles}",
			$"  native peers with pattern background: {result.NativePeersWithPatternBackground}/{result.TrackedCycles}",
			$"  estimated assigned native image bytes: {result.EstimatedPatternImageBytes:N0}",
			$"  estimated assigned native image MiB: {retainedMiB:N1}",
			$"  alive native peers: {result.AliveNativePeers}/{result.TrackedCycles}",
			$"  alive renderers: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  alive FlyoutPages: {result.AliveFlyoutPages}/{result.TrackedCycles}",
			$"  alive payloads: {result.AlivePayloads}/{result.TrackedCycles}",
			$"  alive image sources: {result.AliveSources}/{result.TrackedCycles}",
			$"  retained managed payload MiB: {payloadMiB:N1}");
	}
}

internal sealed class PayloadFlyoutPage : FlyoutPage
{
	public PayloadFlyoutPage(int cycle, FlyoutPayload payload)
	{
		Payload = payload;
		Title = $"Tablet flyout {cycle}";
		Flyout = new ContentPage { Title = $"Flyout {cycle}", Content = new Label { Text = "Flyout" } };
		Detail = new ContentPage { Title = $"Detail {cycle}", Content = new Label { Text = "Detail" } };
	}

	public FlyoutPayload Payload { get; }
}

internal sealed class FlyoutPayload
{
	readonly byte[] _bytes;

	public FlyoutPayload(int cycle, long bytes)
	{
		_bytes = new byte[checked((int)bytes)];
		for (var i = 0; i < _bytes.Length; i += 4096)
			_bytes[i] = (byte)((cycle + i) % 251);
	}

	public long PayloadBytes => _bytes.LongLength;
}
