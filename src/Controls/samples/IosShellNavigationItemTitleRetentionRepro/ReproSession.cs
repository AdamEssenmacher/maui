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

namespace IosShellNavigationItemTitleRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 96;
	internal const int PayloadKiBPerTitle = 256;

	const long PayloadBytesPerTitle = PayloadKiBPerTitle * 1024L;

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");
	static readonly PropertyInfo PageToolbarProperty =
		typeof(Page).GetProperty("Toolbar", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(typeof(Page).FullName, "Toolbar");

	static readonly List<IReadOnlyList<RetainedNativeNavigationItem>> RetainedNativeNavigationItems = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-shell-navigationitem-title-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS Shell navigation item title retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear Shell UINavigationItem title before retaining native navigation item",
			context,
			clearNativeTitle: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: ShellPageRendererTracker leaves UINavigationItem title assigned",
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
			Title = CreateLargeTitle(cycle),
			Content = new Label { Text = $"Orders {cycle:0000}" }
		};
		var shell = CreateShell(page);
		var shellHandler = new ContextOnlyElementHandler(context);
		var pageHandler = new ContextOnlyElementHandler(context);
		shellHandler.SetVirtualView(shell);
		pageHandler.SetVirtualView(page);
		shell.Handler = shellHandler;
		page.Handler = pageHandler;

		PrepareShellToolbar(shell, page);

		var shellContext = new FakeShellContext(shell);
		var viewController = new UIViewController();
		var tracker = new TestShellPageRendererTracker(shellContext)
		{
			ViewController = viewController,
			Page = page,
			IsRootPage = true
		};

		tracker.UpdateTitleForTest();

		var navigationItem = viewController.NavigationItem
			?? throw new InvalidOperationException("ShellPageRendererTracker did not create a UINavigationItem.");

		if (CountAssignedPayloadTitles(navigationItem) != 1)
			throw new InvalidOperationException("ShellPageRendererTracker did not assign the payload-sized native title slot.");

		if (clearNativeTitle)
			navigationItem.Title = string.Empty;

		var retainedNavigationItem = RetainNativeNavigationItem(navigationItem);

		tracker.Dispose();
		viewController.Dispose();

		shellHandler.DisconnectHandler();
		pageHandler.DisconnectHandler();
		await DrainMainQueueAsync();

		return new CycleResult(
			retainedNavigationItem,
			TrackedCycle.Create(cycle, tracker, shell, page, shellHandler, pageHandler));
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
			Title = "Operations",
			CurrentItem = shellContent
		};
		shellSection.Items.Add(shellContent);

		var flyoutItem = new FlyoutItem
		{
			Title = "Operations",
			CurrentItem = shellSection
		};
		flyoutItem.Items.Add(shellSection);

		shell.Items.Add(flyoutItem);
		shell.CurrentItem = flyoutItem;

		return shell;
	}

	static void PrepareShellToolbar(Shell shell, Page page)
	{
		var toolbar = PageToolbarProperty.GetValue(shell)
			?? throw new InvalidOperationException("Shell did not create a toolbar.");

		var toolbarType = toolbar.GetType();
		toolbarType.GetMethod("ApplyChanges", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
			?.Invoke(toolbar, Array.Empty<object>());

		var currentPageProperty = toolbarType.GetProperty("CurrentPage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
			?? throw new MissingMemberException(toolbarType.FullName, "CurrentPage");
		var titleProperty = toolbarType.GetProperty("Title", BindingFlags.Instance | BindingFlags.Public)
			?? throw new MissingMemberException(toolbarType.FullName, "Title");

		if (!ReferenceEquals(currentPageProperty.GetValue(toolbar), page))
		{
			var currentPageField = toolbarType.GetField("_currentPage", BindingFlags.Instance | BindingFlags.NonPublic)
				?? throw new MissingFieldException(toolbarType.FullName, "_currentPage");
			currentPageField.SetValue(toolbar, page);
		}

		titleProperty.SetValue(toolbar, page.Title ?? string.Empty);
	}

	static string CreateLargeTitle(int cycle)
	{
		var header = $"Shell generated navigation label {cycle:0000}. ";
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
		return EstimateTitleBytes(navigationItem.Title) >= PayloadBytesPerTitle * 0.95 ? 1 : 0;
	}

	static long EstimateAssignedTitleBytes(UINavigationItem navigationItem)
	{
		return Math.Min(EstimateTitleBytes(navigationItem.Title), PayloadBytesPerTitle);
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
		WeakReference<ContextOnlyElementHandler> ShellHandler,
		WeakReference<ContextOnlyElementHandler> PageHandler)
	{
		public static TrackedCycle Create(
			int cycle,
			TestShellPageRendererTracker tracker,
			Shell shell,
			ContentPage page,
			ContextOnlyElementHandler shellHandler,
			ContextOnlyElementHandler pageHandler)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<TestShellPageRendererTracker>(tracker),
				new WeakReference<Shell>(shell),
				new WeakReference<ContentPage>(page),
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
		Current.AliveTrackers <= 1 &&
		Current.AliveShellHandlers == 0 &&
		Current.AlivePageHandlers <= 1;

	public string ToText()
	{
		var currentMiB = Current.EstimatedAssignedTitleBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedAssignedTitleBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosShellNavigationItemTitleRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload per native title slot: {PayloadKiBPerTitle} KiB",
			$"Native title slots per cycle: 1",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated retained native title payload: {controlMiB:N1} MiB",
			$"Current estimated retained native title payload: {currentMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var nativeTitleMiB = result.EstimatedAssignedTitleBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native navigation items: {result.RetainedNativeNavigationItems}/{result.TrackedCycles}",
			$"  assigned payload-sized title slots: {result.AssignedPayloadTitleSlots}/{result.TrackedCycles}",
			$"  estimated retained native title bytes: {result.EstimatedAssignedTitleBytes:N0}",
			$"  estimated retained native title MiB: {nativeTitleMiB:N1}",
			$"  alive trackers: {result.AliveTrackers}/{result.TrackedCycles}",
			$"  alive shells: {result.AliveShells}/{result.TrackedCycles}",
			$"  alive pages: {result.AlivePages}/{result.TrackedCycles}",
			$"  alive shell handlers: {result.AliveShellHandlers}/{result.TrackedCycles}",
			$"  alive page handlers: {result.AlivePageHandlers}/{result.TrackedCycles}");
	}
}

internal sealed class TestShellPageRendererTracker : ShellPageRendererTracker
{
	public TestShellPageRendererTracker(IShellContext context)
		: base(context)
	{
	}

	public void UpdateTitleForTest()
	{
		UpdateTitle();
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
