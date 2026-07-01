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
using Google.Android.Material.BottomNavigation;
using Google.Android.Material.Tabs;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.Android;
using Microsoft.Maui.Controls.Internals;
using Microsoft.Maui.Controls.Platform;
using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.AndroidSpecific;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Platform;
using AColor = Android.Graphics.Color;
using AView = Android.Views.View;
using ControlsTabbedPage = Microsoft.Maui.Controls.TabbedPage;
using LegacyPlatform = Microsoft.Maui.Controls.Compatibility.Platform.Android.Platform;
using LegacyTabbedPageRenderer = Microsoft.Maui.Controls.Compatibility.Platform.Android.AppCompat.TabbedPageRenderer;

namespace AndroidLegacyTabbedPageRendererBottomTabIconRetentionRepro;

internal static class ReproSession
{
	const int CyclesPerPlacement = 24;
	const int TabsPerPage = 4;
	internal const int IconWidth = 512;
	internal const int IconHeight = 512;
	const int BytesPerPixel = 4;
	const int PayloadBytesPerIcon = IconWidth * IconHeight * BytesPerPixel;

	static bool s_formsInitialized;

	static readonly FieldInfo RendererPlatformField =
		typeof(LegacyTabbedPageRenderer).GetField("_platform", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(LegacyTabbedPageRenderer).FullName, "_platform");

	static readonly FieldInfo TabLayoutField =
		typeof(LegacyTabbedPageRenderer).GetField("_tabLayout", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(LegacyTabbedPageRenderer).FullName, "_tabLayout");

	static readonly FieldInfo BottomNavigationViewField =
		typeof(LegacyTabbedPageRenderer).GetField("_bottomNavigationView", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(LegacyTabbedPageRenderer).FullName, "_bottomNavigationView");

	static readonly ConstructorInfo LegacyPlatformConstructor =
		typeof(LegacyPlatform).GetConstructor(
			BindingFlags.Instance | BindingFlags.NonPublic,
			binder: null,
			types: new[] { typeof(Context), typeof(bool) },
			modifiers: null)
		?? throw new MissingMethodException(typeof(LegacyPlatform).FullName, ".ctor(Context,bool)");

	static readonly MethodInfo LegacyPlatformDisposeMethod =
		typeof(LegacyPlatform).GetMethod("Dispose", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(LegacyPlatform).FullName, "Dispose");

	static readonly List<object> RetainedNativePeerRoots = new();

	static readonly IntPtr TabLayoutClass = JNIEnv.FindClass("com/google/android/material/tabs/TabLayout");
	static readonly IntPtr TabLayoutTabClass = JNIEnv.FindClass("com/google/android/material/tabs/TabLayout$Tab");
	static readonly IntPtr BottomNavigationViewClass = JNIEnv.FindClass("com/google/android/material/bottomnavigation/BottomNavigationView");
	static readonly IntPtr MenuClass = JNIEnv.FindClass("android/view/Menu");
	static readonly IntPtr MenuItemClass = JNIEnv.FindClass("android/view/MenuItem");
	static readonly IntPtr DrawableClass = JNIEnv.FindClass("android/graphics/drawable/Drawable");

	static readonly IntPtr GetTabCountMethod = JNIEnv.GetMethodID(TabLayoutClass, "getTabCount", "()I");
	static readonly IntPtr GetTabAtMethod = JNIEnv.GetMethodID(TabLayoutClass, "getTabAt", "(I)Lcom/google/android/material/tabs/TabLayout$Tab;");
	static readonly IntPtr GetTabIconMethod = JNIEnv.GetMethodID(TabLayoutTabClass, "getIcon", "()Landroid/graphics/drawable/Drawable;");
	static readonly IntPtr SetTabIconMethod = JNIEnv.GetMethodID(TabLayoutTabClass, "setIcon", "(Landroid/graphics/drawable/Drawable;)Lcom/google/android/material/tabs/TabLayout$Tab;");
	static readonly IntPtr GetMenuMethod = JNIEnv.GetMethodID(BottomNavigationViewClass, "getMenu", "()Landroid/view/Menu;");
	static readonly IntPtr MenuSizeMethod = JNIEnv.GetMethodID(MenuClass, "size", "()I");
	static readonly IntPtr MenuGetItemMethod = JNIEnv.GetMethodID(MenuClass, "getItem", "(I)Landroid/view/MenuItem;");
	static readonly IntPtr MenuItemGetIconMethod = JNIEnv.GetMethodID(MenuItemClass, "getIcon", "()Landroid/graphics/drawable/Drawable;");
	static readonly IntPtr MenuItemSetIconMethod = JNIEnv.GetMethodID(MenuItemClass, "setIcon", "(Landroid/graphics/drawable/Drawable;)Landroid/view/MenuItem;");
	static readonly IntPtr GetIntrinsicWidthMethod = JNIEnv.GetMethodID(DrawableClass, "getIntrinsicWidth", "()I");
	static readonly IntPtr GetIntrinsicHeightMethod = JNIEnv.GetMethodID(DrawableClass, "getIntrinsicHeight", "()I");

	public static async Task<ReproReport> RunAsync(AppCompatActivity activity)
	{
		EnsureFormsInitialized(activity);
		RetainedNativePeerRoots.Clear();

		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			activity,
			"control: clear native legacy tab/menu icon slots before renderer disposal",
			clearNativeIconSlots: true);

		var current = await RunScenarioAsync(
			activity,
			"current: dispose legacy TabbedPageRenderer without clearing native tab/menu icons",
			clearNativeIconSlots: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(forceFullCollection: true);

		GC.KeepAlive(RetainedNativePeerRoots);

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
		var retainedPeers = new List<NativePeerRoot>(CyclesPerPlacement);
		var tracked = new List<TrackedCycle>(CyclesPerPlacement);

		for (var i = 0; i < CyclesPerPlacement; i++)
		{
			await CreateCycleAsync(activity, TabPlacement.Bottom, i, retainedPeers, tracked, clearNativeIconSlots);

			if (i % 6 == 0)
				await Task.Yield();
		}

		RetainedNativePeerRoots.Add(retainedPeers);
		await Task.Delay(250);
		ForceFullGc();
		GC.KeepAlive(retainedPeers);

		return ScenarioResult.From(name, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static async Task CreateCycleAsync(
		AppCompatActivity activity,
		TabPlacement placement,
		int cycle,
		List<NativePeerRoot> retainedPeers,
		List<TrackedCycle> tracked,
		bool clearNativeIconSlots)
	{
		var services = new ReproServiceProvider(activity);
		var mauiContext = new MauiContext(services, activity);
		var handler = new FakeElementHandler(mauiContext);
		var legacyPlatform = CreateLegacyPlatform(activity);
		var renderer = new LegacyTabbedPageRenderer(activity);
		RendererPlatformField.SetValue(renderer, legacyPlatform);

		var tabbedPage = new ControlsTabbedPage
		{
			Title = $"Legacy transient tabs {placement} {cycle + 1:000}"
		};
		tabbedPage.On<Microsoft.Maui.Controls.PlatformConfiguration.Android>()
			.SetToolbarPlacement(ToolbarPlacement.Bottom);

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
		handler.SetVirtualView(tabbedPage);
		tabbedPage.Handler = handler;

		((IVisualElementRenderer)renderer).SetElement(tabbedPage);

		var nativePeer = NativePeerRoot.Create(placement, (BottomNavigationView?)BottomNavigationViewField.GetValue(renderer));

		await WaitForIconSlotsAsync(nativePeer, placement);

		var assignedBeforeCleanup = CaptureIconSlots(nativePeer).PayloadSlots >= TabsPerPage;

		if (clearNativeIconSlots)
			ClearNativeIconSlots(nativePeer);

		renderer.Dispose();
		LegacyPlatformDisposeMethod.Invoke(legacyPlatform, null);
		RendererPlatformField.SetValue(renderer, null);

		foreach (var page in pages)
		{
			page.Title = null;
			page.IconImageSource = null;
			page.Content = null;
		}

		tabbedPage.Children.Clear();
		tabbedPage.Handler = null!;
		handler.DisconnectHandler();

		retainedPeers.Add(nativePeer);
		tracked.Add(TrackedCycle.Create(
			placement,
			nativePeer,
			renderer,
			tabbedPage,
			handler,
			pages,
			iconSources,
			assignedBeforeCleanup));

		services = null!;
		mauiContext = null!;
		handler = null!;
		legacyPlatform = null!;
		renderer = null!;
		tabbedPage = null!;
		pages = null!;
		iconSources = null!;
	}

	static async Task WaitForIconSlotsAsync(NativePeerRoot nativePeer, TabPlacement placement)
	{
		IconSnapshot snapshot = IconSnapshot.Empty;

		for (var i = 0; i < 120; i++)
		{
			snapshot = CaptureIconSlots(nativePeer);
			if (snapshot.PayloadSlots >= TabsPerPage)
				return;

			await Task.Delay(25);
		}

		throw new InvalidOperationException(
			$"Expected {TabsPerPage} payload icon slots for legacy {placement} tabs, saw {snapshot.PayloadSlots}.");
	}

	static LegacyPlatform CreateLegacyPlatform(Context context) =>
		(LegacyPlatform)LegacyPlatformConstructor.Invoke(new object[] { context, true });

	static void EnsureFormsInitialized(Context context)
	{
		Registrar.Registered.Register(typeof(PayloadImageSource), typeof(PayloadImageSourceHandler));

		if (s_formsInitialized)
			return;

		var services = new ReproServiceProvider(context);
		var mauiContext = new MauiContext(services, context);
		Microsoft.Maui.Controls.Compatibility.Forms.Init(mauiContext);
		s_formsInitialized = true;
	}

	static IconSnapshot CaptureIconSlots(NativePeerRoot nativePeer) =>
		nativePeer.Placement == TabPlacement.Top
			? CaptureTopIconSlots(nativePeer.GlobalRef)
			: CaptureBottomIconSlots(nativePeer.GlobalRef);

	static IconSnapshot CaptureTopIconSlots(IntPtr tabLayout)
	{
		var assigned = 0;
		var payload = 0;
		long bytes = 0;
		var count = JNIEnv.CallIntMethod(tabLayout, GetTabCountMethod);

		for (var i = 0; i < count; i++)
		{
			var tab = JNIEnv.CallObjectMethod(tabLayout, GetTabAtMethod, new JValue(i));
			if (tab == IntPtr.Zero)
				continue;

			try
			{
				var icon = JNIEnv.CallObjectMethod(tab, GetTabIconMethod);
				try
				{
					AccumulateIcon(icon, ref assigned, ref payload, ref bytes);
				}
				finally
				{
					if (icon != IntPtr.Zero)
						JNIEnv.DeleteLocalRef(icon);
				}
			}
			finally
			{
				JNIEnv.DeleteLocalRef(tab);
			}
		}

		return new IconSnapshot(assigned, payload, bytes);
	}

	static IconSnapshot CaptureBottomIconSlots(IntPtr bottomNavigationView)
	{
		var assigned = 0;
		var payload = 0;
		long bytes = 0;
		var menu = JNIEnv.CallObjectMethod(bottomNavigationView, GetMenuMethod);
		if (menu == IntPtr.Zero)
			return IconSnapshot.Empty;

		try
		{
			var count = JNIEnv.CallIntMethod(menu, MenuSizeMethod);

			for (var i = 0; i < count; i++)
			{
				var item = JNIEnv.CallObjectMethod(menu, MenuGetItemMethod, new JValue(i));
				if (item == IntPtr.Zero)
					continue;

				try
				{
					var icon = JNIEnv.CallObjectMethod(item, MenuItemGetIconMethod);
					try
					{
						AccumulateIcon(icon, ref assigned, ref payload, ref bytes);
					}
					finally
					{
						if (icon != IntPtr.Zero)
							JNIEnv.DeleteLocalRef(icon);
					}
				}
				finally
				{
					JNIEnv.DeleteLocalRef(item);
				}
			}
		}
		finally
		{
			JNIEnv.DeleteLocalRef(menu);
		}

		return new IconSnapshot(assigned, payload, bytes);
	}

	static void AccumulateIcon(IntPtr icon, ref int assigned, ref int payload, ref long bytes)
	{
		if (icon == IntPtr.Zero)
			return;

		assigned++;

		var width = JNIEnv.CallIntMethod(icon, GetIntrinsicWidthMethod);
		var height = JNIEnv.CallIntMethod(icon, GetIntrinsicHeightMethod);
		var iconBytes = width > 0 && height > 0
			? (long)width * height * BytesPerPixel
			: PayloadBytesPerIcon;

		bytes += iconBytes;

		if (width >= IconWidth && height >= IconHeight)
			payload++;
	}

	static void ClearNativeIconSlots(NativePeerRoot nativePeer)
	{
		if (nativePeer.Placement == TabPlacement.Top)
			ClearTopIconSlots(nativePeer.GlobalRef);
		else
			ClearBottomIconSlots(nativePeer.GlobalRef);
	}

	static void ClearTopIconSlots(IntPtr tabLayout)
	{
		var count = JNIEnv.CallIntMethod(tabLayout, GetTabCountMethod);

		for (var i = 0; i < count; i++)
		{
			var tab = JNIEnv.CallObjectMethod(tabLayout, GetTabAtMethod, new JValue(i));
			if (tab == IntPtr.Zero)
				continue;

			try
			{
				var result = JNIEnv.CallObjectMethod(tab, SetTabIconMethod, new JValue(IntPtr.Zero));
				if (result != IntPtr.Zero)
					JNIEnv.DeleteLocalRef(result);
			}
			finally
			{
				JNIEnv.DeleteLocalRef(tab);
			}
		}
	}

	static void ClearBottomIconSlots(IntPtr bottomNavigationView)
	{
		var menu = JNIEnv.CallObjectMethod(bottomNavigationView, GetMenuMethod);
		if (menu == IntPtr.Zero)
			return;

		try
		{
			var count = JNIEnv.CallIntMethod(menu, MenuSizeMethod);

			for (var i = 0; i < count; i++)
			{
				var item = JNIEnv.CallObjectMethod(menu, MenuGetItemMethod, new JValue(i));
				if (item == IntPtr.Zero)
					continue;

				try
				{
					var result = JNIEnv.CallObjectMethod(item, MenuItemSetIconMethod, new JValue(IntPtr.Zero));
					if (result != IntPtr.Zero)
						JNIEnv.DeleteLocalRef(result);
				}
				finally
				{
					JNIEnv.DeleteLocalRef(item);
				}
			}
		}
		finally
		{
			JNIEnv.DeleteLocalRef(menu);
		}
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
		readonly Context _context;
		readonly PayloadImageSourceService _imageSourceService = new();

		public ReproServiceProvider(Context context)
		{
			_context = context;
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
			if (serviceType == typeof(Activity) && _context is Activity activity)
				return activity;
			if (serviceType == typeof(Context))
				return _context;
			if (serviceType == typeof(LayoutInflater))
				return LayoutInflater.From(_context);

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

	internal sealed record NativePeerRoot(TabPlacement Placement, IntPtr GlobalRef)
	{
		public static NativePeerRoot Create(TabPlacement placement, Java.Lang.Object? peer)
		{
			if (peer is null || peer.Handle == IntPtr.Zero)
				throw new InvalidOperationException($"Native legacy {placement} tab peer was not available before renderer disposal.");

			var globalRef = JNIEnv.NewGlobalRef(peer.Handle);
			if (globalRef == IntPtr.Zero)
				throw new InvalidOperationException($"Failed to create a JNI global reference for the native legacy {placement} tab peer.");

			return new NativePeerRoot(placement, globalRef);
		}
	}

	internal sealed record IconSnapshot(int AssignedSlots, int PayloadSlots, long RetainedBytes)
	{
		public static IconSnapshot Empty { get; } = new(0, 0, 0);
	}

	internal sealed record TrackedCycle(
		TabPlacement Placement,
		NativePeerRoot NativePeer,
		WeakReference<LegacyTabbedPageRenderer> ManagedRenderer,
		WeakReference<ControlsTabbedPage> TabbedPage,
		WeakReference<FakeElementHandler> Handler,
		IReadOnlyList<WeakReference<ContentPage>> Pages,
		IReadOnlyList<WeakReference<PayloadImageSource>> IconSources,
		bool AssignedBeforeCleanup)
	{
		public static TrackedCycle Create(
			TabPlacement placement,
			NativePeerRoot nativePeer,
			LegacyTabbedPageRenderer renderer,
			ControlsTabbedPage tabbedPage,
			FakeElementHandler handler,
			IReadOnlyList<ContentPage> pages,
			IReadOnlyList<PayloadImageSource> iconSources,
			bool assignedBeforeCleanup)
		{
			return new TrackedCycle(
				placement,
				nativePeer,
				new WeakReference<LegacyTabbedPageRenderer>(renderer),
				new WeakReference<ControlsTabbedPage>(tabbedPage),
				new WeakReference<FakeElementHandler>(handler),
				pages.Select(static page => new WeakReference<ContentPage>(page)).ToArray(),
				iconSources.Select(static iconSource => new WeakReference<PayloadImageSource>(iconSource)).ToArray(),
				assignedBeforeCleanup);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveTopTabPeers,
		int AliveBottomTabPeers,
		int AliveManagedRenderers,
		int AliveTabbedPages,
		int AliveHandlers,
		int AliveChildPages,
		int AliveIconSources,
		int AssignedBeforeCleanup,
		int AssignedTopIconSlots,
		int PayloadTopIconSlots,
		int AssignedBottomIconSlots,
		int PayloadBottomIconSlots,
		long RetainedNativeIconBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var rendererRefs = new List<WeakReference<LegacyTabbedPageRenderer>>();
			var tabbedPageRefs = new List<WeakReference<ControlsTabbedPage>>();
			var handlerRefs = new List<WeakReference<FakeElementHandler>>();
			var pageRefs = new List<WeakReference<ContentPage>>();
			var iconSourceRefs = new List<WeakReference<PayloadImageSource>>();

			var aliveTopPeers = 0;
			var aliveBottomPeers = 0;
			var assignedBeforeCleanup = 0;
			var assignedTop = 0;
			var payloadTop = 0;
			var assignedBottom = 0;
			var payloadBottom = 0;
			long retainedBytes = 0;

			foreach (var cycle in tracked)
			{
				rendererRefs.Add(cycle.ManagedRenderer);
				tabbedPageRefs.Add(cycle.TabbedPage);
				handlerRefs.Add(cycle.Handler);
				pageRefs.AddRange(cycle.Pages);
				iconSourceRefs.AddRange(cycle.IconSources);

				if (cycle.AssignedBeforeCleanup)
					assignedBeforeCleanup++;

				if (cycle.NativePeer.GlobalRef != IntPtr.Zero)
				{
					var snapshot = CaptureIconSlots(cycle.NativePeer);
					if (cycle.Placement == TabPlacement.Top)
					{
						aliveTopPeers++;
						assignedTop += snapshot.AssignedSlots;
						payloadTop += snapshot.PayloadSlots;
					}
					else
					{
						aliveBottomPeers++;
						assignedBottom += snapshot.AssignedSlots;
						payloadBottom += snapshot.PayloadSlots;
					}

					retainedBytes += snapshot.RetainedBytes;
				}
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveTopPeers,
				aliveBottomPeers,
				CountAlive(rendererRefs),
				CountAlive(tabbedPageRefs),
				CountAlive(handlerRefs),
				CountAlive(pageRefs),
				CountAlive(iconSourceRefs),
				assignedBeforeCleanup,
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

	public int BitmapLoads { get; set; }

	public override bool IsEmpty => false;
}

internal sealed class PayloadImageSourceHandler : IImageViewHandler, IImageSourceHandler
{
	public Task LoadImageAsync(ImageSource imagesource, ImageView imageView, CancellationToken cancellationToken = default)
	{
		return Task.CompletedTask;
	}

	public Task<Bitmap> LoadImageAsync(ImageSource imagesource, Context context, CancellationToken cancelationToken = default)
	{
		return Task.FromResult(CreateBitmap(imagesource));
	}

	internal static Bitmap CreateBitmap(ImageSource imageSource)
	{
		if (imageSource is not PayloadImageSource payloadSource)
			return null!;

		payloadSource.BitmapLoads++;
		var bitmap = Bitmap.CreateBitmap(ReproSession.IconWidth, ReproSession.IconHeight, Bitmap.Config.Argb8888!)
			?? throw new InvalidOperationException("Failed to create the legacy tab icon bitmap.");
		var placementOffset = payloadSource.Placement == ReproSession.TabPlacement.Top ? 43 : 163;
		bitmap.EraseColor(AColor.Argb(
			255,
			(placementOffset + payloadSource.Cycle * 37 + payloadSource.Tab * 13) % 255,
			(placementOffset + payloadSource.Cycle * 67 + payloadSource.Tab * 17) % 255,
			(placementOffset + payloadSource.Cycle * 97 + payloadSource.Tab * 19) % 255));

		return bitmap;
	}
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

		var bitmap = PayloadImageSourceHandler.CreateBitmap(payloadSource);
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
	public int ExpectedTopPayloadSlots => 0;

	public int ExpectedBottomPayloadSlots => CyclesPerPlacement * TabsPerPage;

	public long ExpectedCurrentRetainedPayloadBytes =>
		(long)ExpectedBottomPayloadSlots * PayloadBytesPerIcon;

	public bool LeakProved =>
		Control.AssignedBeforeCleanup == CyclesPerPlacement &&
		Current.AssignedBeforeCleanup == CyclesPerPlacement &&
		Control.AliveTopTabPeers == 0 &&
		Current.AliveTopTabPeers == 0 &&
		Control.AliveBottomTabPeers == CyclesPerPlacement &&
		Current.AliveBottomTabPeers == CyclesPerPlacement &&
		Control.PayloadTopIconSlots == 0 &&
		Control.PayloadBottomIconSlots == 0 &&
		Current.PayloadTopIconSlots == 0 &&
		Current.PayloadBottomIconSlots >= ExpectedBottomPayloadSlots &&
		Current.RetainedNativeIconBytes >= ExpectedCurrentRetainedPayloadBytes &&
		Control.AliveManagedRenderers == 0 &&
		Current.AliveManagedRenderers == 0 &&
		Control.AliveTabbedPages == 0 &&
		Current.AliveTabbedPages == 0 &&
		Control.AliveHandlers == 0 &&
		Current.AliveHandlers == 0 &&
		Control.AliveChildPages == 0 &&
		Current.AliveChildPages == 0 &&
		Control.AliveIconSources == 0 &&
		Current.AliveIconSources == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidLegacyTabbedPageRendererBottomTabIconRetentionRepro",
			$"Cycles per placement: {CyclesPerPlacement}",
			$"Tabs per transient legacy TabbedPageRenderer: {TabsPerPage}",
			$"Icon size: {IconWidth}x{IconHeight} ARGB ({FormatBytes(PayloadBytesPerIcon)})",
			$"Expected bottom-tab payload slots: {ExpectedBottomPayloadSlots}",
			$"Expected current retained native icon payload: {FormatBytes(ExpectedCurrentRetainedPayloadBytes)}",
			"Source path exercised: obsolete Android TabbedPageRenderer.SetElement -> SetupBottomNavigationView -> BottomNavigationView menu item icons",
			"Native roots retained after disposal: JNI global refs to BottomNavigationView peers only",
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
			$"  payload icons assigned before cleanup: {result.AssignedBeforeCleanup}/{result.TrackedCycles}",
			$"  retained native TabLayout peers: {result.AliveTopTabPeers}/0",
			$"  retained native BottomNavigationView peers: {result.AliveBottomTabPeers}/{result.TrackedCycles}",
			$"  alive managed renderer wrappers after full GC: {result.AliveManagedRenderers}/{result.TrackedCycles}",
			$"  alive TabbedPage roots after full GC: {result.AliveTabbedPages}/{result.TrackedCycles}",
			$"  alive fake handlers after full GC: {result.AliveHandlers}/{result.TrackedCycles}",
			$"  alive child pages after full GC: {result.AliveChildPages}/{result.TrackedCycles * 4}",
			$"  alive tab icon image sources after full GC: {result.AliveIconSources}/{result.TrackedCycles * 4}",
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
