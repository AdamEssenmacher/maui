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
using Android.Runtime;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.AppCompat.Widget;
using AndroidX.DrawerLayout.Widget;
using Google.Android.Material.BottomNavigation;
using Google.Android.Material.Tabs;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers;
using Microsoft.Maui.Controls.Platform.Compatibility;
using Microsoft.Maui.Graphics;
using AColor = Android.Graphics.Color;
using AToolbar = AndroidX.AppCompat.Widget.Toolbar;
using AView = Android.Views.View;

namespace AndroidShellBottomTabIconRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 48;
	const int TabsPerShell = 4;
	internal const int IconWidth = 512;
	internal const int IconHeight = 512;
	const int BytesPerPixel = 4;
	const int PayloadBytesPerIcon = IconWidth * IconHeight * BytesPerPixel;

	static readonly MethodInfo DestroyMethod =
		typeof(ShellItemRenderer).GetMethod("Destroy", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(ShellItemRenderer).FullName, "Destroy");

	static readonly List<RetainedNativeBottomNavigationView> RetainedBottomViews = new();

	public static async Task<ReproReport> RunAsync(AppCompatActivity activity)
	{
		RetainedBottomViews.Clear();
		EnsureCurrentApplication(activity);

		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			activity,
			"control: clear native Shell bottom-tab icon slots before renderer destroy",
			clearNativeIconSlots: true);

		var current = await RunScenarioAsync(
			activity,
			"current: ShellItemRenderer.Destroy() leaves bottom-tab icon slots assigned",
			clearNativeIconSlots: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedBottomViews);

		return new ReproReport(
			Cycles,
			TabsPerShell,
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
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			await CreateCycleAsync(activity, i, tracked, clearNativeIconSlots);

			if (i % 8 == 0)
				await Task.Yield();
		}

		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static async Task CreateCycleAsync(
		AppCompatActivity activity,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeIconSlots)
	{
		var services = new ReproServiceProvider(activity);
		var mauiContext = new MauiContext(services, activity);
		var shell = new Shell
		{
			FlyoutBehavior = FlyoutBehavior.Disabled
		};
		var shellHandler = new FakeElementHandler(mauiContext);
		shellHandler.SetVirtualView(shell);
		shell.Handler = shellHandler;

		var tabBar = new TabBar
		{
			Title = $"Root tabs {cycle + 1:000}"
		};

		var sections = new List<Tab>(TabsPerShell);
		var shellContents = new List<ShellContent>(TabsPerShell);
		var iconSources = new List<PayloadImageSource>(TabsPerShell);
		for (var tab = 0; tab < TabsPerShell; tab++)
		{
			var iconSource = new PayloadImageSource(cycle, tab);
			var page = new ContentPage
			{
				Title = $"Shell page {cycle + 1:000}-{tab + 1:00}",
				Content = new Label
				{
					Text = $"Shell order queue {cycle + 1:000}-{tab + 1:00}"
				}
			};
			var shellContent = new ShellContent
			{
				Title = $"Content {tab + 1}",
				Content = page
			};
			var section = new Tab
			{
				Title = $"Tab {tab + 1}",
				Icon = iconSource
			};

			section.Items.Add(shellContent);
			tabBar.Items.Add(section);
			sections.Add(section);
			shellContents.Add(shellContent);
			iconSources.Add(iconSource);
		}

		shell.Items.Add(tabBar);
		shell.CurrentItem = tabBar;
		tabBar.CurrentItem = sections[0];

		var shellContext = new FakeShellContext(activity, shell);
		var renderer = new ProbeShellItemRenderer(shellContext);
		((IShellItemRenderer)renderer).ShellItem = tabBar;
		var rootView = renderer.OnCreateView(LayoutInflater.From(activity), new FrameLayout(activity), null)
			?? throw new InvalidOperationException("ShellItemRenderer did not create a root view.");
		var bottomView = FindDescendant<BottomNavigationView>(rootView)
			?? throw new InvalidOperationException("ShellItemRenderer did not create a BottomNavigationView.");

		renderer.ForceBottomMenuSetup(bottomView);
		await WaitForIconSlotsAsync(bottomView);

		ClearNativeTitleSlots(bottomView);

		if (clearNativeIconSlots)
			ClearNativeIconSlots(bottomView);

		var retainedBottomView = RetainedNativeBottomNavigationView.Create(bottomView);
		RetainedBottomViews.Add(retainedBottomView);

		DestroyMethod.Invoke(renderer, null);

		foreach (var section in sections)
		{
			section.Icon = null;
			section.Items.Clear();
		}

		tabBar.Items.Clear();
		shell.Items.Clear();
		shell.Handler = null;
		shellHandler.DisconnectHandler();
		shellContext.Dispose();

		tracked.Add(TrackedCycle.Create(
			cycle,
			retainedBottomView,
			renderer,
			shell,
			tabBar,
			shellHandler,
			sections,
			shellContents,
			iconSources));

		services = null!;
		mauiContext = null!;
		shell = null!;
		shellHandler = null!;
		tabBar = null!;
		shellContext = null!;
		renderer = null!;
		rootView = null!;
		bottomView = null!;
		sections = null!;
		shellContents = null!;
		iconSources = null!;
	}

	static void EnsureCurrentApplication(AppCompatActivity activity)
	{
		var services = new ReproServiceProvider(activity);
		var mauiContext = new MauiContext(services, activity);
		var application = Microsoft.Maui.Controls.Application.Current ?? new Microsoft.Maui.Controls.Application();
		application.Handler = new FakeElementHandler(mauiContext);
		Microsoft.Maui.Controls.Application.SetCurrentApplication(application);
	}

	static async Task WaitForIconSlotsAsync(BottomNavigationView bottomView)
	{
		IconSnapshot snapshot = IconSnapshot.Empty;

		for (var i = 0; i < 80; i++)
		{
			snapshot = CaptureIconSlots(bottomView);
			if (snapshot.PayloadSlots >= TabsPerShell)
				return;

			await Task.Delay(25);
		}

		throw new InvalidOperationException(
			$"Expected {TabsPerShell} payload icon slots for Shell bottom tabs, saw {snapshot.PayloadSlots}.");
	}

	static T? FindDescendant<T>(AView view)
		where T : AView
	{
		if (view is T match)
			return match;

		if (view is ViewGroup viewGroup)
		{
			for (var i = 0; i < viewGroup.ChildCount; i++)
			{
				if (viewGroup.GetChildAt(i) is AView child &&
					FindDescendant<T>(child) is { } result)
				{
					return result;
				}
			}
		}

		return null;
	}

	static void ClearNativeTitleSlots(BottomNavigationView bottomView)
	{
		var menu = bottomView.Menu;
		for (var i = 0; i < menu.Size(); i++)
			menu.GetItem(i)?.SetTitle(string.Empty);
	}

	static void ClearNativeIconSlots(BottomNavigationView bottomView)
	{
		var menu = bottomView.Menu;
		for (var i = 0; i < menu.Size(); i++)
			menu.GetItem(i)?.SetIcon((Drawable?)null);
	}

	static IconSnapshot CaptureIconSlots(BottomNavigationView bottomView)
	{
		var assigned = 0;
		var payload = 0;
		long bytes = 0;
		var menu = bottomView.Menu;

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

		public IServiceProvider HostServiceProvider => this;

		public IImageSourceService? GetImageSourceService(Type imageSource) =>
			imageSource == typeof(PayloadImageSource)
				? _imageSourceService
				: null;

		public object? GetService(Type serviceType)
		{
			if (serviceType == typeof(IImageSourceServiceProvider))
				return this;
			if (serviceType == typeof(Activity))
				return _activity;
			if (serviceType == typeof(Context))
				return _activity;
			if (serviceType == typeof(LayoutInflater))
				return LayoutInflater.From(_activity);

			return null;
		}
	}

	sealed class FakeShellContext : IShellContext, IDisposable
	{
		readonly NoOpBottomNavViewAppearanceTracker _bottomTracker = new();

		public FakeShellContext(Context context, Shell shell)
		{
			AndroidContext = context;
			Shell = shell;
			CurrentDrawerLayout = new DrawerLayout(context);
		}

		public Context AndroidContext { get; }

		public DrawerLayout CurrentDrawerLayout { get; }

		public Shell Shell { get; }

		public IShellObservableFragment CreateFragmentForPage(Page page) =>
			throw new NotSupportedException("Fragments are suppressed for this Shell bottom-tab icon repro.");

		public IShellFlyoutContentRenderer CreateShellFlyoutContentRenderer() =>
			throw new NotSupportedException("Flyout content is not needed for this repro.");

		public IShellItemRenderer CreateShellItemRenderer(ShellItem shellItem) =>
			throw new NotSupportedException("Nested Shell item renderers are not needed for this repro.");

		public IShellSectionRenderer CreateShellSectionRenderer(ShellSection shellSection) =>
			throw new NotSupportedException("Section renderers are suppressed for this repro.");

		public IShellToolbarTracker CreateTrackerForToolbar(AToolbar toolbar) =>
			throw new NotSupportedException("Toolbar trackers are not needed for this repro.");

		public IShellToolbarAppearanceTracker CreateToolbarAppearanceTracker() =>
			throw new NotSupportedException("Toolbar appearance is not needed for this repro.");

		public IShellTabLayoutAppearanceTracker CreateTabLayoutAppearanceTracker(ShellSection shellSection) =>
			throw new NotSupportedException("Top tab layout appearance is not needed for this repro.");

		public IShellBottomNavViewAppearanceTracker CreateBottomNavViewAppearanceTracker(ShellItem shellItem) =>
			_bottomTracker;

		public void Dispose()
		{
			CurrentDrawerLayout.Dispose();
			_bottomTracker.Dispose();
		}
	}

	sealed class NoOpBottomNavViewAppearanceTracker : IShellBottomNavViewAppearanceTracker
	{
		public void SetAppearance(BottomNavigationView bottomView, IShellAppearanceElement appearance)
		{
		}

		public void ResetAppearance(BottomNavigationView bottomView)
		{
		}

		public void Dispose()
		{
		}
	}

	sealed class ProbeShellItemRenderer : ShellItemRenderer
	{
		public ProbeShellItemRenderer(IShellContext shellContext)
			: base(shellContext)
		{
		}

		public void ForceBottomMenuSetup(BottomNavigationView bottomView)
		{
			DisplayedPage = ((IShellContentController)ShellItem.CurrentItem.CurrentItem).GetOrCreateContent();
			SetupMenu(bottomView.Menu, bottomView.MaxItemCount, ShellItem);
		}

		protected override void OnShellSectionChanged()
		{
			// The repro only needs bottom tab menu creation; suppress fragment navigation.
		}
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

		public void SetMauiContext(IMauiContext mauiContext)
		{
			MauiContext = mauiContext;
		}

		public bool HasContainer { get; set; }

		public object? ContainerView => null;

		public void SetVirtualView(IElement view)
		{
			_elementVirtualView = view;
			VirtualView = view as IView;
			view.Handler = this;
		}

		public void UpdateValue(string property)
		{
		}

		public void Invoke(string command, object? args = null)
		{
		}

		public void DisconnectHandler()
		{
			if (_elementVirtualView?.Handler == this)
				_elementVirtualView.Handler = null;

			_elementVirtualView = null;
			VirtualView = null;
			MauiContext = null;
		}

		public Size GetDesiredSize(double widthConstraint, double heightConstraint) => Size.Zero;

		public void PlatformArrange(Microsoft.Maui.Graphics.Rect frame)
		{
		}
	}

	internal sealed class RetainedNativeBottomNavigationView
	{
		readonly IntPtr _globalHandle;

		RetainedNativeBottomNavigationView(IntPtr globalHandle)
		{
			_globalHandle = globalHandle;
		}

		public bool IsAlive => _globalHandle != IntPtr.Zero;

		public static RetainedNativeBottomNavigationView Create(BottomNavigationView bottomView)
		{
			var globalHandle = JNIEnv.NewGlobalRef(bottomView.Handle);
			return new RetainedNativeBottomNavigationView(globalHandle);
		}

		public BottomNavigationView CreateWrapper()
		{
			if (_globalHandle == IntPtr.Zero)
				throw new ObjectDisposedException(nameof(RetainedNativeBottomNavigationView));

			return Java.Lang.Object.GetObject<BottomNavigationView>(_globalHandle, JniHandleOwnership.DoNotTransfer)
				?? throw new InvalidOperationException("Could not re-wrap retained native BottomNavigationView peer.");
		}
	}

	internal sealed record IconSnapshot(int AssignedSlots, int PayloadSlots, long RetainedBytes)
	{
		public static IconSnapshot Empty { get; } = new(0, 0, 0);
	}

	internal sealed record TrackedCycle(
		int Cycle,
		RetainedNativeBottomNavigationView NativeBottomView,
		WeakReference<ShellItemRenderer> Renderer,
		WeakReference<Shell> Shell,
		WeakReference<TabBar> ShellItem,
		WeakReference<FakeElementHandler> ShellHandler,
		IReadOnlyList<WeakReference<Tab>> ShellSections,
		IReadOnlyList<WeakReference<ShellContent>> ShellContents,
		IReadOnlyList<WeakReference<PayloadImageSource>> IconSources)
	{
		public static TrackedCycle Create(
			int cycle,
			RetainedNativeBottomNavigationView nativeBottomView,
			ShellItemRenderer renderer,
			Shell shell,
			TabBar shellItem,
			FakeElementHandler shellHandler,
			IReadOnlyList<Tab> shellSections,
			IReadOnlyList<ShellContent> shellContents,
			IReadOnlyList<PayloadImageSource> iconSources)
		{
			return new TrackedCycle(
				cycle,
				nativeBottomView,
				new WeakReference<ShellItemRenderer>(renderer),
				new WeakReference<Shell>(shell),
				new WeakReference<TabBar>(shellItem),
				new WeakReference<FakeElementHandler>(shellHandler),
				shellSections.Select(static section => new WeakReference<Tab>(section)).ToArray(),
				shellContents.Select(static content => new WeakReference<ShellContent>(content)).ToArray(),
				iconSources.Select(static iconSource => new WeakReference<PayloadImageSource>(iconSource)).ToArray());
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeBottomViews,
		int AliveRenderers,
		int AliveShells,
		int AliveShellItems,
		int AliveShellHandlers,
		int AliveShellSections,
		int AliveShellContents,
		int AliveIconSources,
		int AssignedIconSlots,
		int PayloadIconSlots,
		long RetainedNativeIconBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var rendererRefs = new List<WeakReference<ShellItemRenderer>>();
			var shellRefs = new List<WeakReference<Shell>>();
			var shellItemRefs = new List<WeakReference<TabBar>>();
			var shellHandlerRefs = new List<WeakReference<FakeElementHandler>>();
			var sectionRefs = new List<WeakReference<Tab>>();
			var contentRefs = new List<WeakReference<ShellContent>>();
			var iconSourceRefs = new List<WeakReference<PayloadImageSource>>();

			var aliveNativeBottomViews = 0;
			var assignedSlots = 0;
			var payloadSlots = 0;
			long retainedBytes = 0;

			foreach (var cycle in tracked)
			{
				rendererRefs.Add(cycle.Renderer);
				shellRefs.Add(cycle.Shell);
				shellItemRefs.Add(cycle.ShellItem);
				shellHandlerRefs.Add(cycle.ShellHandler);
				sectionRefs.AddRange(cycle.ShellSections);
				contentRefs.AddRange(cycle.ShellContents);
				iconSourceRefs.AddRange(cycle.IconSources);

				if (!cycle.NativeBottomView.IsAlive)
					continue;

				aliveNativeBottomViews++;
				using var wrapper = cycle.NativeBottomView.CreateWrapper();
				var snapshot = CaptureIconSlots(wrapper);
				assignedSlots += snapshot.AssignedSlots;
				payloadSlots += snapshot.PayloadSlots;
				retainedBytes += snapshot.RetainedBytes;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeBottomViews,
				CountAlive(rendererRefs),
				CountAlive(shellRefs),
				CountAlive(shellItemRefs),
				CountAlive(shellHandlerRefs),
				CountAlive(sectionRefs),
				CountAlive(contentRefs),
				CountAlive(iconSourceRefs),
				assignedSlots,
				payloadSlots,
				retainedBytes);
		}
	}
}

internal sealed class PayloadImageSource : ImageSource
{
	public PayloadImageSource(int cycle, int tab)
	{
		Cycle = cycle;
		Tab = tab;
	}

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
		var color = AColor.Argb(
			255,
			(59 + payloadSource.Cycle * 37 + payloadSource.Tab * 13) % 255,
			(59 + payloadSource.Cycle * 67 + payloadSource.Tab * 17) % 255,
			(59 + payloadSource.Cycle * 97 + payloadSource.Tab * 19) % 255);
		bitmap.EraseColor(color);

		Drawable drawable = new BitmapDrawable(context.Resources, bitmap);
		return Task.FromResult<IImageSourceServiceResult<Drawable>?>(new ImageSourceServiceResult(drawable));
	}
}

internal sealed record ReproReport(
	int Cycles,
	int TabsPerShell,
	int IconWidth,
	int IconHeight,
	int PayloadBytesPerIcon,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public int ExpectedPayloadSlots => Cycles * TabsPerShell;

	public long ExpectedCurrentRetainedPayloadBytes => (long)ExpectedPayloadSlots * PayloadBytesPerIcon;

	public bool LeakProved =>
		Control.AliveNativeBottomViews == Cycles &&
		Current.AliveNativeBottomViews == Cycles &&
		Control.PayloadIconSlots == 0 &&
		Current.PayloadIconSlots >= ExpectedPayloadSlots &&
		Current.RetainedNativeIconBytes >= ExpectedCurrentRetainedPayloadBytes &&
		Current.AliveRenderers == 0 &&
		Current.AliveShells == 0 &&
		Current.AliveShellItems == 0 &&
		Current.AliveShellHandlers == 0 &&
		Current.AliveShellSections == 0 &&
		Current.AliveShellContents == 0 &&
		Current.AliveIconSources == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidShellBottomTabIconRetentionRepro",
			$"Cycles: {Cycles}",
			$"Tabs per transient Shell item: {TabsPerShell}",
			$"Icon size: {IconWidth}x{IconHeight}",
			$"Payload bytes per native bottom-tab icon: {PayloadBytesPerIcon:N0}",
			$"Expected payload icon slots: {ExpectedPayloadSlots}",
			$"Expected current retained native icon payload: {FormatBytes(ExpectedCurrentRetainedPayloadBytes)}",
			$"Source path mirrored: ShellItemRenderer.SetupMenu -> BottomNavigationViewUtils.SetMenuItemIcon -> IMenuItem.SetIcon",
			$"Control isolation: native title slots are cleared in both runs; native icon slots are cleared only in the control run",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained native bottom-tab icon payload: {FormatBytes(Control.RetainedNativeIconBytes)}",
			$"Current retained native bottom-tab icon payload: {FormatBytes(Current.RetainedNativeIconBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  alive native BottomNavigationView peers: {result.AliveNativeBottomViews}/{result.TrackedCycles}",
			$"  alive ShellItemRenderers: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  alive Shells: {result.AliveShells}/{result.TrackedCycles}",
			$"  alive TabBars: {result.AliveShellItems}/{result.TrackedCycles}",
			$"  alive fake Shell handlers: {result.AliveShellHandlers}/{result.TrackedCycles}",
			$"  alive Shell sections: {result.AliveShellSections}/{result.TrackedCycles * 4}",
			$"  alive Shell contents: {result.AliveShellContents}/{result.TrackedCycles * 4}",
			$"  alive tab icon image sources: {result.AliveIconSources}/{result.TrackedCycles * 4}",
			$"  assigned bottom-tab icon slots: {result.AssignedIconSlots}",
			$"  payload-sized bottom-tab icon slots: {result.PayloadIconSlots}",
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
