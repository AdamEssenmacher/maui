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
using AndroidX.AppCompat.Widget;
using AndroidX.DrawerLayout.Widget;
using Google.Android.Material.BottomNavigation;
using Google.Android.Material.Tabs;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Platform;
using Microsoft.Maui.Controls.Platform.Compatibility;
using Microsoft.Maui.Graphics;
using AToolbar = AndroidX.AppCompat.Widget.Toolbar;

namespace AndroidShellTabTitleRetentionRepro;

internal static class ReproSession
{
	const int CyclesPerPlacement = 128;
	const int ItemsPerCycle = 4;
	const int PayloadCharsPerTitle = 8 * 1024;
	const int PayloadBytesPerTitle = PayloadCharsPerTitle * sizeof(char);
	const string PayloadPrefix = "android-shell-tab-title-";

	static readonly MethodInfo SetupMenuMethod =
		typeof(BottomNavigationViewUtils).GetMethod("SetupMenu", BindingFlags.Static | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(BottomNavigationViewUtils).FullName, "SetupMenu");

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
			"control: clear native Shell TabLayout/MenuItem title slots after assignment",
			clearNativeTitleSlots: true);

		var current = await RunScenarioAsync(
			activity,
			"current: Shell title-copy paths leave native TabLayout/MenuItem title slots assigned",
			clearNativeTitleSlots: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedTopTabLayouts);
		GC.KeepAlive(RetainedBottomNavigationViews);

		return new ReproReport(
			CyclesPerPlacement,
			ItemsPerCycle,
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
			CreateTopTabCycle(activity, i, tracked, clearNativeTitleSlots);
			CreateBottomTabCycle(activity, i, tracked, clearNativeTitleSlots);

			if (i % 16 == 0)
				await Task.Yield();
		}

		await Task.Delay(250);
		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateTopTabCycle(
		AppCompatActivity activity,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeTitleSlots)
	{
		var services = new ReproServiceProvider(activity);
		var mauiContext = new MauiContext(services, activity);
		var shell = new Shell();
		var shellHandler = new FakeElementHandler(mauiContext);
		shellHandler.SetVirtualView(shell);
		shell.Handler = shellHandler;

		var shellContext = new FakeShellContext(activity, shell);
		var section = new ShellSection
		{
			Title = $"Top shell section {cycle + 1:000}"
		};
		var contents = new List<ShellContent>(ItemsPerCycle);
		for (var item = 0; item < ItemsPerCycle; item++)
		{
			var content = new ShellContent
			{
				Title = CreateTitlePayload(TabPlacement.Top, cycle, item),
				ContentTemplate = new DataTemplate(() => new ContentPage
				{
					Title = $"Orders {cycle + 1:000}-{item + 1:00}",
					Content = new Label { Text = $"Orders queue {cycle + 1:000}-{item + 1:00}" }
				})
			};

			contents.Add(content);
			section.Items.Add(content);
		}

		shell.Items.Add(new FlyoutItem { Items = { section } });

		var renderer = new ShellSectionRenderer(shellContext)
		{
			ShellSection = section
		};
		var strategy = (TabLayoutMediator.ITabConfigurationStrategy)renderer;
		var tabLayout = new TabLayout(activity);

		for (var item = 0; item < ItemsPerCycle; item++)
		{
			var tab = tabLayout.NewTab();
			strategy.OnConfigureTab(tab, item);
			tabLayout.AddTab(tab, item == 0);
		}

		foreach (var content in contents)
			content.Title = null;
		section.Title = null;

		if (clearNativeTitleSlots)
			ClearTopTitleSlots(tabLayout);

		RetainedTopTabLayouts.Add(tabLayout);

		tracked.Add(TrackedCycle.CreateTop(
			tabLayout,
			shell,
			section,
			contents,
			renderer,
			shellHandler));

		services = null!;
		mauiContext = null!;
		shell = null!;
		shellHandler = null!;
		shellContext = null!;
		section = null!;
		contents = null!;
		renderer = null!;
		strategy = null!;
		tabLayout = null!;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateBottomTabCycle(
		AppCompatActivity activity,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeTitleSlots)
	{
		var services = new ReproServiceProvider(activity);
		var mauiContext = new MauiContext(services, activity);
		var shell = new Shell();
		var shellHandler = new FakeElementHandler(mauiContext);
		shellHandler.SetVirtualView(shell);
		shell.Handler = shellHandler;

		var shellItem = new TabBar
		{
			Title = $"Bottom tab bar {cycle + 1:000}"
		};
		var sections = new List<ShellSection>(ItemsPerCycle);
		for (var item = 0; item < ItemsPerCycle; item++)
		{
			var section = new ShellSection
			{
				Title = CreateTitlePayload(TabPlacement.Bottom, cycle, item),
				Items =
				{
					new ShellContent
					{
						Title = $"Queue page {cycle + 1:000}-{item + 1:00}",
						ContentTemplate = new DataTemplate(() => new ContentPage
						{
							Content = new Label { Text = $"Queue page {cycle + 1:000}-{item + 1:00}" }
						})
					}
				}
			};

			sections.Add(section);
			shellItem.Items.Add(section);
		}
		shell.Items.Add(shellItem);

		var bottomView = new BottomNavigationView(activity);
		var items = sections
			.Select(static section => (section.Title, section.Icon, section.IsEnabled))
			.ToList();

		using (var menu = bottomView.Menu)
		{
			SetupMenuMethod.Invoke(null, new object[] { menu, 5, items, 0, bottomView, mauiContext });
		}

		foreach (var section in sections)
			section.Title = null;
		shellItem.Title = null;

		if (clearNativeTitleSlots)
			ClearBottomTitleSlots(bottomView);

		RetainedBottomNavigationViews.Add(bottomView);

		tracked.Add(TrackedCycle.CreateBottom(
			bottomView,
			shell,
			shellItem,
			sections,
			shellHandler));

		services = null!;
		mauiContext = null!;
		shell = null!;
		shellHandler = null!;
		shellItem = null!;
		sections = null!;
		bottomView = null!;
		items = null!;
	}

	static void ClearTopTitleSlots(TabLayout topTabs)
	{
		for (var i = 0; i < topTabs.TabCount; i++)
			topTabs.GetTabAt(i)?.SetText(string.Empty);
	}

	static void ClearBottomTitleSlots(BottomNavigationView bottomTabs)
	{
		var menu = bottomTabs.Menu;
		for (var i = 0; i < menu.Size(); i++)
			menu.GetItem(i)?.SetTitle(string.Empty);
	}

	static TitleSnapshot CaptureTopTitleSlots(TabLayout topTabs)
	{
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

	static TitleSnapshot CaptureBottomTitleSlots(BottomNavigationView bottomTabs)
	{
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

		if (text.StartsWith(PayloadPrefix, StringComparison.Ordinal) &&
			text.Length >= PayloadCharsPerTitle)
		{
			payload++;
		}
	}

	static string CreateTitlePayload(TabPlacement placement, int cycle, int item)
	{
		var prefix = $"{PayloadPrefix}{placement.ToString().ToLowerInvariant()}-{cycle:D4}-{item:D2}-";
		return prefix + new string((char)('A' + ((cycle + item) % 26)), PayloadCharsPerTitle - prefix.Length);
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

		public object? GetService(Type serviceType)
		{
			if (serviceType == typeof(Activity))
				return _activity;
			if (serviceType == typeof(Context))
				return _activity;
			if (serviceType == typeof(LayoutInflater))
				return LayoutInflater.From(_activity);

			return null;
		}
	}

	sealed class FakeShellContext : IShellContext
	{
		public FakeShellContext(Context context, Shell shell)
		{
			AndroidContext = context;
			Shell = shell;
		}

		public Context AndroidContext { get; }

		public DrawerLayout CurrentDrawerLayout => throw new NotSupportedException();

		public Shell Shell { get; }

		public IShellObservableFragment CreateFragmentForPage(Page page) => throw new NotSupportedException();

		public IShellFlyoutContentRenderer CreateShellFlyoutContentRenderer() => throw new NotSupportedException();

		public IShellItemRenderer CreateShellItemRenderer(ShellItem shellItem) => throw new NotSupportedException();

		public IShellSectionRenderer CreateShellSectionRenderer(ShellSection shellSection) => throw new NotSupportedException();

		public IShellToolbarTracker CreateTrackerForToolbar(AToolbar toolbar) => throw new NotSupportedException();

		public IShellToolbarAppearanceTracker CreateToolbarAppearanceTracker() => throw new NotSupportedException();

		public IShellTabLayoutAppearanceTracker CreateTabLayoutAppearanceTracker(ShellSection shellSection) => throw new NotSupportedException();

		public IShellBottomNavViewAppearanceTracker CreateBottomNavViewAppearanceTracker(ShellItem shellItem) => throw new NotSupportedException();
	}

	internal sealed class FakeElementHandler : IViewHandler
	{
		IElement? _elementVirtualView;

		public FakeElementHandler(IMauiContext mauiContext)
		{
			MauiContext = mauiContext;
		}

		public object? PlatformView => null;

		public IView? VirtualView { get; private set; }

		IElement? IElementHandler.VirtualView => _elementVirtualView;

		public IMauiContext? MauiContext { get; private set; }

		public bool HasContainer { get; set; }

		public object? ContainerView => null;

		public void SetMauiContext(IMauiContext mauiContext)
		{
			MauiContext = mauiContext;
		}

		public void SetVirtualView(IElement view)
		{
			_elementVirtualView = view;
			VirtualView = view as IView;
		}

		public void UpdateValue(string property)
		{
		}

		public void Invoke(string command, object? args = null)
		{
		}

		public void DisconnectHandler()
		{
			_elementVirtualView = null;
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
		WeakReference<Shell> Shell,
		WeakReference<ShellItem>? ShellItem,
		IReadOnlyList<WeakReference<ShellSection>> Sections,
		IReadOnlyList<WeakReference<ShellContent>> Contents,
		WeakReference<ShellSectionRenderer>? ShellSectionRenderer,
		WeakReference<FakeElementHandler> ShellHandler)
	{
		public static TrackedCycle CreateTop(
			TabLayout topTabs,
			Shell shell,
			ShellSection section,
			IReadOnlyList<ShellContent> contents,
			ShellSectionRenderer renderer,
			FakeElementHandler shellHandler)
		{
			return new TrackedCycle(
				TabPlacement.Top,
				new WeakReference<TabLayout>(topTabs),
				null,
				new WeakReference<Shell>(shell),
				null,
				new[] { new WeakReference<ShellSection>(section) },
				contents.Select(static content => new WeakReference<ShellContent>(content)).ToArray(),
				new WeakReference<ShellSectionRenderer>(renderer),
				new WeakReference<FakeElementHandler>(shellHandler));
		}

		public static TrackedCycle CreateBottom(
			BottomNavigationView bottomTabs,
			Shell shell,
			ShellItem shellItem,
			IReadOnlyList<ShellSection> sections,
			FakeElementHandler shellHandler)
		{
			return new TrackedCycle(
				TabPlacement.Bottom,
				null,
				new WeakReference<BottomNavigationView>(bottomTabs),
				new WeakReference<Shell>(shell),
				new WeakReference<ShellItem>(shellItem),
				sections.Select(static section => new WeakReference<ShellSection>(section)).ToArray(),
				Array.Empty<WeakReference<ShellContent>>(),
				null,
				new WeakReference<FakeElementHandler>(shellHandler));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveTopTabLayouts,
		int AliveBottomNavigationViews,
		int AliveShells,
		int AliveShellItems,
		int AliveShellSections,
		int AliveShellContents,
		int AliveShellSectionRenderers,
		int AliveShellHandlers,
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
			var shellRefs = new List<WeakReference<Shell>>();
			var shellItemRefs = new List<WeakReference<ShellItem>>();
			var sectionRefs = new List<WeakReference<ShellSection>>();
			var contentRefs = new List<WeakReference<ShellContent>>();
			var rendererRefs = new List<WeakReference<ShellSectionRenderer>>();
			var handlerRefs = new List<WeakReference<FakeElementHandler>>();

			var assignedTop = 0;
			var payloadTop = 0;
			var assignedBottom = 0;
			var payloadBottom = 0;
			long retainedBytes = 0;

			foreach (var cycle in tracked)
			{
				shellRefs.Add(cycle.Shell);
				handlerRefs.Add(cycle.ShellHandler);
				sectionRefs.AddRange(cycle.Sections);
				contentRefs.AddRange(cycle.Contents);

				if (cycle.ShellItem is not null)
					shellItemRefs.Add(cycle.ShellItem);
				if (cycle.ShellSectionRenderer is not null)
					rendererRefs.Add(cycle.ShellSectionRenderer);

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
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				CountAlive(topLayoutRefs),
				CountAlive(bottomViewRefs),
				CountAlive(shellRefs),
				CountAlive(shellItemRefs),
				CountAlive(sectionRefs),
				CountAlive(contentRefs),
				CountAlive(rendererRefs),
				CountAlive(handlerRefs),
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
	int ItemsPerCycle,
	int PayloadCharsPerTitle,
	int PayloadBytesPerTitle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public int ExpectedTopPayloadSlots => CyclesPerPlacement * ItemsPerCycle;

	public int ExpectedBottomPayloadSlots => CyclesPerPlacement * ItemsPerCycle;

	public bool LeakProved =>
		Control.AliveTopTabLayouts == CyclesPerPlacement &&
		Control.AliveBottomNavigationViews == CyclesPerPlacement &&
		Current.AliveTopTabLayouts == CyclesPerPlacement &&
		Current.AliveBottomNavigationViews == CyclesPerPlacement &&
		Control.PayloadTopTitleSlots == 0 &&
		Control.PayloadBottomTitleSlots == 0 &&
		Current.PayloadTopTitleSlots >= ExpectedTopPayloadSlots &&
		Current.PayloadBottomTitleSlots >= ExpectedBottomPayloadSlots &&
		Current.AliveShells == 0 &&
		Current.AliveShellItems == 0 &&
		Current.AliveShellSections == 0 &&
		Current.AliveShellContents == 0 &&
		Current.AliveShellSectionRenderers == 0 &&
		Current.AliveShellHandlers == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidShellTabTitleRetentionRepro",
			$"Cycles per placement: {CyclesPerPlacement}",
			$"Items per transient Shell tab group: {ItemsPerCycle}",
			$"Payload chars per native tab title slot: {PayloadCharsPerTitle}",
			$"Payload bytes per native tab title slot: {PayloadBytesPerTitle}",
			$"Expected top Shell tab payload slots: {ExpectedTopPayloadSlots}",
			$"Expected bottom Shell tab payload slots: {ExpectedBottomPayloadSlots}",
			"Top-tab source path: ShellSectionRenderer.ITabConfigurationStrategy.OnConfigureTab() assigns ShellContent.Title to TabLayout.Tab text",
			"Bottom-tab source path: ShellItemRenderer title list feeds BottomNavigationViewUtils.SetupMenu(), assigning ShellSection.Title to Android IMenuItem title",
			"Control run explicitly clears retained native title slots after assignment; current run leaves MAUI-assigned native title slots intact",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained native Shell tab title payload: {FormatBytes(Control.RetainedNativeTitleBytes)}",
			$"Current retained native Shell tab title payload: {FormatBytes(Current.RetainedNativeTitleBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked native Shell tab cycles: {result.TrackedCycles}",
			$"  alive native TabLayout peers: {result.AliveTopTabLayouts}/{result.TrackedCycles / 2}",
			$"  alive native BottomNavigationView peers: {result.AliveBottomNavigationViews}/{result.TrackedCycles / 2}",
			$"  alive Shell roots: {result.AliveShells}/{result.TrackedCycles}",
			$"  alive ShellItems: {result.AliveShellItems}/{result.TrackedCycles / 2}",
			$"  alive ShellSections: {result.AliveShellSections}/{result.TrackedCycles / 2 + result.TrackedCycles / 2 * 4}",
			$"  alive ShellContents: {result.AliveShellContents}/{result.TrackedCycles / 2 * 4}",
			$"  alive ShellSectionRenderers: {result.AliveShellSectionRenderers}/{result.TrackedCycles / 2}",
			$"  alive fake Shell handlers: {result.AliveShellHandlers}/{result.TrackedCycles}",
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
