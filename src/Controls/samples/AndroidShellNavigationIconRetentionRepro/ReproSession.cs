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
using Google.Android.Material.AppBar;
using Google.Android.Material.BottomNavigation;
using Google.Android.Material.Tabs;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Platform.Compatibility;
using Microsoft.Maui.Graphics;
using AColor = Android.Graphics.Color;
using AToolbar = AndroidX.AppCompat.Widget.Toolbar;
using AView = Android.Views.View;
using ControlsToolbar = Microsoft.Maui.Controls.Toolbar;

namespace AndroidShellNavigationIconRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 96;
	internal const int IconWidth = 512;
	internal const int IconHeight = 512;
	const int BytesPerPixel = 4;
	const int PayloadBytesPerIcon = IconWidth * IconHeight * BytesPerPixel;

	static readonly List<AToolbar> RetainedNativeToolbars = new();

	public static async Task<ReproReport> RunAsync(AppCompatActivity activity)
	{
		RetainedNativeToolbars.Clear();
		EnsureCurrentApplication(activity);

		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			activity,
			"control: clear native Shell toolbar NavigationIcon before tracker disposal",
			clearNativeNavigationIcon: true);

		var current = await RunScenarioAsync(
			activity,
			"current: ShellToolbarTracker.Dispose() leaves native NavigationIcon assigned",
			clearNativeNavigationIcon: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeToolbars);

		return new ReproReport(
			Cycles,
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
		bool clearNativeNavigationIcon)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			await CreateCycleAsync(activity, i, tracked, clearNativeNavigationIcon);

			if (i % 16 == 0)
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
		bool clearNativeNavigationIcon)
	{
		var services = new ReproServiceProvider(activity);
		var mauiContext = new MauiContext(services, activity);
		var shell = new Shell
		{
			FlyoutBehavior = FlyoutBehavior.Flyout,
			Title = $"Transient Shell {cycle + 1:000}"
		};
		var shellHandler = new FakeElementHandler(mauiContext, new FrameLayout(activity));
		shellHandler.SetVirtualView(shell);
		shell.Handler = shellHandler;

		var iconSource = new PayloadImageSource(cycle);
		var backButtonBehavior = new BackButtonBehavior
		{
			IconOverride = iconSource,
			TextOverride = string.Empty
		};
		var page = new ContentPage
		{
			Title = $"Route {cycle + 1:000}",
			Content = new Label
			{
				Text = $"Route payload {cycle + 1:000}"
			}
		};
		Shell.SetBackButtonBehavior(page, backButtonBehavior);

		var virtualToolbar = new ControlsToolbar(page)
		{
			BackButtonVisible = true,
			BackButtonEnabled = true,
			DrawerToggleVisible = true,
			IsVisible = true,
			Title = page.Title
		};
		var platformToolbar = new AToolbar(activity);
		var appBar = new AppBarLayout(activity);
		appBar.AddView(platformToolbar, new ViewGroup.LayoutParams(1, 1));
		var drawerLayout = new DrawerLayout(activity);
		var shellContext = new FakeShellContext(activity, drawerLayout, shell);
		var tracker = new ShellToolbarTracker(shellContext, platformToolbar, drawerLayout)
		{
			CanNavigateBack = true,
			TintColor = Colors.White
		};
		((IShellToolbarTracker)tracker).SetToolbar(virtualToolbar);
		tracker.Page = page;

		await WaitForNavigationIconAsync(platformToolbar);

		if (clearNativeNavigationIcon)
			ClearNativeNavigationIcon(platformToolbar);

		RetainedNativeToolbars.Add(platformToolbar);

		tracker.Dispose();
		platformToolbar.SetNavigationOnClickListener(null);
		backButtonBehavior.IconOverride = null;
		Shell.SetBackButtonBehavior(page, null);
		shell.Handler = null;
		shellHandler.DisconnectHandler();
		appBar.RemoveView(platformToolbar);
		shellContext.Dispose();

		tracked.Add(TrackedCycle.Create(
			cycle,
			platformToolbar,
			tracker,
			shell,
			shellHandler,
			page,
			backButtonBehavior,
			virtualToolbar,
			iconSource));

		services = null!;
		mauiContext = null!;
		shell = null!;
		shellHandler = null!;
		iconSource = null!;
		backButtonBehavior = null!;
		page = null!;
		virtualToolbar = null!;
		platformToolbar = null!;
		appBar = null!;
		drawerLayout = null!;
		shellContext = null!;
		tracker = null!;
	}

	static void EnsureCurrentApplication(AppCompatActivity activity)
	{
		var services = new ReproServiceProvider(activity);
		var mauiContext = new MauiContext(services, activity);
		var application = Microsoft.Maui.Controls.Application.Current ?? new Microsoft.Maui.Controls.Application();
		application.Handler = new FakeElementHandler(mauiContext, new FrameLayout(activity));
		Microsoft.Maui.Controls.Application.SetCurrentApplication(application);
	}

	static async Task WaitForNavigationIconAsync(AToolbar toolbar)
	{
		NavigationIconSnapshot snapshot = NavigationIconSnapshot.Empty;

		for (var i = 0; i < 80; i++)
		{
			snapshot = CaptureNavigationIcon(toolbar);
			if (snapshot.PayloadSlots == 1)
				return;

			await Task.Delay(25);
		}

		throw new InvalidOperationException(
			$"Expected a payload-sized Shell toolbar NavigationIcon, saw assigned={snapshot.AssignedSlots}, payload={snapshot.PayloadSlots}, bytes={snapshot.RetainedNativeIconBytes:N0}.");
	}

	static void ClearNativeNavigationIcon(AToolbar toolbar)
	{
		var previous = toolbar.NavigationIcon;
		toolbar.NavigationIcon = null;
		previous?.Dispose();
	}

	static NavigationIconSnapshot CaptureNavigationIcon(AToolbar toolbar)
	{
		var navigationIcon = toolbar.NavigationIcon;
		if (navigationIcon is null)
			return NavigationIconSnapshot.Empty;

		var payloadBytes = GetFlyoutIconPayloadBytes(navigationIcon);
		var payloadSlots = payloadBytes >= PayloadBytesPerIcon ? 1 : 0;
		return new NavigationIconSnapshot(1, payloadSlots, payloadBytes);
	}

	static long GetFlyoutIconPayloadBytes(Drawable drawable)
	{
		var iconBitmapProperty = drawable.GetType().GetProperty(
			"IconBitmap",
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

		if (iconBitmapProperty?.GetValue(drawable) is Drawable iconBitmap)
			return GetDrawableByteCount(iconBitmap);

		return GetDrawableByteCount(drawable);
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
		public FakeShellContext(Context context, DrawerLayout drawerLayout, Shell shell)
		{
			AndroidContext = context;
			CurrentDrawerLayout = drawerLayout;
			Shell = shell;
		}

		public Context AndroidContext { get; }

		public DrawerLayout CurrentDrawerLayout { get; }

		public Shell Shell { get; }

		public IShellObservableFragment CreateFragmentForPage(Page page) =>
			throw new NotSupportedException("Fragments are not needed for this Shell toolbar navigation icon repro.");

		public IShellFlyoutContentRenderer CreateShellFlyoutContentRenderer() =>
			throw new NotSupportedException("Flyout content is not needed for this repro.");

		public IShellItemRenderer CreateShellItemRenderer(ShellItem shellItem) =>
			throw new NotSupportedException("Shell item renderers are not needed for this repro.");

		public IShellSectionRenderer CreateShellSectionRenderer(ShellSection shellSection) =>
			throw new NotSupportedException("Shell section renderers are not needed for this repro.");

		public IShellToolbarTracker CreateTrackerForToolbar(AToolbar toolbar) =>
			throw new NotSupportedException("Nested toolbar trackers are not needed for this repro.");

		public IShellToolbarAppearanceTracker CreateToolbarAppearanceTracker() =>
			throw new NotSupportedException("Toolbar appearance trackers are not needed for this repro.");

		public IShellTabLayoutAppearanceTracker CreateTabLayoutAppearanceTracker(ShellSection shellSection) =>
			throw new NotSupportedException("Tab layout appearance is not needed for this repro.");

		public IShellBottomNavViewAppearanceTracker CreateBottomNavViewAppearanceTracker(ShellItem shellItem) =>
			throw new NotSupportedException("Bottom nav appearance is not needed for this repro.");

		public void Dispose()
		{
			CurrentDrawerLayout.Dispose();
		}
	}

	internal sealed class FakeElementHandler : IViewHandler
	{
		readonly object? _platformView;
		IElement? _elementVirtualView;

		public FakeElementHandler(IMauiContext mauiContext, object? platformView)
		{
			MauiContext = mauiContext;
			_platformView = platformView;
		}

		public object? PlatformView => _platformView;

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

	internal sealed record NavigationIconSnapshot(int AssignedSlots, int PayloadSlots, long RetainedNativeIconBytes)
	{
		public static NavigationIconSnapshot Empty { get; } = new(0, 0, 0);
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<AToolbar> NativeToolbar,
		WeakReference<ShellToolbarTracker> Tracker,
		WeakReference<Shell> Shell,
		WeakReference<FakeElementHandler> ShellHandler,
		WeakReference<Page> Page,
		WeakReference<BackButtonBehavior> BackButtonBehavior,
		WeakReference<ControlsToolbar> VirtualToolbar,
		WeakReference<PayloadImageSource> IconSource)
	{
		public static TrackedCycle Create(
			int cycle,
			AToolbar nativeToolbar,
			ShellToolbarTracker tracker,
			Shell shell,
			FakeElementHandler shellHandler,
			Page page,
			BackButtonBehavior backButtonBehavior,
			ControlsToolbar virtualToolbar,
			PayloadImageSource iconSource)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<AToolbar>(nativeToolbar),
				new WeakReference<ShellToolbarTracker>(tracker),
				new WeakReference<Shell>(shell),
				new WeakReference<FakeElementHandler>(shellHandler),
				new WeakReference<Page>(page),
				new WeakReference<BackButtonBehavior>(backButtonBehavior),
				new WeakReference<ControlsToolbar>(virtualToolbar),
				new WeakReference<PayloadImageSource>(iconSource));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeToolbars,
		int AliveTrackers,
		int AliveShells,
		int AliveShellHandlers,
		int AlivePages,
		int AliveBackButtonBehaviors,
		int AliveVirtualToolbars,
		int AliveIconSources,
		int AssignedNavigationIconSlots,
		int PayloadNavigationIconSlots,
		long RetainedNativeIconBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var nativeToolbarRefs = new List<WeakReference<AToolbar>>();
			var trackerRefs = new List<WeakReference<ShellToolbarTracker>>();
			var shellRefs = new List<WeakReference<Shell>>();
			var shellHandlerRefs = new List<WeakReference<FakeElementHandler>>();
			var pageRefs = new List<WeakReference<Page>>();
			var backButtonBehaviorRefs = new List<WeakReference<BackButtonBehavior>>();
			var virtualToolbarRefs = new List<WeakReference<ControlsToolbar>>();
			var iconSourceRefs = new List<WeakReference<PayloadImageSource>>();
			var assignedSlots = 0;
			var payloadSlots = 0;
			long retainedBytes = 0;

			foreach (var cycle in tracked)
			{
				nativeToolbarRefs.Add(cycle.NativeToolbar);
				trackerRefs.Add(cycle.Tracker);
				shellRefs.Add(cycle.Shell);
				shellHandlerRefs.Add(cycle.ShellHandler);
				pageRefs.Add(cycle.Page);
				backButtonBehaviorRefs.Add(cycle.BackButtonBehavior);
				virtualToolbarRefs.Add(cycle.VirtualToolbar);
				iconSourceRefs.Add(cycle.IconSource);

				if (cycle.NativeToolbar.TryGetTarget(out var toolbar))
				{
					var snapshot = CaptureNavigationIcon(toolbar);
					assignedSlots += snapshot.AssignedSlots;
					payloadSlots += snapshot.PayloadSlots;
					retainedBytes += snapshot.RetainedNativeIconBytes;
				}
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				CountAlive(nativeToolbarRefs),
				CountAlive(trackerRefs),
				CountAlive(shellRefs),
				CountAlive(shellHandlerRefs),
				CountAlive(pageRefs),
				CountAlive(backButtonBehaviorRefs),
				CountAlive(virtualToolbarRefs),
				CountAlive(iconSourceRefs),
				assignedSlots,
				payloadSlots,
				retainedBytes);
		}
	}
}

internal sealed class PayloadImageSource : ImageSource
{
	public PayloadImageSource(int cycle)
	{
		Cycle = cycle;
	}

	public int Cycle { get; }
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
			(59 + payloadSource.Cycle * 37) % 255,
			(59 + payloadSource.Cycle * 67) % 255,
			(59 + payloadSource.Cycle * 97) % 255);
		bitmap.EraseColor(color);

		Drawable drawable = new BitmapDrawable(context.Resources, bitmap);
		return Task.FromResult<IImageSourceServiceResult<Drawable>?>(new ImageSourceServiceResult(drawable));
	}
}

internal sealed record ReproReport(
	int Cycles,
	int IconWidth,
	int IconHeight,
	int PayloadBytesPerIcon,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public long ExpectedCurrentRetainedPayloadBytes => (long)Cycles * PayloadBytesPerIcon;

	public bool LeakProved =>
		Control.AliveNativeToolbars == Cycles &&
		Current.AliveNativeToolbars == Cycles &&
		Control.PayloadNavigationIconSlots == 0 &&
		Current.PayloadNavigationIconSlots == Cycles &&
		Current.RetainedNativeIconBytes >= ExpectedCurrentRetainedPayloadBytes &&
		Current.AliveTrackers == 0 &&
		Current.AliveShells == 0 &&
		Current.AliveShellHandlers == 0 &&
		Current.AlivePages == 0 &&
		Current.AliveBackButtonBehaviors == 0 &&
		Current.AliveVirtualToolbars == 0 &&
		Current.AliveIconSources == Cycles;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidShellNavigationIconRetentionRepro",
			$"Cycles: {Cycles}",
			$"Icon size: {IconWidth}x{IconHeight}",
			$"Payload bytes per native Shell toolbar navigation icon: {PayloadBytesPerIcon:N0}",
			$"Expected current retained native navigation icon payload: {FormatBytes(ExpectedCurrentRetainedPayloadBytes)}",
			"Source path mirrored: ShellToolbarTracker.UpdateLeftBarButtonItem -> ImageSource.GetPlatformImageAsync -> FlyoutIconDrawerDrawable.IconBitmap -> Toolbar.NavigationIcon",
			"Control isolation: Toolbar navigation click listener is cleared in both runs; NavigationIcon is cleared only in the control run",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained native navigation icon payload: {FormatBytes(Control.RetainedNativeIconBytes)}",
			$"Current retained native navigation icon payload: {FormatBytes(Current.RetainedNativeIconBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  alive native toolbar peers: {result.AliveNativeToolbars}/{result.TrackedCycles}",
			$"  alive ShellToolbarTrackers: {result.AliveTrackers}/{result.TrackedCycles}",
			$"  alive Shells: {result.AliveShells}/{result.TrackedCycles}",
			$"  alive fake Shell handlers: {result.AliveShellHandlers}/{result.TrackedCycles}",
			$"  alive pages: {result.AlivePages}/{result.TrackedCycles}",
			$"  alive BackButtonBehaviors: {result.AliveBackButtonBehaviors}/{result.TrackedCycles}",
			$"  alive virtual toolbars: {result.AliveVirtualToolbars}/{result.TrackedCycles}",
			$"  alive icon image sources: {result.AliveIconSources}/{result.TrackedCycles}",
			$"  assigned native NavigationIcon slots: {result.AssignedNavigationIconSlots}/{result.TrackedCycles}",
			$"  payload-sized native NavigationIcon slots: {result.PayloadNavigationIconSlots}/{result.TrackedCycles}",
			$"  retained native navigation icon bytes: {result.RetainedNativeIconBytes:N0}");
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
