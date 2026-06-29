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
using Android.Graphics;
using Android.Graphics.Drawables;
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
using AColor = Android.Graphics.Color;
using AndroidXFragmentManager = AndroidX.Fragment.App.FragmentManager;
using ControlsTabbedPage = Microsoft.Maui.Controls.TabbedPage;

namespace AndroidTabbedPageTabIconRetentionRepro;

internal static class ReproSession
{
	const int CyclesPerPlacement = 24;
	const int TabsPerPage = 4;
	internal const int IconWidth = 512;
	internal const int IconHeight = 512;
	const int BytesPerPixel = 4;
	const int PayloadBytesPerIcon = IconWidth * IconHeight * BytesPerPixel;

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
			"control: clear native TabLayout/MenuItem icon slots after disconnect",
			clearNativeIconSlots: true);

		var current = await RunScenarioAsync(
			activity,
			"current: SetElement(null) leaves native TabLayout/MenuItem icon slots assigned",
			clearNativeIconSlots: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedTopTabLayouts);
		GC.KeepAlive(RetainedBottomNavigationViews);

		return new ReproReport(
			CyclesPerPlacement,
			TabsPerPage,
			IconWidth,
			IconHeight,
			PayloadBytesPerIcon,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		AppCompatActivity activity,
		string name,
		bool clearNativeIconSlots)
	{
		var tracked = new List<TrackedCycle>(CyclesPerPlacement * 2);

		for (var i = 0; i < CyclesPerPlacement; i++)
		{
			await CreateCycleAsync(activity, TabPlacement.Top, i, tracked, clearNativeIconSlots);
			await CreateCycleAsync(activity, TabPlacement.Bottom, i, tracked, clearNativeIconSlots);

			if (i % 8 == 0)
				await Task.Yield();
		}

		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static async Task CreateCycleAsync(
		AppCompatActivity activity,
		TabPlacement placement,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeIconSlots)
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
		var iconSources = new List<PayloadImageSource>(TabsPerPage);
		for (var tab = 0; tab < TabsPerPage; tab++)
		{
			var iconSource = new PayloadImageSource(placement, cycle, tab);
			var page = new ContentPage
			{
				Title = $"Orders {placement} {cycle + 1:000}-{tab + 1:00}",
				IconImageSource = iconSource,
				Content = new Label
				{
					Text = $"Orders queue {cycle + 1:000}-{tab + 1:00}",
					AutomationId = $"orders-label-{placement}-{cycle + 1:000}-{tab + 1:00}"
				}
			};

			iconSources.Add(iconSource);
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
		await WaitForIconSlotsAsync(placement, topTabs, bottomTabs);

		manager.SetElement(null!);
		tabbedPage.Handler = null!;
		handler.DisconnectHandler();

		NeutralizeManagedRoots(rootManager, manager, topTabs, bottomTabs);

		foreach (var page in pages)
		{
			page.Title = null;
			page.IconImageSource = null;
			page.Content = null;
		}

		tabbedPage.Children.Clear();

		if (clearNativeIconSlots)
			ClearNativeIconSlots(topTabs, bottomTabs);

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
			pages,
			iconSources));

		rootManager = null!;
		mauiContext = null!;
		manager = null!;
		tabbedPage = null!;
		handler = null!;
		services = null!;
		pages = null!;
		iconSources = null!;
	}

	static async Task WaitForIconSlotsAsync(
		TabPlacement placement,
		TabLayout? topTabs,
		BottomNavigationView? bottomTabs)
	{
		IconSnapshot snapshot = IconSnapshot.Empty;

		for (var i = 0; i < 80; i++)
		{
			snapshot = placement == TabPlacement.Top
				? CaptureTopIconSlots(topTabs)
				: CaptureBottomIconSlots(bottomTabs);

			if (snapshot.PayloadSlots >= TabsPerPage)
				return;

			await Task.Delay(25);
		}

		throw new InvalidOperationException(
			$"Expected {TabsPerPage} payload icon slots for {placement}, saw {snapshot.PayloadSlots}.");
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

	static void ClearNativeIconSlots(TabLayout? topTabs, BottomNavigationView? bottomTabs)
	{
		if (topTabs is not null)
		{
			for (var i = 0; i < topTabs.TabCount; i++)
				topTabs.GetTabAt(i)?.SetIcon((Drawable?)null);
		}

		if (bottomTabs is not null)
		{
			var menu = bottomTabs.Menu;
			for (var i = 0; i < menu.Size(); i++)
				menu.GetItem(i)?.SetIcon((Drawable?)null);
		}
	}

	static IconSnapshot CaptureTopIconSlots(TabLayout? topTabs)
	{
		if (topTabs is null)
			return IconSnapshot.Empty;

		var assigned = 0;
		var payload = 0;
		long bytes = 0;

		for (var i = 0; i < topTabs.TabCount; i++)
			AccumulateIcon(topTabs.GetTabAt(i)?.Icon, ref assigned, ref payload, ref bytes);

		return new IconSnapshot(assigned, payload, bytes);
	}

	static IconSnapshot CaptureBottomIconSlots(BottomNavigationView? bottomTabs)
	{
		if (bottomTabs is null)
			return IconSnapshot.Empty;

		var assigned = 0;
		var payload = 0;
		long bytes = 0;
		var menu = bottomTabs.Menu;

		for (var i = 0; i < menu.Size(); i++)
			AccumulateIcon(menu.GetItem(i)?.Icon, ref assigned, ref payload, ref bytes);

		return new IconSnapshot(assigned, payload, bytes);
	}

	static void AccumulateIcon(Drawable? icon, ref int assigned, ref int payload, ref long bytes)
	{
		var iconBytes = GetDrawableByteCount(icon);

		if (iconBytes <= 0)
			return;

		assigned++;
		bytes += iconBytes;

		if (iconBytes >= PayloadBytesPerIcon)
			payload++;
	}

	static long GetDrawableByteCount(Drawable? drawable)
	{
		if (drawable is BitmapDrawable bitmapDrawable && bitmapDrawable.Bitmap is Bitmap bitmap)
			return bitmap.AllocationByteCount;

		return drawable is null ? 0 : PayloadBytesPerIcon;
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

	sealed class ReproServiceProvider : IServiceProvider, IImageSourceServiceProvider
	{
		readonly AppCompatActivity _activity;
		readonly PayloadImageSourceService _imageSourceService = new();

		public ReproServiceProvider(AppCompatActivity activity)
		{
			_activity = activity;
		}

		public NavigationRootManager? RootManager { get; set; }

		public IServiceProvider HostServiceProvider => this;

		public IImageSourceService? GetImageSourceService(Type imageSource) =>
			imageSource == typeof(PayloadImageSource)
				? _imageSourceService
				: null;

		public object? GetService(Type serviceType)
		{
			if (serviceType == typeof(IImageSourceServiceProvider))
				return this;
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

		public void PlatformArrange(Microsoft.Maui.Graphics.Rect frame)
		{
		}
	}

	internal enum TabPlacement
	{
		Top,
		Bottom
	}

	internal sealed record IconSnapshot(int AssignedSlots, int PayloadSlots, long RetainedBytes)
	{
		public static IconSnapshot Empty { get; } = new(0, 0, 0);
	}

	internal sealed record TrackedCycle(
		TabPlacement Placement,
		WeakReference<TabLayout>? TopTabs,
		WeakReference<BottomNavigationView>? BottomTabs,
		WeakReference<ControlsTabbedPage> TabbedPage,
		WeakReference<TabbedPageManager> Manager,
		WeakReference<FakeElementHandler> Handler,
		IReadOnlyList<WeakReference<ContentPage>> Pages,
		IReadOnlyList<WeakReference<PayloadImageSource>> IconSources)
	{
		public static TrackedCycle Create(
			TabPlacement placement,
			TabLayout? topTabs,
			BottomNavigationView? bottomTabs,
			ControlsTabbedPage tabbedPage,
			TabbedPageManager manager,
			FakeElementHandler handler,
			IReadOnlyList<ContentPage> pages,
			IReadOnlyList<PayloadImageSource> iconSources)
		{
			return new TrackedCycle(
				placement,
				topTabs is null ? null : new WeakReference<TabLayout>(topTabs),
				bottomTabs is null ? null : new WeakReference<BottomNavigationView>(bottomTabs),
				new WeakReference<ControlsTabbedPage>(tabbedPage),
				new WeakReference<TabbedPageManager>(manager),
				new WeakReference<FakeElementHandler>(handler),
				pages.Select(static page => new WeakReference<ContentPage>(page)).ToArray(),
				iconSources.Select(static iconSource => new WeakReference<PayloadImageSource>(iconSource)).ToArray());
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
		int AliveIconSources,
		int AssignedTopIconSlots,
		int PayloadTopIconSlots,
		int AssignedBottomIconSlots,
		int PayloadBottomIconSlots,
		long RetainedNativeIconBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var topLayoutRefs = new List<WeakReference<TabLayout>>();
			var bottomViewRefs = new List<WeakReference<BottomNavigationView>>();
			var tabbedPageRefs = new List<WeakReference<ControlsTabbedPage>>();
			var managerRefs = new List<WeakReference<TabbedPageManager>>();
			var handlerRefs = new List<WeakReference<FakeElementHandler>>();
			var pageRefs = new List<WeakReference<ContentPage>>();
			var iconSourceRefs = new List<WeakReference<PayloadImageSource>>();

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
						var snapshot = CaptureTopIconSlots(topTabs);
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
						var snapshot = CaptureBottomIconSlots(bottomTabs);
						assignedBottom += snapshot.AssignedSlots;
						payloadBottom += snapshot.PayloadSlots;
						retainedBytes += snapshot.RetainedBytes;
					}
				}

				tabbedPageRefs.Add(cycle.TabbedPage);
				managerRefs.Add(cycle.Manager);
				handlerRefs.Add(cycle.Handler);
				pageRefs.AddRange(cycle.Pages);
				iconSourceRefs.AddRange(cycle.IconSources);
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
				CountAlive(iconSourceRefs),
				assignedTop,
				payloadTop,
				assignedBottom,
				payloadBottom,
				retainedBytes);
		}
	}
}

internal sealed class PayloadImageSource : ImageSource
{
	public PayloadImageSource(ReproSession.TabPlacement placement, int cycle, int tab)
	{
		Placement = placement;
		Cycle = cycle;
		Tab = tab;
	}

	public ReproSession.TabPlacement Placement { get; }

	public int Cycle { get; }

	public int Tab { get; }
}

internal sealed class PayloadImageSourceService : ImageSourceService, IImageSourceService<PayloadImageSource>
{
	public override Task<IImageSourceServiceResult<Drawable>?> GetDrawableAsync(
		IImageSource imageSource,
		Context context,
		CancellationToken cancellationToken = default)
	{
		if (imageSource is not PayloadImageSource payloadSource)
			return Task.FromResult<IImageSourceServiceResult<Drawable>?>(null);

		var bitmap = Bitmap.CreateBitmap(ReproSession.IconWidth, ReproSession.IconHeight, Bitmap.Config.Argb8888!);
		var placementOffset = payloadSource.Placement == ReproSession.TabPlacement.Top ? 41 : 157;
		var color = AColor.Argb(
			255,
			(placementOffset + payloadSource.Cycle * 37 + payloadSource.Tab * 13) % 255,
			(placementOffset + payloadSource.Cycle * 67 + payloadSource.Tab * 17) % 255,
			(placementOffset + payloadSource.Cycle * 97 + payloadSource.Tab * 19) % 255);
		bitmap.EraseColor(color);

		Drawable drawable = new BitmapDrawable(context.Resources, bitmap);
		return Task.FromResult<IImageSourceServiceResult<Drawable>?>(new ImageSourceServiceResult(drawable));
	}
}

internal sealed record ReproReport(
	int CyclesPerPlacement,
	int TabsPerPage,
	int IconWidth,
	int IconHeight,
	int PayloadBytesPerIcon,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public int ExpectedTopPayloadSlots => CyclesPerPlacement * TabsPerPage;

	public int ExpectedBottomPayloadSlots => CyclesPerPlacement * TabsPerPage;

	public long ExpectedCurrentRetainedPayloadBytes =>
		(long)(ExpectedTopPayloadSlots + ExpectedBottomPayloadSlots) * PayloadBytesPerIcon;

	public bool LeakProved =>
		Control.AliveTopTabLayouts == CyclesPerPlacement &&
		Control.AliveBottomNavigationViews == CyclesPerPlacement &&
		Current.AliveTopTabLayouts == CyclesPerPlacement &&
		Current.AliveBottomNavigationViews == CyclesPerPlacement &&
		Control.PayloadTopIconSlots == 0 &&
		Control.PayloadBottomIconSlots == 0 &&
		Current.PayloadTopIconSlots >= ExpectedTopPayloadSlots &&
		Current.PayloadBottomIconSlots >= ExpectedBottomPayloadSlots &&
		Current.RetainedNativeIconBytes >= ExpectedCurrentRetainedPayloadBytes &&
		Current.AliveTabbedPages == 0 &&
		Current.AliveManagers == 0 &&
		Current.AliveHandlers == 0 &&
		Current.AliveChildPages == 0 &&
		Current.AliveIconSources == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidTabbedPageTabIconRetentionRepro",
			$"Cycles per placement: {CyclesPerPlacement}",
			$"Tabs per transient TabbedPage: {TabsPerPage}",
			$"Icon size: {IconWidth}x{IconHeight}",
			$"Payload bytes per native tab icon: {PayloadBytesPerIcon:N0}",
			$"Expected top-tab payload slots: {ExpectedTopPayloadSlots}",
			$"Expected bottom-tab payload slots: {ExpectedBottomPayloadSlots}",
			$"Expected current retained native icon payload: {FormatBytes(ExpectedCurrentRetainedPayloadBytes)}",
			$"Source path mirrored: TabbedPageManager.SetTabIconImageSource -> TabLayout.Tab.SetIcon and BottomNavigationViewUtils.SetMenuItemIcon -> IMenuItem.SetIcon",
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
			$"Control retained native tab icon payload: {FormatBytes(Control.RetainedNativeIconBytes)}",
			$"Current retained native tab icon payload: {FormatBytes(Current.RetainedNativeIconBytes)}",
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
			$"  alive tab icon image sources: {result.AliveIconSources}/{result.TrackedCycles * 4}",
			$"  assigned top-tab icon slots: {result.AssignedTopIconSlots}",
			$"  payload-sized top-tab icon slots: {result.PayloadTopIconSlots}",
			$"  assigned bottom-tab icon slots: {result.AssignedBottomIconSlots}",
			$"  payload-sized bottom-tab icon slots: {result.PayloadBottomIconSlots}",
			$"  retained native icon bytes: {result.RetainedNativeIconBytes:N0}");
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
