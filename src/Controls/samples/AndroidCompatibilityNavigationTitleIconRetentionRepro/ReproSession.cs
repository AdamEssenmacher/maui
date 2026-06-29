#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Runtime;
using Android.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.Android.AppCompat;
using AView = Android.Views.View;
using AToolbar = AndroidX.AppCompat.Widget.Toolbar;

namespace AndroidCompatibilityNavigationTitleIconRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 96;
	const int IconWidth = 512;
	const int IconHeight = 512;
	const int BytesPerPixel = 4;
	const int PayloadBytesPerIcon = IconWidth * IconHeight * BytesPerPixel;

	static readonly MethodInfo UpdateToolbarMethod =
		typeof(NavigationPageRenderer).GetMethod("UpdateToolbar", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(nameof(NavigationPageRenderer), "UpdateToolbar");

	static readonly FieldInfo TitleIconViewField =
		typeof(NavigationPageRenderer).GetField("_titleIconView", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(nameof(NavigationPageRenderer), "_titleIconView");

	static readonly FieldInfo ToolbarField =
		typeof(NavigationPageRenderer).GetField("_toolbar", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(nameof(NavigationPageRenderer), "_toolbar");

	static readonly FieldInfo ToolbarTrackerField =
		typeof(NavigationPageRenderer).GetField("_toolbarTracker", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(nameof(NavigationPageRenderer), "_toolbarTracker");

	static readonly Type ToolbarTrackerType =
		typeof(NavigationPage).Assembly.GetType("Microsoft.Maui.Controls.ToolbarTracker", throwOnError: true)
		?? throw new InvalidOperationException("Could not resolve ToolbarTracker.");

	static readonly List<RetainedNativeImageView> RetainedNativeTitleIconViews = new();

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		var androidContext = context.Context
			?? throw new InvalidOperationException("Expected an Android context.");
		RetainedNativeTitleIconViews.Clear();

		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			"control: clear native NavigationPage title-icon drawable before renderer disposal",
			context,
			clearNativeTitleIcon: true);

		var current = await RunScenarioAsync(
			"current: NavigationPageRenderer disposal leaves native title-icon drawable assigned",
			context,
			clearNativeTitleIcon: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeTitleIconViews);

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
		string name,
		IMauiContext context,
		bool clearNativeTitleIcon)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			await CreateCycleAsync(context, i, tracked, clearNativeTitleIcon);

			if (i % 12 == 0)
				await Task.Yield();
		}

		await DrainAsync();
		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	static async Task DrainAsync()
	{
		for (var i = 0; i < 4; i++)
		{
			await Task.Delay(250);
			ForceFullGc();
		}
	}

	static async Task CreateCycleAsync(
		IMauiContext context,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeTitleIcon)
	{
		var androidContext = context.Context
			?? throw new InvalidOperationException("Expected an Android context.");
		var imagePath = CreatePayloadImageFile(androidContext, cycle);
		var titleIcon = ImageSource.FromFile(imagePath);
		var page = new ContentPage
		{
			Title = $"Compatibility title page {cycle:D4}"
		};
		NavigationPage.SetTitleIconImageSource(page, titleIcon);

		var navPage = new NavigationPage(page);
		var navHandler = new FakeViewHandler(androidContext);
		navHandler.SetMauiContext(context);
		navHandler.SetVirtualView(navPage);

		var pageHandler = new FakeViewHandler(androidContext);
		pageHandler.SetMauiContext(context);
		pageHandler.SetVirtualView(page);

		var renderer = new NavigationPageRenderer(androidContext);
		SeedToolbar(renderer, androidContext);
		renderer.SetElement(navPage);
		var titleIconView = await WaitForTitleIconViewAsync(renderer);
		var retainedTitleIconView = RetainedNativeImageView.Create(titleIconView);

		if (clearNativeTitleIcon)
			titleIconView.SetImageDrawable(null);

		renderer.Dispose();
		navHandler.DisconnectHandler();
		pageHandler.DisconnectHandler();

		RetainedNativeTitleIconViews.Add(retainedTitleIconView);
		tracked.Add(TrackedCycle.Create(cycle, retainedTitleIconView, renderer, navPage, page, titleIcon, navHandler, pageHandler));
	}

	static void SeedToolbar(NavigationPageRenderer renderer, Context context)
	{
		var toolbar = new AToolbar(context);
		renderer.AddView(toolbar);
		ToolbarField.SetValue(renderer, toolbar);
		ToolbarTrackerField.SetValue(
			renderer,
			Activator.CreateInstance(ToolbarTrackerType, nonPublic: true));
	}

	static async Task<ImageView> WaitForTitleIconViewAsync(NavigationPageRenderer renderer)
	{
		for (var i = 0; i < 80; i++)
		{
			UpdateToolbarMethod.Invoke(renderer, null);

			if (TitleIconViewField.GetValue(renderer) is ImageView imageView &&
				GetDrawableByteCount(imageView.Drawable) >= PayloadBytesPerIcon)
			{
				return imageView;
			}

			await Task.Delay(25);
		}

		throw new InvalidOperationException("Could not find the compatibility NavigationPage title-icon ImageView with an assigned payload drawable.");
	}

	static string CreatePayloadImageFile(Context context, int cycle)
	{
		var cacheDir = context.CacheDir?.AbsolutePath
			?? throw new InvalidOperationException("Android cache directory is unavailable.");
		var imageDir = System.IO.Path.Combine(cacheDir, "navigation-title-icon-retention");
		Directory.CreateDirectory(imageDir);
		var imagePath = System.IO.Path.Combine(imageDir, $"title-icon-{cycle:D4}.png");

		using var bitmap = Bitmap.CreateBitmap(IconWidth, IconHeight, Bitmap.Config.Argb8888!);
		var color = Color.Argb(
			255,
			(cycle * 37) % 255,
			(cycle * 67) % 255,
			(cycle * 97) % 255);
		bitmap.EraseColor(color);

		using var stream = File.Create(imagePath);
		if (!bitmap.Compress(Bitmap.CompressFormat.Png!, 100, stream))
			throw new InvalidOperationException("Failed to encode generated title-icon payload.");

		return imagePath;
	}

	static long GetDrawableByteCount(Drawable? drawable)
	{
		if (drawable is null)
			return 0;

		if (drawable is BitmapDrawable bitmapDrawable && bitmapDrawable.Bitmap is Bitmap bitmap)
			return bitmap.AllocationByteCount;

		var width = Math.Max(0, drawable.IntrinsicWidth);
		var height = Math.Max(0, drawable.IntrinsicHeight);
		return width > 0 && height > 0 ? (long)width * height * BytesPerPixel : PayloadBytesPerIcon;
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

	internal sealed class RetainedNativeImageView
	{
		readonly IntPtr _globalHandle;

		RetainedNativeImageView(IntPtr globalHandle)
		{
			_globalHandle = globalHandle;
		}

		public static RetainedNativeImageView Create(ImageView imageView)
		{
			if (imageView.Handle == IntPtr.Zero)
				throw new InvalidOperationException("Native ImageView does not expose a Java handle.");

			return new RetainedNativeImageView(JNIEnv.NewGlobalRef(imageView.Handle));
		}

		public ImageView CreateWrapper()
		{
			if (_globalHandle == IntPtr.Zero)
				throw new ObjectDisposedException(nameof(RetainedNativeImageView));

			return Java.Lang.Object.GetObject<ImageView>(_globalHandle, JniHandleOwnership.DoNotTransfer)
				?? throw new InvalidOperationException("Could not re-wrap retained native ImageView peer.");
		}
	}

	internal sealed class FakeViewHandler : IViewHandler
	{
		public FakeViewHandler(Context context)
		{
			PlatformView = new FrameLayout(context);
		}

		public object? PlatformView { get; }

		public object? ContainerView => null;

		public bool HasContainer { get; set; }

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
			if (VirtualView?.Handler == this)
				VirtualView.Handler = null;

			VirtualView = null;
		}

		public Microsoft.Maui.Graphics.Size GetDesiredSize(double widthConstraint, double heightConstraint) =>
			Microsoft.Maui.Graphics.Size.Zero;

		public void PlatformArrange(Microsoft.Maui.Graphics.Rect frame)
		{
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		RetainedNativeImageView NativeTitleIconView,
		WeakReference<NavigationPageRenderer> Renderer,
		WeakReference<NavigationPage> NavigationPage,
		WeakReference<Page> Page,
		WeakReference<ImageSource> TitleIconSource,
		WeakReference<FakeViewHandler> NavigationHandler,
		WeakReference<FakeViewHandler> PageHandler)
	{
		public static TrackedCycle Create(
			int cycle,
			RetainedNativeImageView nativeTitleIconView,
			NavigationPageRenderer renderer,
			NavigationPage navigationPage,
			Page page,
			ImageSource titleIconSource,
			FakeViewHandler navigationHandler,
			FakeViewHandler pageHandler)
		{
			return new TrackedCycle(
				cycle,
				nativeTitleIconView,
				new WeakReference<NavigationPageRenderer>(renderer),
				new WeakReference<NavigationPage>(navigationPage),
				new WeakReference<Page>(page),
				new WeakReference<ImageSource>(titleIconSource),
				new WeakReference<FakeViewHandler>(navigationHandler),
				new WeakReference<FakeViewHandler>(pageHandler));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeTitleIconViews,
		int AliveRenderers,
		int AliveNavigationPages,
		int AlivePages,
		int AliveTitleIconSources,
		int AliveNavigationHandlers,
		int AlivePageHandlers,
		int AssignedIconSlots,
		int PayloadSizedIconSlots,
		long RetainedNativeIconBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var rendererRefs = new List<WeakReference<NavigationPageRenderer>>();
			var navigationPageRefs = new List<WeakReference<NavigationPage>>();
			var pageRefs = new List<WeakReference<Page>>();
			var titleIconSourceRefs = new List<WeakReference<ImageSource>>();
			var navigationHandlerRefs = new List<WeakReference<FakeViewHandler>>();
			var pageHandlerRefs = new List<WeakReference<FakeViewHandler>>();
			var aliveNativeTitleIconViews = 0;
			var assignedIconSlots = 0;
			var payloadSizedIconSlots = 0;
			long retainedNativeIconBytes = 0;

			foreach (var cycle in tracked)
			{
				rendererRefs.Add(cycle.Renderer);
				navigationPageRefs.Add(cycle.NavigationPage);
				pageRefs.Add(cycle.Page);
				titleIconSourceRefs.Add(cycle.TitleIconSource);
				navigationHandlerRefs.Add(cycle.NavigationHandler);
				pageHandlerRefs.Add(cycle.PageHandler);

				var titleIconView = cycle.NativeTitleIconView.CreateWrapper();
				if (titleIconView.Handle != IntPtr.Zero)
				{
					aliveNativeTitleIconViews++;
					var iconBytes = GetDrawableByteCount(titleIconView.Drawable);

					if (iconBytes > 0)
						assignedIconSlots++;
					if (iconBytes >= PayloadBytesPerIcon)
						payloadSizedIconSlots++;

					retainedNativeIconBytes += iconBytes;
				}
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeTitleIconViews,
				CountAlive(rendererRefs),
				CountAlive(navigationPageRefs),
				CountAlive(pageRefs),
				CountAlive(titleIconSourceRefs),
				CountAlive(navigationHandlerRefs),
				CountAlive(pageHandlerRefs),
				assignedIconSlots,
				payloadSizedIconSlots,
				retainedNativeIconBytes);
		}
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
	public bool LeakProved =>
		Control.AliveNativeTitleIconViews == Cycles &&
		Current.AliveNativeTitleIconViews == Cycles &&
		Control.PayloadSizedIconSlots == 0 &&
		Current.PayloadSizedIconSlots == Cycles &&
		Current.AliveRenderers == 0 &&
		Current.AliveNavigationPages == 0 &&
		Current.AlivePages == 0 &&
		Current.AliveTitleIconSources == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidCompatibilityNavigationTitleIconRetentionRepro",
			$"Cycles: {Cycles}",
			$"Icon size: {IconWidth}x{IconHeight}",
			$"Payload bytes per native title icon: {PayloadBytesPerIcon:N0}",
			"Source path mirrored: Android compatibility NavigationPageRenderer.UpdateTitleIcon assigns TitleIconImageSource through ImageView.SetImageDrawable",
			"Retained peers: JNI global refs to native title-icon ImageViews only",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained native title-icon payload: {FormatBytes(Control.RetainedNativeIconBytes)}",
			$"Current retained native title-icon payload: {FormatBytes(Current.RetainedNativeIconBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  alive native title-icon ImageViews: {result.AliveNativeTitleIconViews}/{result.TrackedCycles}",
			$"  alive compatibility renderers: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  alive NavigationPages: {result.AliveNavigationPages}/{result.TrackedCycles}",
			$"  alive pages: {result.AlivePages}/{result.TrackedCycles}",
			$"  alive title-icon image sources: {result.AliveTitleIconSources}/{result.TrackedCycles}",
			$"  alive navigation fake handlers: {result.AliveNavigationHandlers}/{result.TrackedCycles}",
			$"  alive page fake handlers: {result.AlivePageHandlers}/{result.TrackedCycles}",
			$"  assigned native title-icon slots: {result.AssignedIconSlots}/{result.TrackedCycles}",
			$"  payload-sized native title-icon slots: {result.PayloadSizedIconSlots}/{result.TrackedCycles}",
			$"  retained native title-icon bytes: {result.RetainedNativeIconBytes:N0}");
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
