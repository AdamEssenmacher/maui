#nullable enable

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Platform.Compatibility;
using ObjCRuntime;
using UIKit;

namespace IosShellBackBarButtonTitleRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 96;
	internal const int PayloadKiBPerTitle = 256;

	const long PayloadBytesPerTitle = PayloadKiBPerTitle * 1024L;

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");

	static readonly List<IReadOnlyList<RetainedNativeNavigationItem>> RetainedNativeNavigationItems = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-shell-backbarbutton-title-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS Shell back bar button title retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear Shell BackBarButtonItem title before retaining previous navigation item",
			context,
			clearNativeTitle: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: ShellPageRendererTracker leaves BackBarButtonItem title assigned",
			context,
			clearNativeTitle: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeNavigationItems);

		return new ReproReport(
			Cycles,
			PayloadKiBPerTitle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearNativeTitle)
	{
		var retainedItems = new List<RetainedNativeNavigationItem>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 16 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var cycleResult = await RunCycleAsync(i, context, clearNativeTitle);
			retainedItems.Add(cycleResult.RetainedNavigationItem);
			tracked.Add(cycleResult.Tracked);
		}

		RetainedNativeNavigationItems.Add(retainedItems);
		ForceFullGc();

		return ScenarioResult.From(name, retainedItems, tracked);
	}

	static async Task<CycleResult> RunCycleAsync(
		int cycle,
		IMauiContext context,
		bool clearNativeTitle)
	{
		var page = new ContentPage
		{
			Title = $"Orders {cycle:0000}",
			Content = new Label { Text = $"Orders {cycle:0000}" }
		};
		var behavior = new BackButtonBehavior
		{
			TextOverride = CreateLargeTitle(cycle),
			IsEnabled = true,
			IsVisible = true
		};
		Shell.SetBackButtonBehavior(page, behavior);

		var shell = CreateShell(page);
		var shellHandler = new ContextOnlyElementHandler(context);
		var pageHandler = new ContextOnlyElementHandler(context);
		shellHandler.SetVirtualView(shell);
		pageHandler.SetVirtualView(page);
		shell.Handler = shellHandler;
		page.Handler = pageHandler;

		var shellContext = new FakeShellContext(shell);
		var previousViewController = new UIViewController();
		var currentViewController = new UIViewController();
		var navigationController = new UINavigationController(previousViewController);
		navigationController.SetViewControllers(new[] { previousViewController, currentViewController }, false);

		var tracker = new TestShellPageRendererTracker(shellContext)
		{
			ViewController = currentViewController,
			Page = page,
			IsRootPage = false
		};

		tracker.SetBackButtonBehaviorForTest(behavior);
		tracker.UpdateBackButtonTitleForTest();

		var previousNavigationItem = previousViewController.NavigationItem
			?? throw new InvalidOperationException("Previous UIViewController did not create a UINavigationItem.");

		if (CountAssignedPayloadTitles(previousNavigationItem) != 1)
			throw new InvalidOperationException("ShellPageRendererTracker did not assign the payload-sized native back button title slot.");

		if (clearNativeTitle && previousNavigationItem.BackBarButtonItem is { } backItem)
			backItem.Title = string.Empty;

		var retainedNavigationItem = RetainNativeNavigationItem(previousNavigationItem);

		page.ClearValue(Shell.BackButtonBehaviorProperty);
		tracker.Dispose();
		navigationController.SetViewControllers(Array.Empty<UIViewController>(), false);
		currentViewController.Dispose();
		previousViewController.Dispose();
		navigationController.Dispose();

		shellHandler.DisconnectHandler();
		pageHandler.DisconnectHandler();
		await DrainMainQueueAsync();

		return new CycleResult(
			retainedNavigationItem,
			TrackedCycle.Create(cycle, tracker, shell, page, behavior, shellHandler, pageHandler));
	}

	static Shell CreateShell(Page page)
	{
		var shell = new Shell
		{
			Title = "Operations Shell"
		};

		var shellContent = new ShellContent
		{
			Title = "Orders",
			Content = page
		};

		var shellSection = new ShellSection
		{
			Title = "Operations"
		};
		shellSection.Items.Add(shellContent);
		shellSection.CurrentItem = shellContent;

		var flyoutItem = new FlyoutItem
		{
			Title = "Operations"
		};
		flyoutItem.Items.Add(shellSection);
		flyoutItem.CurrentItem = shellSection;

		shell.Items.Add(flyoutItem);
		shell.CurrentItem = flyoutItem;

		return shell;
	}

	static string CreateLargeTitle(int cycle)
	{
		var header = $"Shell generated back label {cycle:0000}. ";
		var sentence = "Generated case workspace, offline account summary, approval route, and review history. ";
		var targetChars = (int)(PayloadBytesPerTitle / 2);
		var builder = new StringBuilder(targetChars + sentence.Length);
		builder.Append(header);

		while (builder.Length < targetChars)
			builder.Append(sentence);

		if (builder.Length > targetChars)
			builder.Length = targetChars;

		return builder.ToString();
	}

	static int CountAssignedPayloadTitles(UINavigationItem navigationItem)
	{
		return EstimateTitleBytes(navigationItem.BackBarButtonItem?.Title) >= PayloadBytesPerTitle * 0.95 ? 1 : 0;
	}

	static long EstimateAssignedTitleBytes(UINavigationItem navigationItem)
	{
		return Math.Min(EstimateTitleBytes(navigationItem.BackBarButtonItem?.Title), PayloadBytesPerTitle);
	}

	static long EstimateTitleBytes(string? title)
	{
		return string.IsNullOrEmpty(title) ? 0 : title.Length * 2L;
	}

	static RetainedNativeNavigationItem RetainNativeNavigationItem(UINavigationItem navigationItem)
	{
		var handle = navigationItem.Handle;
		if (handle == IntPtr.Zero)
			throw new InvalidOperationException("Cannot retain a native UINavigationItem with a zero handle.");

		var retained = IntPtr_objc_msgSend(handle, RetainSelector);
		if (retained == IntPtr.Zero)
			throw new InvalidOperationException("Objective-C retain returned a zero handle.");

		return new RetainedNativeNavigationItem(retained);
	}

	static async Task DrainMainQueueAsync()
	{
		await Task.Delay(50);
		NSRunLoop.Main.RunUntil(NSDate.FromTimeIntervalSinceNow(0.02));
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			using var pool = new NSAutoreleasePool();
			NSRunLoop.Current.RunUntil(NSDate.FromTimeIntervalSinceNow(0.01));
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(75);
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

	internal sealed record CycleResult(RetainedNativeNavigationItem RetainedNavigationItem, TrackedCycle Tracked);

	internal sealed class RetainedNativeNavigationItem
	{
		public RetainedNativeNavigationItem(IntPtr handle)
		{
			Handle = handle;
		}

		public IntPtr Handle { get; }

		public UINavigationItem? TryGetNavigationItem()
		{
			if (Handle == IntPtr.Zero)
				return null;

			try
			{
				return Runtime.GetNSObject<UINavigationItem>(Handle, false);
			}
			catch
			{
				return null;
			}
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<TestShellPageRendererTracker> Tracker,
		WeakReference<Shell> Shell,
		WeakReference<ContentPage> Page,
		WeakReference<BackButtonBehavior> Behavior,
		WeakReference<ContextOnlyElementHandler> ShellHandler,
		WeakReference<ContextOnlyElementHandler> PageHandler)
	{
		public static TrackedCycle Create(
			int cycle,
			TestShellPageRendererTracker tracker,
			Shell shell,
			ContentPage page,
			BackButtonBehavior behavior,
			ContextOnlyElementHandler shellHandler,
			ContextOnlyElementHandler pageHandler)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<TestShellPageRendererTracker>(tracker),
				new WeakReference<Shell>(shell),
				new WeakReference<ContentPage>(page),
				new WeakReference<BackButtonBehavior>(behavior),
				new WeakReference<ContextOnlyElementHandler>(shellHandler),
				new WeakReference<ContextOnlyElementHandler>(pageHandler));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativeNavigationItems,
		int AssignedPayloadTitleSlots,
		long EstimatedAssignedTitleBytes,
		int AliveTrackers,
		int AliveShells,
		int AlivePages,
		int AliveBehaviors,
		int AliveShellHandlers,
		int AlivePageHandlers)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedNativeNavigationItem> retainedItems,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var retainedNativeNavigationItems = 0;
			var assignedPayloadTitleSlots = 0;
			long estimatedAssignedTitleBytes = 0;

			foreach (var retainedItem in retainedItems)
			{
				var navigationItem = retainedItem.TryGetNavigationItem();
				if (navigationItem is null)
					continue;

				retainedNativeNavigationItems++;
				assignedPayloadTitleSlots += CountAssignedPayloadTitles(navigationItem);
				estimatedAssignedTitleBytes += EstimateAssignedTitleBytes(navigationItem);
			}

			var aliveTrackers = 0;
			var aliveShells = 0;
			var alivePages = 0;
			var aliveBehaviors = 0;
			var aliveShellHandlers = 0;
			var alivePageHandlers = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.Tracker.TryGetTarget(out _))
					aliveTrackers++;

				if (cycle.Shell.TryGetTarget(out _))
					aliveShells++;

				if (cycle.Page.TryGetTarget(out _))
					alivePages++;

				if (cycle.Behavior.TryGetTarget(out _))
					aliveBehaviors++;

				if (cycle.ShellHandler.TryGetTarget(out _))
					aliveShellHandlers++;

				if (cycle.PageHandler.TryGetTarget(out _))
					alivePageHandlers++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedNativeNavigationItems,
				assignedPayloadTitleSlots,
				estimatedAssignedTitleBytes,
				aliveTrackers,
				aliveShells,
				alivePages,
				aliveBehaviors,
				aliveShellHandlers,
				alivePageHandlers);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadKiBPerTitle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedNativeNavigationItems == Cycles &&
		Control.AssignedPayloadTitleSlots == 0 &&
		Current.RetainedNativeNavigationItems == Cycles &&
		Current.AssignedPayloadTitleSlots == Cycles &&
		Current.EstimatedAssignedTitleBytes >= Cycles * PayloadKiBPerTitle * 1024L * 0.95 &&
		Current.AliveShells <= 1 &&
		Current.AlivePages <= 1 &&
		Current.AliveBehaviors <= 1 &&
		Current.AliveTrackers <= 1 &&
		Current.AliveShellHandlers == 0 &&
		Current.AlivePageHandlers <= 1;

	public string ToText()
	{
		var currentMiB = Current.EstimatedAssignedTitleBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedAssignedTitleBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosShellBackBarButtonTitleRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload per native back title slot: {PayloadKiBPerTitle} KiB",
			$"Native back title slots per cycle: 1",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated retained native back title payload: {controlMiB:N1} MiB",
			$"Current estimated retained native back title payload: {currentMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var nativeTitleMiB = result.EstimatedAssignedTitleBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained previous native navigation items: {result.RetainedNativeNavigationItems}/{result.TrackedCycles}",
			$"  assigned payload-sized back title slots: {result.AssignedPayloadTitleSlots}/{result.TrackedCycles}",
			$"  estimated retained native back title bytes: {result.EstimatedAssignedTitleBytes:N0}",
			$"  estimated retained native back title MiB: {nativeTitleMiB:N1}",
			$"  alive trackers: {result.AliveTrackers}/{result.TrackedCycles}",
			$"  alive shells: {result.AliveShells}/{result.TrackedCycles}",
			$"  alive pages: {result.AlivePages}/{result.TrackedCycles}",
			$"  alive BackButtonBehaviors: {result.AliveBehaviors}/{result.TrackedCycles}",
			$"  alive shell handlers: {result.AliveShellHandlers}/{result.TrackedCycles}",
			$"  alive page handlers: {result.AlivePageHandlers}/{result.TrackedCycles}");
	}
}

internal sealed class TestShellPageRendererTracker : ShellPageRendererTracker
{
	static readonly MethodInfo SetBackButtonBehaviorMethod =
		typeof(ShellPageRendererTracker).GetMethod("SetBackButtonBehavior", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(ShellPageRendererTracker).FullName, "SetBackButtonBehavior");

	static readonly MethodInfo UpdateBackButtonTitleMethod =
		typeof(ShellPageRendererTracker).GetMethod("UpdateBackButtonTitle", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(ShellPageRendererTracker).FullName, "UpdateBackButtonTitle");

	public TestShellPageRendererTracker(IShellContext context)
		: base(context)
	{
	}

	public void SetBackButtonBehaviorForTest(BackButtonBehavior behavior)
	{
		SetBackButtonBehaviorMethod.Invoke(this, new object[] { behavior });
	}

	public void UpdateBackButtonTitleForTest()
	{
		UpdateBackButtonTitleMethod.Invoke(this, Array.Empty<object>());
	}
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

	public Microsoft.Maui.Graphics.Size GetDesiredSize(double widthConstraint, double heightConstraint)
	{
		return Microsoft.Maui.Graphics.Size.Zero;
	}

	public void PlatformArrange(Microsoft.Maui.Graphics.Rect frame)
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
