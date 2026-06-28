#nullable enable

using System.Threading;
using CoreGraphics;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Graphics;
using UIKit;

namespace IosNavigationBarBackgroundImageRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 8;
	internal const int NavigationBarWidthPoints = 1024;
	internal const int NavigationBarHeightPoints = 140;

	static readonly List<RetainedPeer> RetainedNativePeers = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-navigationbar-background-image-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS navigation bar background image retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear native navigation bar background images before retaining peer",
			context,
			clearNativeBackgroundImages: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: MAUI NavigationRenderer leaves native navigation bar background images assigned",
			context,
			clearNativeBackgroundImages: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativePeers);

		return new ReproReport(
			Cycles,
			NavigationBarWidthPoints,
			NavigationBarHeightPoints,
			GetDisplayScale(),
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearNativeBackgroundImages)
	{
		var retainedPeers = new List<RetainedPeer>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 8 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var result = await RunCycleAsync(i, context, clearNativeBackgroundImages);
			retainedPeers.Add(result.RetainedPeer);
			tracked.Add(result.Tracked);
		}

		RetainedNativePeers.AddRange(retainedPeers);
		ForceFullGc();

		return ScenarioResult.From(name, retainedPeers, tracked);
	}

	static async Task<CycleResult> RunCycleAsync(
		int cycle,
		IMauiContext context,
		bool clearNativeBackgroundImages)
	{
		var rootPage = new ContentPage
		{
			Title = $"Orders {cycle}",
			Content = new Label { Text = $"Orders {cycle}" }
		};

		var navPage = new NavigationPage(rootPage)
		{
			Title = "Operations"
		};

		var renderer = new NavigationRenderer();
		((IElementHandler)renderer).SetMauiContext(context);
		renderer.SetElement(navPage);
		renderer.LoadViewIfNeeded();

		SetRealisticNavigationBarBounds(renderer);
		navPage.BarBackground = CreateBarBackground(cycle);

		await DrainMainQueueAsync();

		var navigationBar = renderer.NavigationBar ?? throw new InvalidOperationException("NavigationRenderer did not create a UINavigationBar.");

		if (!HasAssignedBackgroundImage(navigationBar))
			throw new InvalidOperationException("NavigationRenderer did not assign a native navigation bar background image.");

		renderer.Dispose();

		if (navPage.Handler == renderer)
			navPage.Handler = null;

		if (rootPage.Handler is not null)
			rootPage.Handler.DisconnectHandler();

		if (clearNativeBackgroundImages)
			ClearBackgroundImages(navigationBar);

		return new CycleResult(
			new RetainedPeer(navigationBar),
			TrackedCycle.Create(cycle, navigationBar, renderer, navPage, rootPage));
	}

	static Brush CreateBarBackground(int cycle)
	{
		return new LinearGradientBrush
		{
			StartPoint = new Point(0, 0),
			EndPoint = new Point(1, 0),
			GradientStops =
			{
				new GradientStop(Color.FromRgb((cycle * 37) % 255, 84, 124), 0),
				new GradientStop(Color.FromRgb(32, (cycle * 53) % 255, 180), 0.52f),
				new GradientStop(Color.FromRgb(18, 28, (cycle * 71) % 255), 1)
			}
		};
	}

	static void SetRealisticNavigationBarBounds(NavigationRenderer renderer)
	{
		var frame = new CGRect(0, 0, NavigationBarWidthPoints, NavigationBarHeightPoints);
		var view = renderer.View ?? throw new InvalidOperationException("NavigationRenderer did not create a UIView.");
		var navigationBar = renderer.NavigationBar ?? throw new InvalidOperationException("NavigationRenderer did not create a UINavigationBar.");

		view.Frame = new CGRect(0, 0, NavigationBarWidthPoints, 768);
		view.Bounds = new CGRect(0, 0, NavigationBarWidthPoints, 768);
		navigationBar.Frame = frame;
		navigationBar.Bounds = frame;
	}

	internal static async Task DrainMainQueueAsync()
	{
		await Task.Delay(20);
		NSRunLoop.Main.RunUntil(NSDate.FromTimeIntervalSinceNow(0.005));
	}

	static bool HasAssignedBackgroundImage(UINavigationBar navigationBar) =>
		CountAssignedBackgroundImages(navigationBar) > 0;

	static int CountAssignedBackgroundImages(UINavigationBar navigationBar)
	{
		var count = 0;

		if (navigationBar.CompactAppearance?.BackgroundImage is not null)
			count++;

		if (navigationBar.StandardAppearance?.BackgroundImage is not null)
			count++;

		if (navigationBar.ScrollEdgeAppearance?.BackgroundImage is not null)
			count++;

		if (navigationBar.GetBackgroundImage(UIBarMetrics.Default) is not null)
			count++;

		return count;
	}

	static void ClearBackgroundImages(UINavigationBar navigationBar)
	{
		Clear(navigationBar.CompactAppearance);
		Clear(navigationBar.StandardAppearance);
		Clear(navigationBar.ScrollEdgeAppearance);
		navigationBar.SetBackgroundImage(null, UIBarMetrics.Default);

		static void Clear(UINavigationBarAppearance? appearance)
		{
			if (appearance is not null)
				appearance.BackgroundImage = null;
		}
	}

	static nfloat GetDisplayScale() => UIScreen.MainScreen.Scale <= 0 ? 1 : UIScreen.MainScreen.Scale;

	static long EstimateBackgroundImageBytes()
	{
		var scale = GetDisplayScale();
		var width = Math.Max(1, (int)Math.Ceiling(NavigationBarWidthPoints * scale));
		var height = Math.Max(1, (int)Math.Ceiling(NavigationBarHeightPoints * scale));
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

	internal sealed record RetainedPeer(UINavigationBar NavigationBar);

	internal sealed record CycleResult(RetainedPeer RetainedPeer, TrackedCycle Tracked);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<UINavigationBar> NavigationBar,
		WeakReference<NavigationRenderer> Renderer,
		WeakReference<NavigationPage> NavigationPage,
		WeakReference<ContentPage> RootPage)
	{
		public static TrackedCycle Create(
			int cycle,
			UINavigationBar navigationBar,
			NavigationRenderer renderer,
			NavigationPage navigationPage,
			ContentPage rootPage)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<UINavigationBar>(navigationBar),
				new WeakReference<NavigationRenderer>(renderer),
				new WeakReference<NavigationPage>(navigationPage),
				new WeakReference<ContentPage>(rootPage));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativePeers,
		int NativeBarsWithBackgroundImages,
		int AssignedBackgroundImageSlots,
		long EstimatedBackgroundImageBytes,
		int AliveNavigationBars,
		int AliveRenderers,
		int AliveNavigationPages,
		int AliveRootPages)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedPeer> retainedPeers,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var nativeBarsWithBackgroundImages = 0;
			var assignedBackgroundImageSlots = 0;

			foreach (var retainedPeer in retainedPeers)
			{
				var slotCount = CountAssignedBackgroundImages(retainedPeer.NavigationBar);
				assignedBackgroundImageSlots += slotCount;

				if (slotCount > 0)
					nativeBarsWithBackgroundImages++;
			}

			var aliveNavigationBars = 0;
			var aliveRenderers = 0;
			var aliveNavigationPages = 0;
			var aliveRootPages = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NavigationBar.TryGetTarget(out _))
					aliveNavigationBars++;

				if (cycle.Renderer.TryGetTarget(out _))
					aliveRenderers++;

				if (cycle.NavigationPage.TryGetTarget(out _))
					aliveNavigationPages++;

				if (cycle.RootPage.TryGetTarget(out _))
					aliveRootPages++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedPeers.Count,
				nativeBarsWithBackgroundImages,
				assignedBackgroundImageSlots,
				nativeBarsWithBackgroundImages * EstimateBackgroundImageBytes(),
				aliveNavigationBars,
				aliveRenderers,
				aliveNavigationPages,
				aliveRootPages);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int NavigationBarWidthPoints,
	int NavigationBarHeightPoints,
	nfloat DisplayScale,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedNativePeers == Cycles &&
		Control.NativeBarsWithBackgroundImages == 0 &&
		Current.RetainedNativePeers == Cycles &&
		Current.NativeBarsWithBackgroundImages == Cycles &&
		Current.EstimatedBackgroundImageBytes > Control.EstimatedBackgroundImageBytes &&
		Control.AliveNavigationPages <= 2 &&
		Current.AliveNavigationPages <= 2;

	public string ToText()
	{
		var currentMiB = Current.EstimatedBackgroundImageBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedBackgroundImageBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosNavigationBarBackgroundImageRetentionRepro",
			$"Cycles: {Cycles}",
			$"Navigation bar background size: {NavigationBarWidthPoints} x {NavigationBarHeightPoints} points",
			$"Display scale: {DisplayScale:N1}",
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
			$"Current estimated assigned native image payload: {currentMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var nativeImageMiB = result.EstimatedBackgroundImageBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native peers: {result.RetainedNativePeers}/{result.TrackedCycles}",
			$"  native bars with background images: {result.NativeBarsWithBackgroundImages}/{result.TrackedCycles}",
			$"  assigned native background image slots: {result.AssignedBackgroundImageSlots}",
			$"  estimated assigned native image bytes: {result.EstimatedBackgroundImageBytes:N0}",
			$"  estimated assigned native image MiB: {nativeImageMiB:N1}",
			$"  alive navigation bars: {result.AliveNavigationBars}/{result.TrackedCycles}",
			$"  alive renderers: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  alive navigation pages: {result.AliveNavigationPages}/{result.TrackedCycles}",
			$"  alive root pages: {result.AliveRootPages}/{result.TrackedCycles}");
	}
}
