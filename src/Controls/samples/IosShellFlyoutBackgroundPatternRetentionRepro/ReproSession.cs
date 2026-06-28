#nullable enable

using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using CoreGraphics;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Platform.Compatibility;
using Microsoft.Maui.Graphics;
using UIKit;

namespace IosShellFlyoutBackgroundPatternRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 12;
	internal const int BackgroundWidthPoints = 384;
	internal const int BackgroundHeightPoints = 512;

	static readonly List<RetainedPeer> RetainedNativePeers = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-shell-flyout-background-pattern-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS Shell flyout background pattern retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear native pattern background before retaining peer",
			context,
			clearNativeBackground: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: MAUI ShellFlyoutContentRenderer leaves native pattern background assigned",
			context,
			clearNativeBackground: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativePeers);

		return new ReproReport(
			Cycles,
			BackgroundWidthPoints,
			BackgroundHeightPoints,
			GetDisplayScale(),
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearNativeBackground)
	{
		var retainedPeers = new List<RetainedPeer>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 6 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var result = await RunCycleAsync(i, context, clearNativeBackground);
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
		bool clearNativeBackground)
	{
		var shell = CreateShell(cycle);
		var shellHandler = new ContextOnlyElementHandler(context);
		shellHandler.SetVirtualView(shell);
		shell.Handler = shellHandler;

		var shellContext = new FakeShellContext(shell);
		var renderer = new TestShellFlyoutContentRenderer(shellContext);
		renderer.LoadViewIfNeeded();
		var nativePeer = renderer.View ?? throw new InvalidOperationException("ShellFlyoutContentRenderer did not create a UIView.");
		nativePeer.Frame = new CGRect(0, 0, BackgroundWidthPoints, BackgroundHeightPoints);
		nativePeer.Bounds = new CGRect(0, 0, BackgroundWidthPoints, BackgroundHeightPoints);

		await renderer.UpdateBackgroundAsync();

		if (!HasPatternBackground(nativePeer))
			throw new InvalidOperationException("ShellFlyoutContentRenderer did not assign a pattern-image background.");

		renderer.ExplicitManagedTeardown(shell);
		shellHandler.DisconnectHandler();

		if (clearNativeBackground)
			nativePeer.BackgroundColor = UIColor.White;

		return new CycleResult(
			new RetainedPeer(nativePeer),
			TrackedCycle.Create(cycle, nativePeer, renderer, shell, shellHandler));
	}

	static Shell CreateShell(int cycle)
	{
		var shell = new Shell
		{
			Title = "Operations Shell",
			FlyoutBackground = new LinearGradientBrush
			{
				StartPoint = new Point(0, 0),
				EndPoint = new Point(1, 1),
				GradientStops =
				{
					new GradientStop(Color.FromRgb((cycle * 29) % 255, 64, 116), 0),
					new GradientStop(Color.FromRgb(36, (cycle * 47) % 255, 176), 0.55f),
					new GradientStop(Color.FromRgb(18, 24, (cycle * 83) % 255), 1)
				}
			}
		};

		var shellContent = new ShellContent
		{
			Title = "Dashboard",
			Content = new ContentPage
			{
				Title = "Dashboard",
				Content = new Label { Text = "Dashboard" }
			}
		};

		var section = new ShellSection { Title = "Operations" };
		section.Items.Add(shellContent);
		var item = new FlyoutItem { Title = "Operations" };
		item.Items.Add(section);
		shell.Items.Add(item);

		return shell;
	}

	internal static async Task DrainMainQueueAsync()
	{
		await Task.Delay(20);
		NSRunLoop.Main.RunUntil(NSDate.FromTimeIntervalSinceNow(0.005));
	}

	static bool HasPatternBackground(UIView view)
	{
		var color = view.BackgroundColor;
		if (color is null)
			return false;

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

	static nfloat GetDisplayScale() => UIScreen.MainScreen.Scale <= 0 ? 1 : UIScreen.MainScreen.Scale;

	static long EstimatePatternImageBytes()
	{
		var scale = GetDisplayScale();
		var width = Math.Max(1, (int)Math.Ceiling(BackgroundWidthPoints * scale));
		var height = Math.Max(1, (int)Math.Ceiling(BackgroundHeightPoints * scale));
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

	internal sealed record RetainedPeer(UIView Peer);

	internal sealed record CycleResult(RetainedPeer RetainedPeer, TrackedCycle Tracked);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<UIView> NativePeer,
		WeakReference<TestShellFlyoutContentRenderer> Renderer,
		WeakReference<Shell> Shell,
		WeakReference<ContextOnlyElementHandler> ShellHandler)
	{
		public static TrackedCycle Create(
			int cycle,
			UIView nativePeer,
			TestShellFlyoutContentRenderer renderer,
			Shell shell,
			ContextOnlyElementHandler shellHandler)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<UIView>(nativePeer),
				new WeakReference<TestShellFlyoutContentRenderer>(renderer),
				new WeakReference<Shell>(shell),
				new WeakReference<ContextOnlyElementHandler>(shellHandler));
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
		int AliveShells,
		int AliveShellHandlers)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedPeer> retainedPeers,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var nativePeersWithPatternBackground = 0;

			foreach (var retainedPeer in retainedPeers)
			{
				if (HasPatternBackground(retainedPeer.Peer))
					nativePeersWithPatternBackground++;
			}

			var aliveNativePeers = 0;
			var aliveRenderers = 0;
			var aliveShells = 0;
			var aliveShellHandlers = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativePeer.TryGetTarget(out _))
					aliveNativePeers++;

				if (cycle.Renderer.TryGetTarget(out _))
					aliveRenderers++;

				if (cycle.Shell.TryGetTarget(out _))
					aliveShells++;

				if (cycle.ShellHandler.TryGetTarget(out _))
					aliveShellHandlers++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedPeers.Count,
				nativePeersWithPatternBackground,
				nativePeersWithPatternBackground * EstimatePatternImageBytes(),
				aliveNativePeers,
				aliveRenderers,
				aliveShells,
				aliveShellHandlers);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int BackgroundWidthPoints,
	int BackgroundHeightPoints,
	nfloat DisplayScale,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedNativePeers == Cycles &&
		Control.NativePeersWithPatternBackground == 0 &&
		Current.RetainedNativePeers == Cycles &&
		Current.NativePeersWithPatternBackground == Cycles &&
		Current.EstimatedPatternImageBytes > Control.EstimatedPatternImageBytes &&
		Control.AliveShells <= 1 &&
		Current.AliveShells <= 1 &&
		Current.AliveShellHandlers == 0;

	public string ToText()
	{
		var retainedMiB = Current.EstimatedPatternImageBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedPatternImageBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosShellFlyoutBackgroundPatternRetentionRepro",
			$"Cycles: {Cycles}",
			$"Rendered background size: {BackgroundWidthPoints} x {BackgroundHeightPoints} points",
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
			$"Control estimated assigned native pattern image payload: {controlMiB:N1} MiB",
			$"Current estimated assigned native pattern image payload: {retainedMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var nativeImageMiB = result.EstimatedPatternImageBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native peers: {result.RetainedNativePeers}/{result.TrackedCycles}",
			$"  native peers with pattern background: {result.NativePeersWithPatternBackground}/{result.TrackedCycles}",
			$"  estimated assigned native image bytes: {result.EstimatedPatternImageBytes:N0}",
			$"  estimated assigned native image MiB: {nativeImageMiB:N1}",
			$"  alive native peers: {result.AliveNativePeers}/{result.TrackedCycles}",
			$"  alive renderers: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  alive shells: {result.AliveShells}/{result.TrackedCycles}",
			$"  alive shell handlers: {result.AliveShellHandlers}/{result.TrackedCycles}");
	}
}

internal sealed class TestShellFlyoutContentRenderer : ShellFlyoutContentRenderer
{
	static readonly MethodInfo HandleShellPropertyChangedMethod =
		typeof(ShellFlyoutContentRenderer).GetMethod("HandleShellPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(ShellFlyoutContentRenderer).FullName, "HandleShellPropertyChanged");

	public TestShellFlyoutContentRenderer(IShellContext context)
		: base(context)
	{
	}

	public async Task UpdateBackgroundAsync()
	{
		UpdateBackground();
		await ReproSession.DrainMainQueueAsync();
	}

	public void ExplicitManagedTeardown(Shell shell)
	{
		var shellChanged = (PropertyChangedEventHandler)Delegate.CreateDelegate(typeof(PropertyChangedEventHandler), this, HandleShellPropertyChangedMethod);
		shell.PropertyChanged -= shellChanged;
		TableViewControllerField(this)?.Dispose();
		BackgroundImageViewField(this)?.RemoveFromSuperview();
		Dispose();
	}

	[UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_tableViewController")]
	static extern ref ShellTableViewController TableViewControllerField(ShellFlyoutContentRenderer renderer);

	[UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_bgImage")]
	static extern ref UIImageView BackgroundImageViewField(ShellFlyoutContentRenderer renderer);
}

internal sealed class ContextOnlyElementHandler : IViewHandler
{
	public ContextOnlyElementHandler(IMauiContext context)
	{
		MauiContext = context;
	}

	public object? PlatformView => null;

	public bool HasContainer { get; set; }

	public object? ContainerView => null;

	public IElement? VirtualView { get; private set; }

	IView? IViewHandler.VirtualView => VirtualView as IView;

	public IMauiContext? MauiContext { get; private set; }

	public void SetMauiContext(IMauiContext mauiContext)
	{
		MauiContext = mauiContext;
	}

	public void SetVirtualView(IElement view)
	{
		VirtualView = view;
	}

	public void UpdateValue(string property)
	{
	}

	public void Invoke(string command, object? args = null)
	{
	}

	public Size GetDesiredSize(double widthConstraint, double heightConstraint)
	{
		return Size.Zero;
	}

	public void PlatformArrange(Rect frame)
	{
	}

	public void DisconnectHandler()
	{
		if (VirtualView?.Handler == this)
			VirtualView.Handler = null;

		VirtualView = null;
		MauiContext = null;
	}
}

internal sealed class FakeShellContext : IShellContext
{
	public FakeShellContext(Shell shell)
	{
		Shell = shell;
	}

	public bool AllowFlyoutGesture => false;

	public IShellItemRenderer CurrentShellItemRenderer => null!;

	public Shell Shell { get; }

	public IShellPageRendererTracker CreatePageRendererTracker() => throw new NotSupportedException();

	public IShellFlyoutContentRenderer CreateShellFlyoutContentRenderer() => throw new NotSupportedException();

	public IShellSectionRenderer CreateShellSectionRenderer(ShellSection shellSection) => throw new NotSupportedException();

	public IShellNavBarAppearanceTracker CreateNavBarAppearanceTracker() => throw new NotSupportedException();

	public IShellTabBarAppearanceTracker CreateTabBarAppearanceTracker() => throw new NotSupportedException();

	public IShellSearchResultsRenderer CreateShellSearchResultsRenderer() => throw new NotSupportedException();
}
