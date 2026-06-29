#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Views;
using AndroidX.AppCompat.App;
using AndroidX.Fragment.App;
using AndroidX.ViewPager2.Widget;
using Google.Android.Material.BottomNavigation;
using Google.Android.Material.Tabs;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers;
using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.AndroidSpecific;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Platform;
using AndroidXFragmentManager = AndroidX.Fragment.App.FragmentManager;
using ControlsTabbedPage = Microsoft.Maui.Controls.TabbedPage;

namespace AndroidTabbedPageTabTitleRetentionRepro;

internal static class ReproSession
{
	const int CyclesPerPlacement = 128;
	const int TabsPerPage = 4;
	const int PayloadCharsPerTitle = 8 * 1024;
	const int PayloadBytesPerTitle = PayloadCharsPerTitle * sizeof(char);

	static readonly FieldInfo RootViewChangedField =
		typeof(NavigationRootManager).GetField("RootViewChanged", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(NavigationRootManager).FullName, "RootViewChanged");

	static readonly FieldInfo PreviousPageField =
		typeof(TabbedPageManager).GetField("previousPage", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(TabbedPageManager).FullName, "previousPage");

	static readonly FieldInfo ListenersField =
		typeof(TabbedPageManager).GetField("_listeners", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(TabbedPageManager).FullName, "_listeners");

	static readonly FieldInfo TabLayoutMediatorField =
		typeof(TabbedPageManager).GetField("_tabLayoutMediator", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(TabbedPageManager).FullName, "_tabLayoutMediator");

	static readonly List<TabLayout> RetainedTopTabLayouts = new();
	static readonly List<BottomNavigationView> RetainedBottomNavigationViews = new();

	public static async Task<ReproReport> RunAsync(AppCompatActivity activity)
	{
		RetainedTopTabLayouts.Clear();
		RetainedBottomNavigationViews.Clear();

		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			activity,
			"control: clear native TabLayout/MenuItem title slots after disconnect",
			clearNativeTitleSlots: true);

		var current = await RunScenarioAsync(
			activity,
			"current: SetElement(null) leaves native TabLayout/MenuItem title slots assigned",
			clearNativeTitleSlots: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedTopTabLayouts);
		GC.KeepAlive(RetainedBottomNavigationViews);

		return new ReproReport(
			CyclesPerPlacement,
			TabsPerPage,
			PayloadCharsPerTitle,
			PayloadBytesPerTitle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		AppCompatActivity activity,
		string name,
		bool clearNativeTitleSlots)
	{
		var tracked = new List<TrackedCycle>(CyclesPerPlacement * 2);

		for (var i = 0; i < CyclesPerPlacement; i++)
		{
			CreateCycle(activity, TabPlacement.Top, i, tracked, clearNativeTitleSlots);
			CreateCycle(activity, TabPlacement.Bottom, i, tracked, clearNativeTitleSlots);

			if (i % 16 == 0)
				await Task.Yield();
		}

		await Task.Delay(250);
		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateCycle(
		AppCompatActivity activity,
		TabPlacement placement,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeTitleSlots)
	{
		var services = new ReproServiceProvider(activity);
		var mauiContext = new MauiContext(services, activity);
		var rootManager = new NavigationRootManager(mauiContext);
		services.RootManager = rootManager;

		var manager = new TabbedPageManager(mauiContext);
		var tabbedPage = new ControlsTabbedPage
		{
			Title = $"Transient workspace {placement} {cycle + 1:000}",
			TabbedPageManager = manager
		};

		if (placement == TabPlacement.Bottom)
		{
			tabbedPage.On<Microsoft.Maui.Controls.PlatformConfiguration.Android>()
				.SetToolbarPlacement(ToolbarPlacement.Bottom);
		}

		var pages = new List<ContentPage>(TabsPerPage);
		for (var tab = 0; tab < TabsPerPage; tab++)
		{
			var page = new ContentPage
			{
				Title = CreateTitlePayload(placement, cycle, tab),
				Content = new Label
				{
					Text = $"Orders queue {cycle + 1:000}-{tab + 1:00}",
					AutomationId = $"orders-label-{placement}-{cycle + 1:000}-{tab + 1:00}"
				}
			};

			pages.Add(page);
			tabbedPage.Children.Add(page);
		}

		tabbedPage.CurrentPage = pages[0];

		var handler = new FakeElementHandler(mauiContext);
		handler.SetVirtualView(tabbedPage);
		tabbedPage.Handler = handler;

		manager.SetElement(tabbedPage);

		var topTabs = placement == TabPlacement.Top ? manager.TabLayout : null;
		var bottomTabs = placement == TabPlacement.Bottom ? manager.BottomNavigationView : null;

		manager.SetElement(null!);
		tabbedPage.Handler = null!;
		handler.DisconnectHandler();

		NeutralizeManagedRoots(rootManager, manager, topTabs, bottomTabs);

		foreach (var page in pages)
			page.Title = null;
		tabbedPage.Children.Clear();

		if (clearNativeTitleSlots)
			ClearNativeTitleSlots(topTabs, bottomTabs);

		if (topTabs is not null)
			RetainedTopTabLayouts.Add(topTabs);
		if (bottomTabs is not null)
			RetainedBottomNavigationViews.Add(bottomTabs);

		tracked.Add(TrackedCycle.Create(
			placement,
			topTabs,
			bottomTabs,
			tabbedPage,
			manager,
			handler,
			pages));

		rootManager = null!;
		mauiContext = null!;
		manager = null!;
		tabbedPage = null!;
		handler = null!;
		services = null!;
		pages = null!;
	}

	static void NeutralizeManagedRoots(
		NavigationRootManager rootManager,
		TabbedPageManager manager,
		TabLayout? topTabs,
		BottomNavigationView? bottomTabs)
	{
		RootViewChangedField.SetValue(rootManager, null);
		PreviousPageField.SetValue(manager, null);

		if (TabLayoutMediatorField.GetValue(manager) is TabLayoutMediator mediator)
		{
			mediator.Detach();
			TabLayoutMediatorField.SetValue(manager, null);
		}

		if (ListenersField.GetValue(manager) is ViewPager2.OnPageChangeCallback pageChangeCallback)
			manager.ViewPager.UnregisterOnPageChangeCallback(pageChangeCallback);

		topTabs?.ClearOnTabSelectedListeners();
		bottomTabs?.SetOnItemSelectedListener(null);
		manager.ViewPager.Adapter = null;
	}

	static void ClearNativeTitleSlots(TabLayout? topTabs, BottomNavigationView? bottomTabs)
	{
		if (topTabs is not null)
		{
			for (var i = 0; i < topTabs.TabCount; i++)
				topTabs.GetTabAt(i)?.SetText(string.Empty);
		}

		if (bottomTabs is not null)
		{
			var menu = bottomTabs.Menu;
			for (var i = 0; i < menu.Size(); i++)
			{
				if (menu.GetItem(i) is { } item)
					item.SetTitle(string.Empty);
			}
		}
	}

	static TitleSnapshot CaptureTopTitleSlots(TabLayout? topTabs)
	{
		if (topTabs is null)
			return TitleSnapshot.Empty;

		var assigned = 0;
		var payload = 0;
		long bytes = 0;

		for (var i = 0; i < topTabs.TabCount; i++)
		{
			var text = topTabs.GetTabAt(i)?.Text?.ToString();
			Accumulate(text, ref assigned, ref payload, ref bytes);
		}

		return new TitleSnapshot(assigned, payload, bytes);
	}

	static TitleSnapshot CaptureBottomTitleSlots(BottomNavigationView? bottomTabs)
	{
		if (bottomTabs is null)
			return TitleSnapshot.Empty;

		var assigned = 0;
		var payload = 0;
		long bytes = 0;
		var menu = bottomTabs.Menu;

		for (var i = 0; i < menu.Size(); i++)
		{
			var text = menu.GetItem(i)?.TitleFormatted?.ToString();
			Accumulate(text, ref assigned, ref payload, ref bytes);
		}

		return new TitleSnapshot(assigned, payload, bytes);
	}

	static void Accumulate(string? text, ref int assigned, ref int payload, ref long bytes)
	{
		if (string.IsNullOrEmpty(text))
			return;

		assigned++;
		bytes += (long)text.Length * sizeof(char);

		if (text.StartsWith("android-tabbedpage-tab-title-", StringComparison.Ordinal) &&
			text.Length >= PayloadCharsPerTitle)
		{
			payload++;
		}
	}

	static string CreateTitlePayload(TabPlacement placement, int cycle, int tab)
	{
		var prefix = $"android-tabbedpage-tab-title-{placement.ToString().ToLowerInvariant()}-{cycle:D4}-{tab:D2}-";
		return prefix + new string((char)('A' + ((cycle + tab) % 26)), PayloadCharsPerTitle - prefix.Length);
	}

	static int CountAlive<T>(IEnumerable<WeakReference<T>> references)
		where T : class
	{
		var count = 0;
		foreach (var reference in references)
		{
			if (reference.TryGetTarget(out _))
				count++;
		}

		return count;
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Java.Lang.JavaSystem.Gc();
			Thread.Sleep(100);
		}
	}

	sealed class ReproServiceProvider : IServiceProvider
	{
		readonly AppCompatActivity _activity;

		public ReproServiceProvider(AppCompatActivity activity)
		{
			_activity = activity;
		}

		public NavigationRootManager? RootManager { get; set; }

		public object? GetService(Type serviceType)
		{
			if (serviceType == typeof(NavigationRootManager))
				return RootManager;
			if (serviceType == typeof(Activity))
				return _activity;
			if (serviceType == typeof(Context))
				return _activity;
			if (serviceType == typeof(LayoutInflater))
				return LayoutInflater.From(_activity);
			if (serviceType == typeof(AndroidXFragmentManager))
				return _activity.SupportFragmentManager;

			return null;
		}
	}

	internal sealed class FakeElementHandler : IViewHandler
	{
		public FakeElementHandler(IMauiContext mauiContext)
		{
			MauiContext = mauiContext;
		}

		public object? PlatformView => null;

		public IView? VirtualView { get; private set; }

		IElement? IElementHandler.VirtualView => VirtualView;

		public IMauiContext? MauiContext { get; private set; }

		public void SetMauiContext(IMauiContext mauiContext)
		{
			MauiContext = mauiContext;
		}

		public bool HasContainer { get; set; }

		public object? ContainerView => null;

		public void SetVirtualView(IElement view)
		{
			VirtualView = (IView)view;
		}

		public void UpdateValue(string property)
		{
		}

		public void Invoke(string command, object? args = null)
		{
		}

		public void DisconnectHandler()
		{
			VirtualView = null;
			MauiContext = null;
		}

		public Size GetDesiredSize(double widthConstraint, double heightConstraint) => Size.Zero;

		public void PlatformArrange(Rect frame)
		{
		}
	}

	internal enum TabPlacement
	{
		Top,
		Bottom
	}

	internal sealed record TitleSnapshot(int AssignedSlots, int PayloadSlots, long RetainedBytes)
	{
		public static TitleSnapshot Empty { get; } = new(0, 0, 0);
	}

	internal sealed record TrackedCycle(
		TabPlacement Placement,
		WeakReference<TabLayout>? TopTabs,
		WeakReference<BottomNavigationView>? BottomTabs,
		WeakReference<ControlsTabbedPage> TabbedPage,
		WeakReference<TabbedPageManager> Manager,
		WeakReference<FakeElementHandler> Handler,
		IReadOnlyList<WeakReference<ContentPage>> Pages)
	{
		public static TrackedCycle Create(
			TabPlacement placement,
			TabLayout? topTabs,
			BottomNavigationView? bottomTabs,
			ControlsTabbedPage tabbedPage,
			TabbedPageManager manager,
			FakeElementHandler handler,
			IReadOnlyList<ContentPage> pages)
		{
			return new TrackedCycle(
				placement,
				topTabs is null ? null : new WeakReference<TabLayout>(topTabs),
				bottomTabs is null ? null : new WeakReference<BottomNavigationView>(bottomTabs),
				new WeakReference<ControlsTabbedPage>(tabbedPage),
				new WeakReference<TabbedPageManager>(manager),
				new WeakReference<FakeElementHandler>(handler),
				pages.Select(static page => new WeakReference<ContentPage>(page)).ToArray());
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveTopTabLayouts,
		int AliveBottomNavigationViews,
		int AliveTabbedPages,
		int AliveManagers,
		int AliveHandlers,
		int AliveChildPages,
		int AssignedTopTitleSlots,
		int PayloadTopTitleSlots,
		int AssignedBottomTitleSlots,
		int PayloadBottomTitleSlots,
		long RetainedNativeTitleBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var topLayoutRefs = new List<WeakReference<TabLayout>>();
			var bottomViewRefs = new List<WeakReference<BottomNavigationView>>();
			var tabbedPageRefs = new List<WeakReference<ControlsTabbedPage>>();
			var managerRefs = new List<WeakReference<TabbedPageManager>>();
			var handlerRefs = new List<WeakReference<FakeElementHandler>>();
			var pageRefs = new List<WeakReference<ContentPage>>();

			var assignedTop = 0;
			var payloadTop = 0;
			var assignedBottom = 0;
			var payloadBottom = 0;
			long retainedBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.TopTabs is not null)
				{
					topLayoutRefs.Add(cycle.TopTabs);
					if (cycle.TopTabs.TryGetTarget(out var topTabs))
					{
						var snapshot = CaptureTopTitleSlots(topTabs);
						assignedTop += snapshot.AssignedSlots;
						payloadTop += snapshot.PayloadSlots;
						retainedBytes += snapshot.RetainedBytes;
					}
				}

				if (cycle.BottomTabs is not null)
				{
					bottomViewRefs.Add(cycle.BottomTabs);
					if (cycle.BottomTabs.TryGetTarget(out var bottomTabs))
					{
						var snapshot = CaptureBottomTitleSlots(bottomTabs);
						assignedBottom += snapshot.AssignedSlots;
						payloadBottom += snapshot.PayloadSlots;
						retainedBytes += snapshot.RetainedBytes;
					}
				}

				tabbedPageRefs.Add(cycle.TabbedPage);
				managerRefs.Add(cycle.Manager);
				handlerRefs.Add(cycle.Handler);
				pageRefs.AddRange(cycle.Pages);
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				CountAlive(topLayoutRefs),
				CountAlive(bottomViewRefs),
				CountAlive(tabbedPageRefs),
				CountAlive(managerRefs),
				CountAlive(handlerRefs),
				CountAlive(pageRefs),
				assignedTop,
				payloadTop,
				assignedBottom,
				payloadBottom,
				retainedBytes);
		}
	}
}

internal sealed record ReproReport(
	int CyclesPerPlacement,
	int TabsPerPage,
	int PayloadCharsPerTitle,
	int PayloadBytesPerTitle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public int ExpectedTopPayloadSlots => CyclesPerPlacement * TabsPerPage;

	public int ExpectedBottomPayloadSlots => CyclesPerPlacement * TabsPerPage;

	public bool LeakProved =>
		Control.AliveTopTabLayouts == CyclesPerPlacement &&
		Control.AliveBottomNavigationViews == CyclesPerPlacement &&
		Current.AliveTopTabLayouts == CyclesPerPlacement &&
		Current.AliveBottomNavigationViews == CyclesPerPlacement &&
		Control.PayloadTopTitleSlots == 0 &&
		Control.PayloadBottomTitleSlots == 0 &&
		Current.PayloadTopTitleSlots >= ExpectedTopPayloadSlots &&
		Current.PayloadBottomTitleSlots >= ExpectedBottomPayloadSlots &&
		Current.AliveTabbedPages == 0 &&
		Current.AliveManagers == 0 &&
		Current.AliveHandlers == 0 &&
		Current.AliveChildPages == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidTabbedPageTabTitleRetentionRepro",
			$"Cycles per placement: {CyclesPerPlacement}",
			$"Tabs per transient TabbedPage: {TabsPerPage}",
			$"Payload chars per native tab title slot: {PayloadCharsPerTitle}",
			$"Payload bytes per native tab title slot: {PayloadBytesPerTitle}",
			$"Expected top-tab payload slots: {ExpectedTopPayloadSlots}",
			$"Expected bottom-tab payload slots: {ExpectedBottomPayloadSlots}",
			$"Managed callback neutralization: RootViewChanged, previousPage, TabLayoutMediator, tab listeners, bottom item listener, and ViewPager callback cleared in both runs",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained native tab title payload: {FormatBytes(Control.RetainedNativeTitleBytes)}",
			$"Current retained native tab title payload: {FormatBytes(Current.RetainedNativeTitleBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked native tab cycles: {result.TrackedCycles}",
			$"  alive native TabLayout peers: {result.AliveTopTabLayouts}/{result.TrackedCycles / 2}",
			$"  alive native BottomNavigationView peers: {result.AliveBottomNavigationViews}/{result.TrackedCycles / 2}",
			$"  alive TabbedPage roots: {result.AliveTabbedPages}/{result.TrackedCycles}",
			$"  alive TabbedPageManager instances: {result.AliveManagers}/{result.TrackedCycles}",
			$"  alive fake handlers: {result.AliveHandlers}/{result.TrackedCycles}",
			$"  alive child pages: {result.AliveChildPages}/{result.TrackedCycles * 4}",
			$"  assigned top-tab title slots: {result.AssignedTopTitleSlots}",
			$"  payload-sized top-tab title slots: {result.PayloadTopTitleSlots}",
			$"  assigned bottom-tab title slots: {result.AssignedBottomTitleSlots}",
			$"  payload-sized bottom-tab title slots: {result.PayloadBottomTitleSlots}",
			$"  retained native title bytes: {result.RetainedNativeTitleBytes:N0}");
	}

	static string FormatBytes(long bytes)
	{
		if (bytes >= 1024 * 1024)
			return $"{bytes / 1024d / 1024d:N1} MiB";
		if (bytes >= 1024)
			return $"{bytes / 1024d:N1} KiB";
		return $"{bytes:N0} B";
	}
}
