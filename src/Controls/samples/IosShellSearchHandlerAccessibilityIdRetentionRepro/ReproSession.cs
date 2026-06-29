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

namespace IosShellSearchHandlerAccessibilityIdRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 1024;
	internal const int PayloadKiBPerIdentifierSlot = 16;

	const long PayloadBytesPerIdentifierSlot = PayloadKiBPerIdentifierSlot * 1024L;

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");

	static readonly List<IReadOnlyList<RetainedNativeSearchBar>> RetainedNativeSearchBars = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-shell-searchhandler-accessibilityid-retention-results.txt");

	public static int TotalIdentifierSlots => Cycles;

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS Shell SearchHandler accessibility identifier retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear Shell UISearchBar accessibility identifier",
			context,
			clearNativeIdentifier: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: ShellPageRendererTracker leaves UISearchBar accessibility identifier assigned",
			context,
			clearNativeIdentifier: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeSearchBars);

		return new ReproReport(
			Cycles,
			PayloadKiBPerIdentifierSlot,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearNativeIdentifier)
	{
		var retainedSearchBars = new List<RetainedNativeSearchBar>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 16 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var cycleResult = await RunCycleAsync(i, context, clearNativeIdentifier);
			retainedSearchBars.Add(cycleResult.RetainedSearchBar);
			tracked.Add(cycleResult.Tracked);
		}

		RetainedNativeSearchBars.Add(retainedSearchBars);
		ForceFullGc();

		return ScenarioResult.From(name, retainedSearchBars, tracked);
	}

	static async Task<CycleResult> RunCycleAsync(
		int cycle,
		IMauiContext context,
		bool clearNativeIdentifier)
	{
		var page = new ContentPage
		{
			Title = $"Orders {cycle:0000}",
			Content = new Label { Text = $"Orders {cycle:0000}" }
		};
		var searchHandler = new SearchHandler
		{
			AutomationId = CreateIdentifierPayload(cycle),
			Placeholder = "Search orders",
			Query = "status:open",
			SearchBoxVisibility = SearchBoxVisibility.Expanded
		};
		Shell.SetSearchHandler(page, searchHandler);

		var shell = CreateShell(page);
		var shellHandler = new ContextOnlyElementHandler(context);
		var pageHandler = new ContextOnlyElementHandler(context);
		shellHandler.SetVirtualView(shell);
		pageHandler.SetVirtualView(page);
		shell.Handler = shellHandler;
		page.Handler = pageHandler;

		var shellContext = new FakeShellContext(shell);
		var viewController = new UIViewController();
		var tracker = new TestShellPageRendererTracker(shellContext)
		{
			ViewController = viewController,
			Page = page,
			IsRootPage = true
		};

		tracker.SetSearchHandlerForTest(searchHandler);
		await DrainMainQueueAsync();

		var searchBar = tracker.SearchBarForTest
			?? throw new InvalidOperationException("ShellPageRendererTracker did not create a UISearchBar.");

		if (CountAssignedPayloadIdentifierSlots(searchBar) != 1)
			throw new InvalidOperationException("ShellPageRendererTracker did not assign a payload-sized native accessibility identifier.");

		// Keep this proof focused on the accessibility identifier, not the adjacent C229 text/event leak.
		ClearNativeText(searchBar);
		searchHandler.ClearValue(SearchHandler.AutomationIdProperty);

		if (clearNativeIdentifier)
			ClearNativeIdentifier(searchBar);

		var retainedSearchBar = RetainNativeSearchBar(searchBar);

		page.ClearValue(Shell.SearchHandlerProperty);
		tracker.SetSearchHandlerForTest(null);
		tracker.RemoveSearchButtonClickedHandlerForTest(searchBar);
		tracker.Dispose();
		viewController.Dispose();

		shellHandler.DisconnectHandler();
		pageHandler.DisconnectHandler();
		await DrainMainQueueAsync();

		return new CycleResult(
			retainedSearchBar,
			TrackedCycle.Create(cycle, tracker, shell, page, searchHandler, shellHandler, pageHandler));
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

	static string CreateIdentifierPayload(int cycle)
	{
		var header = $"cycle-{cycle:0000}-shell-searchhandler-route-";
		var sentence = "generated-search-workspace-automation-id-workflow-context-command-confirmation-";
		var targetChars = (int)(PayloadBytesPerIdentifierSlot / 2);
		var builder = new StringBuilder(targetChars + sentence.Length);
		builder.Append(header);

		while (builder.Length < targetChars)
			builder.Append(sentence);

		if (builder.Length > targetChars)
			builder.Length = targetChars;

		return builder.ToString();
	}

	static void ClearNativeIdentifier(UISearchBar searchBar)
	{
		searchBar.AccessibilityIdentifier = null;
	}

	static void ClearNativeText(UISearchBar searchBar)
	{
		searchBar.Text = string.Empty;
		searchBar.Placeholder = string.Empty;

		var textField = TryGetSearchTextField(searchBar);
		if (textField is not null)
		{
			textField.Text = string.Empty;
			textField.Placeholder = string.Empty;
			textField.AttributedPlaceholder = null;
		}
	}

	static int CountAssignedPayloadIdentifierSlots(UISearchBar searchBar) =>
		EstimateTextBytes(searchBar.AccessibilityIdentifier) >= PayloadBytesPerIdentifierSlot * 0.95 ? 1 : 0;

	static long EstimateAssignedIdentifierBytes(UISearchBar searchBar)
	{
		var bytes = EstimateTextBytes(searchBar.AccessibilityIdentifier);
		return bytes >= PayloadBytesPerIdentifierSlot * 0.95 ? Math.Min(bytes, PayloadBytesPerIdentifierSlot) : 0;
	}

	static long EstimatePlaceholderBytes(UISearchBar searchBar)
	{
		var bytes = EstimateTextBytes(searchBar.Placeholder);
		var textField = TryGetSearchTextField(searchBar);
		if (textField?.AttributedPlaceholder?.Value is string attributedPlaceholder)
			bytes = Math.Max(bytes, EstimateTextBytes(attributedPlaceholder));
		else if (textField?.Placeholder is string placeholder)
			bytes = Math.Max(bytes, EstimateTextBytes(placeholder));

		return bytes;
	}

	static long EstimateTextBytes(string? text)
	{
		return string.IsNullOrEmpty(text) ? 0 : text.Length * 2L;
	}

	static UITextField? TryGetSearchTextField(UISearchBar searchBar)
	{
		try
		{
			return searchBar.ValueForKey(new NSString("searchField")) as UITextField;
		}
		catch
		{
			return null;
		}
	}

	static RetainedNativeSearchBar RetainNativeSearchBar(UISearchBar searchBar)
	{
		var handle = searchBar.Handle;
		if (handle == IntPtr.Zero)
			throw new InvalidOperationException("Cannot retain a native UISearchBar with a zero handle.");

		var retained = IntPtr_objc_msgSend(handle, RetainSelector);
		if (retained == IntPtr.Zero)
			throw new InvalidOperationException("Objective-C retain returned a zero handle.");

		return new RetainedNativeSearchBar(retained);
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

	internal sealed record CycleResult(RetainedNativeSearchBar RetainedSearchBar, TrackedCycle Tracked);

	internal sealed class RetainedNativeSearchBar
	{
		public RetainedNativeSearchBar(IntPtr handle)
		{
			Handle = handle;
		}

		public IntPtr Handle { get; }

		public UISearchBar? TryGetSearchBar()
		{
			if (Handle == IntPtr.Zero)
				return null;

			try
			{
				return Runtime.GetNSObject<UISearchBar>(Handle, false);
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
		WeakReference<SearchHandler> SearchHandler,
		WeakReference<ContextOnlyElementHandler> ShellHandler,
		WeakReference<ContextOnlyElementHandler> PageHandler)
	{
		public static TrackedCycle Create(
			int cycle,
			TestShellPageRendererTracker tracker,
			Shell shell,
			ContentPage page,
			SearchHandler searchHandler,
			ContextOnlyElementHandler shellHandler,
			ContextOnlyElementHandler pageHandler)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<TestShellPageRendererTracker>(tracker),
				new WeakReference<Shell>(shell),
				new WeakReference<ContentPage>(page),
				new WeakReference<SearchHandler>(searchHandler),
				new WeakReference<ContextOnlyElementHandler>(shellHandler),
				new WeakReference<ContextOnlyElementHandler>(pageHandler));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativeSearchBars,
		int AssignedPayloadIdentifierSlots,
		long EstimatedAssignedIdentifierBytes,
		int AliveTrackers,
		int AliveShells,
		int AlivePages,
		int AliveSearchHandlers,
		int AliveShellHandlers,
		int AlivePageHandlers)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedNativeSearchBar> retainedSearchBars,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var retainedNativeSearchBars = 0;
			var assignedPayloadIdentifierSlots = 0;
			long estimatedAssignedIdentifierBytes = 0;

			foreach (var retainedSearchBar in retainedSearchBars)
			{
				var searchBar = retainedSearchBar.TryGetSearchBar();
				if (searchBar is null)
					continue;

				retainedNativeSearchBars++;
				assignedPayloadIdentifierSlots += CountAssignedPayloadIdentifierSlots(searchBar);
				estimatedAssignedIdentifierBytes += EstimateAssignedIdentifierBytes(searchBar);
			}

			var aliveTrackers = 0;
			var aliveShells = 0;
			var alivePages = 0;
			var aliveSearchHandlers = 0;
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

				if (cycle.SearchHandler.TryGetTarget(out _))
					aliveSearchHandlers++;

				if (cycle.ShellHandler.TryGetTarget(out _))
					aliveShellHandlers++;

				if (cycle.PageHandler.TryGetTarget(out _))
					alivePageHandlers++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedNativeSearchBars,
				assignedPayloadIdentifierSlots,
				estimatedAssignedIdentifierBytes,
				aliveTrackers,
				aliveShells,
				alivePages,
				aliveSearchHandlers,
				aliveShellHandlers,
				alivePageHandlers);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadKiBPerIdentifierSlot,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedNativeSearchBars == Cycles &&
		Control.AssignedPayloadIdentifierSlots == 0 &&
		Control.AliveTrackers <= 1 &&
		Current.RetainedNativeSearchBars == Cycles &&
		Current.AssignedPayloadIdentifierSlots == ReproSession.TotalIdentifierSlots &&
		Current.EstimatedAssignedIdentifierBytes >= ReproSession.TotalIdentifierSlots * PayloadKiBPerIdentifierSlot * 1024L * 0.95 &&
		Current.AliveTrackers <= 1 &&
		Current.AliveShells <= 1 &&
		Current.AlivePages <= 1 &&
		Current.AliveSearchHandlers <= 1 &&
		Current.AliveShellHandlers == 0 &&
		Current.AlivePageHandlers <= 1;

	public string ToText()
	{
		var currentMiB = Current.EstimatedAssignedIdentifierBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedAssignedIdentifierBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosShellSearchHandlerAccessibilityIdRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload per native accessibility identifier slot: {PayloadKiBPerIdentifierSlot} KiB",
			$"Native accessibility identifier slots per cycle: 1",
			$"Total native accessibility identifier slots: {ReproSession.TotalIdentifierSlots}",
			"Note: the sibling search text and SearchButtonClicked event paths are cleared in both runs; this repro isolates the extra native AccessibilityIdentifier payload.",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated retained native search accessibility identifier payload: {controlMiB:N1} MiB",
			$"Current estimated retained native search accessibility identifier payload: {currentMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var nativeIdentifierMiB = result.EstimatedAssignedIdentifierBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native search bars: {result.RetainedNativeSearchBars}/{result.TrackedCycles}",
			$"  assigned payload-sized accessibility identifier slots: {result.AssignedPayloadIdentifierSlots}/{ReproSession.TotalIdentifierSlots}",
			$"  estimated retained native search accessibility identifier bytes: {result.EstimatedAssignedIdentifierBytes:N0}",
			$"  estimated retained native search accessibility identifier MiB: {nativeIdentifierMiB:N1}",
			$"  alive trackers: {result.AliveTrackers}/{result.TrackedCycles}",
			$"  alive shells: {result.AliveShells}/{result.TrackedCycles}",
			$"  alive pages: {result.AlivePages}/{result.TrackedCycles}",
			$"  alive SearchHandlers: {result.AliveSearchHandlers}/{result.TrackedCycles}",
			$"  alive shell handlers: {result.AliveShellHandlers}/{result.TrackedCycles}",
			$"  alive page handlers: {result.AlivePageHandlers}/{result.TrackedCycles}");
	}
}

internal sealed class TestShellPageRendererTracker : ShellPageRendererTracker
{
	static readonly PropertyInfo SearchHandlerProperty =
		typeof(ShellPageRendererTracker).GetProperty("SearchHandler", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(typeof(ShellPageRendererTracker).FullName, "SearchHandler");

	static readonly FieldInfo SearchControllerField =
		typeof(ShellPageRendererTracker).GetField("_searchController", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(ShellPageRendererTracker).FullName, "_searchController");

	static readonly MethodInfo SearchButtonClickedMethod =
		typeof(ShellPageRendererTracker).GetMethod("SearchButtonClicked", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(ShellPageRendererTracker).FullName, "SearchButtonClicked");

	public TestShellPageRendererTracker(IShellContext context)
		: base(context)
	{
	}

	public UISearchBar? SearchBarForTest =>
		(SearchControllerField.GetValue(this) as UISearchController)?.SearchBar;

	public void SetSearchHandlerForTest(SearchHandler? searchHandler)
	{
		SearchHandlerProperty.SetValue(this, searchHandler);
	}

	public void RemoveSearchButtonClickedHandlerForTest(UISearchBar searchBar)
	{
		var handler = (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), this, SearchButtonClickedMethod);
		searchBar.SearchButtonClicked -= handler;
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
