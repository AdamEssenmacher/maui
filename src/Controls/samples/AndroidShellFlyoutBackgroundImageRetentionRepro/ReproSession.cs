#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.AppCompat.Widget;
using AndroidX.DrawerLayout.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Platform.Compatibility;
using AColor = Android.Graphics.Color;
using AToolbar = AndroidX.AppCompat.Widget.Toolbar;
using AView = Android.Views.View;

namespace AndroidShellFlyoutBackgroundImageRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 96;
	internal const int BitmapEdge = 512;
	const int BytesPerPixel = 4;
	internal const int PayloadBytesPerDrawable = BitmapEdge * BitmapEdge * BytesPerPixel;

	static readonly BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;
	static readonly MethodInfo OnFlyoutStateChangingMethod = typeof(ShellFlyoutTemplatedContentRenderer).GetMethod("OnFlyoutStateChanging", InstanceNonPublic)
		?? throw new MissingMethodException(typeof(ShellFlyoutTemplatedContentRenderer).FullName, "OnFlyoutStateChanging");
	static readonly FieldInfo BackgroundImageViewField = typeof(ShellFlyoutTemplatedContentRenderer).GetField("_bgImage", InstanceNonPublic)
		?? throw new MissingFieldException(typeof(ShellFlyoutTemplatedContentRenderer).FullName, "_bgImage");
	static readonly FieldInfo RootViewField = typeof(ShellFlyoutTemplatedContentRenderer).GetField("_rootView", InstanceNonPublic)
		?? throw new MissingFieldException(typeof(ShellFlyoutTemplatedContentRenderer).FullName, "_rootView");
	static readonly FieldInfo FlyoutContentViewField = typeof(ShellFlyoutTemplatedContentRenderer).GetField("_flyoutContentView", InstanceNonPublic)
		?? throw new MissingFieldException(typeof(ShellFlyoutTemplatedContentRenderer).FullName, "_flyoutContentView");

	static readonly List<RetainedNativeImageView> RetainedNativeImageViews = new();

	public static async Task<ReproReport> RunAsync(AppCompatActivity activity)
	{
		RetainedNativeImageViews.Clear();
		EnsureCurrentApplication(activity);
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			activity,
			"control: clear native Shell flyout background ImageView drawable before renderer disposal",
			clearNativeDrawable: true);

		var current = await RunScenarioAsync(
			activity,
			"current: ShellFlyoutTemplatedContentRenderer.Dispose() leaves background ImageView drawable assigned",
			clearNativeDrawable: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeImageViews);

		return new ReproReport(
			Cycles,
			BitmapEdge,
			PayloadBytesPerDrawable,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		AppCompatActivity activity,
		string name,
		bool clearNativeDrawable)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			await CreateCycleAsync(activity, i, tracked, clearNativeDrawable);

			if (i % 16 == 0)
				await Task.Yield();
		}

		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	static async Task CreateCycleAsync(
		AppCompatActivity activity,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeDrawable)
	{
		var services = new ReproServiceProvider(activity);
		var mauiContext = new MauiContext(services, activity);
		var shell = new Shell
		{
			FlyoutBackgroundImage = new PayloadImageSource(cycle),
			FlyoutBackgroundImageAspect = Aspect.AspectFill,
			FlyoutBehavior = FlyoutBehavior.Flyout
		};
		var imageSource = (PayloadImageSource)shell.FlyoutBackgroundImage;
		var shellHandler = new FakeElementHandler(mauiContext);
		shellHandler.SetVirtualView(shell);
		shell.Handler = shellHandler;

		var shellContext = new FakeShellContext(activity, shell);
		var renderer = new ProbeShellFlyoutTemplatedContentRenderer(shellContext);
		var imageView = GetBackgroundImageView(renderer);

		await WaitForDrawableAsync(imageView);
		DispatchGlobalLayouts(renderer.AndroidView, count: 2);
		RemoveDrawerStateChangedHandler(renderer, shellContext.CurrentDrawerLayout);

		var assignedDrawable = imageView.Drawable;
		if (GetDrawableByteCount(assignedDrawable) < PayloadBytesPerDrawable)
			throw new InvalidOperationException("The Shell flyout background image did not retain the generated payload drawable.");

		if (imageView.Parent is ViewGroup parent)
			parent.RemoveView(imageView);

		if (clearNativeDrawable)
			ClearNativeDrawable(imageView);

		var retainedImageView = RetainedNativeImageView.Create(imageView);
		RetainedNativeImageViews.Add(retainedImageView);

		renderer.Dispose();
		shell.FlyoutBackgroundImage = null;
		shell.Handler = null;
		shellHandler.DisconnectHandler();
		shellContext.Dispose();

		tracked.Add(TrackedCycle.Create(cycle, retainedImageView, renderer, shell, shellHandler, imageSource));

		services = null!;
		mauiContext = null!;
		shell = null!;
		shellHandler = null!;
		shellContext = null!;
		renderer = null!;
		imageSource = null!;
	}

	static void EnsureCurrentApplication(AppCompatActivity activity)
	{
		var services = new ReproServiceProvider(activity);
		var mauiContext = new MauiContext(services, activity);
		var application = Microsoft.Maui.Controls.Application.Current ?? new Microsoft.Maui.Controls.Application();
		application.Handler = new FakeElementHandler(mauiContext);
		Microsoft.Maui.Controls.Application.SetCurrentApplication(application);
	}

	static ImageView GetBackgroundImageView(ShellFlyoutTemplatedContentRenderer renderer)
	{
		return BackgroundImageViewField.GetValue(renderer) as ImageView
			?? throw new InvalidOperationException("Could not read ShellFlyoutTemplatedContentRenderer._bgImage.");
	}

	static async Task WaitForDrawableAsync(ImageView imageView)
	{
		for (var i = 0; i < 40; i++)
		{
			if (GetDrawableByteCount(imageView.Drawable) >= PayloadBytesPerDrawable)
				return;

			await Task.Delay(25);
		}
	}

	static void DispatchGlobalLayouts(AView rootView, int count)
	{
		for (var i = 0; i < count; i++)
			rootView.ViewTreeObserver?.DispatchOnGlobalLayout();
	}

	static void RemoveDrawerStateChangedHandler(
		ShellFlyoutTemplatedContentRenderer renderer,
		DrawerLayout drawerLayout)
	{
		var handler = (EventHandler<DrawerLayout.DrawerStateChangedEventArgs>)Delegate.CreateDelegate(
			typeof(EventHandler<DrawerLayout.DrawerStateChangedEventArgs>),
			renderer,
			OnFlyoutStateChangingMethod);

		drawerLayout.DrawerStateChanged -= handler;
	}

	static void ClearNativeDrawable(ImageView imageView)
	{
		var drawable = imageView.Drawable;
		imageView.SetImageDrawable(null);
		drawable?.Dispose();
	}

	static long GetDrawableByteCount(Drawable? drawable)
	{
		if (drawable is BitmapDrawable bitmapDrawable && bitmapDrawable.Bitmap is Bitmap bitmap && !bitmap.IsRecycled)
			return bitmap.AllocationByteCount;

		return drawable is null ? 0 : PayloadBytesPerDrawable;
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
		readonly PayloadImageSourceService _payloadImageSourceService = new();
		readonly ReproImageSourceServiceProvider _imageSourceServiceProvider;

		public ReproServiceProvider(AppCompatActivity activity)
		{
			_activity = activity;
			_imageSourceServiceProvider = new ReproImageSourceServiceProvider(this, _payloadImageSourceService);
		}

		public object? GetService(Type serviceType)
		{
			if (serviceType == typeof(IImageSourceServiceProvider))
				return _imageSourceServiceProvider;
			if (serviceType == typeof(Activity))
				return _activity;
			if (serviceType == typeof(Context))
				return _activity;
			if (serviceType == typeof(LayoutInflater))
				return LayoutInflater.From(_activity);

			return null;
		}
	}

	sealed class ReproImageSourceServiceProvider : IImageSourceServiceProvider
	{
		readonly PayloadImageSourceService _payloadImageSourceService;

		public ReproImageSourceServiceProvider(IServiceProvider hostServiceProvider, PayloadImageSourceService payloadImageSourceService)
		{
			HostServiceProvider = hostServiceProvider;
			_payloadImageSourceService = payloadImageSourceService;
		}

		public IServiceProvider HostServiceProvider { get; }

		public IImageSourceService? GetImageSourceService(Type imageSource) =>
			imageSource == typeof(PayloadImageSource) ? _payloadImageSourceService : null;

		public object? GetService(Type serviceType) =>
			serviceType == typeof(IImageSourceServiceProvider)
				? this
				: HostServiceProvider.GetService(serviceType);
	}

	sealed class FakeShellContext : IShellContext, IDisposable
	{
		public FakeShellContext(Context context, Shell shell)
		{
			AndroidContext = context;
			Shell = shell;
			CurrentDrawerLayout = new DrawerLayout(context);
		}

		public Context AndroidContext { get; }

		public DrawerLayout CurrentDrawerLayout { get; }

		public Shell Shell { get; }

		public IShellObservableFragment CreateFragmentForPage(Page page) => throw new NotSupportedException();

		public IShellFlyoutContentRenderer CreateShellFlyoutContentRenderer() => throw new NotSupportedException();

		public IShellItemRenderer CreateShellItemRenderer(ShellItem shellItem) => throw new NotSupportedException();

		public IShellSectionRenderer CreateShellSectionRenderer(ShellSection shellSection) => throw new NotSupportedException();

		public IShellToolbarTracker CreateTrackerForToolbar(AToolbar toolbar) => throw new NotSupportedException();

		public IShellToolbarAppearanceTracker CreateToolbarAppearanceTracker() => throw new NotSupportedException();

		public IShellTabLayoutAppearanceTracker CreateTabLayoutAppearanceTracker(ShellSection shellSection) => throw new NotSupportedException();

		public IShellBottomNavViewAppearanceTracker CreateBottomNavViewAppearanceTracker(ShellItem shellItem) => throw new NotSupportedException();

		public void Dispose()
		{
			CurrentDrawerLayout.Dispose();
		}
	}

	sealed class ProbeShellFlyoutTemplatedContentRenderer : ShellFlyoutTemplatedContentRenderer
	{
		public ProbeShellFlyoutTemplatedContentRenderer(IShellContext shellContext)
			: base(shellContext)
		{
		}

		protected override void UpdateFlyoutContent()
		{
			if (FlyoutContentViewField.GetValue(this) is not null)
				return;

			if (RootViewField.GetValue(this) is not ViewGroup rootView)
				return;

			var context = rootView.Context
				?? throw new InvalidOperationException("Shell flyout root view has no Android context.");
			var dummyFlyoutContent = new FrameLayout(context)
			{
				LayoutParameters = new ViewGroup.LayoutParams(1, 1)
			};

			FlyoutContentViewField.SetValue(this, dummyFlyoutContent);
			rootView.AddView(dummyFlyoutContent);
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

		public Microsoft.Maui.Graphics.Size GetDesiredSize(double widthConstraint, double heightConstraint) =>
			Microsoft.Maui.Graphics.Size.Zero;

		public void PlatformArrange(Microsoft.Maui.Graphics.Rect frame)
		{
		}
	}

	internal sealed class RetainedNativeImageView
	{
		readonly IntPtr _globalHandle;

		RetainedNativeImageView(IntPtr globalHandle)
		{
			_globalHandle = globalHandle;
		}

		public bool IsAlive => _globalHandle != IntPtr.Zero;

		public static RetainedNativeImageView Create(ImageView imageView)
		{
			var globalHandle = JNIEnv.NewGlobalRef(imageView.Handle);
			return new RetainedNativeImageView(globalHandle);
		}

		public ImageView CreateWrapper()
		{
			if (_globalHandle == IntPtr.Zero)
				throw new ObjectDisposedException(nameof(RetainedNativeImageView));

			return Java.Lang.Object.GetObject<ImageView>(_globalHandle, JniHandleOwnership.DoNotTransfer)
				?? throw new InvalidOperationException("Could not re-wrap retained native ImageView peer.");
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		RetainedNativeImageView NativeImageView,
		WeakReference<ShellFlyoutTemplatedContentRenderer> Renderer,
		WeakReference<Shell> Shell,
		WeakReference<FakeElementHandler> ShellHandler,
		WeakReference<PayloadImageSource> ImageSource)
	{
		public static TrackedCycle Create(
			int cycle,
			RetainedNativeImageView nativeImageView,
			ShellFlyoutTemplatedContentRenderer renderer,
			Shell shell,
			FakeElementHandler shellHandler,
			PayloadImageSource imageSource)
		{
			return new TrackedCycle(
				cycle,
				nativeImageView,
				new WeakReference<ShellFlyoutTemplatedContentRenderer>(renderer),
				new WeakReference<Shell>(shell),
				new WeakReference<FakeElementHandler>(shellHandler),
				new WeakReference<PayloadImageSource>(imageSource));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeImageViews,
		int AliveRenderers,
		int AliveShells,
		int AliveShellHandlers,
		int AliveImageSources,
		int AssignedDrawableSlots,
		int PayloadSizedDrawableSlots,
		long RetainedNativeDrawableBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var rendererRefs = new List<WeakReference<ShellFlyoutTemplatedContentRenderer>>();
			var shellRefs = new List<WeakReference<Shell>>();
			var shellHandlerRefs = new List<WeakReference<FakeElementHandler>>();
			var imageSourceRefs = new List<WeakReference<PayloadImageSource>>();
			var aliveNativeImageViews = 0;
			var assignedSlots = 0;
			var payloadSlots = 0;
			long retainedBytes = 0;

			foreach (var cycle in tracked)
			{
				rendererRefs.Add(cycle.Renderer);
				shellRefs.Add(cycle.Shell);
				shellHandlerRefs.Add(cycle.ShellHandler);
				imageSourceRefs.Add(cycle.ImageSource);

				if (!cycle.NativeImageView.IsAlive)
					continue;

				aliveNativeImageViews++;
				using var wrapper = cycle.NativeImageView.CreateWrapper();
				var drawableBytes = GetDrawableByteCount(wrapper.Drawable);

				if (drawableBytes > 0)
					assignedSlots++;
				if (drawableBytes >= PayloadBytesPerDrawable)
					payloadSlots++;

				retainedBytes += drawableBytes;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeImageViews,
				CountAlive(rendererRefs),
				CountAlive(shellRefs),
				CountAlive(shellHandlerRefs),
				CountAlive(imageSourceRefs),
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

		var bitmap = Bitmap.CreateBitmap(ReproSession.BitmapEdge, ReproSession.BitmapEdge, Bitmap.Config.Argb8888!);
		var color = AColor.Argb(
			255,
			(payloadSource.Cycle * 37) % 255,
			(payloadSource.Cycle * 67) % 255,
			(payloadSource.Cycle * 97) % 255);
		bitmap.EraseColor(color);

		Drawable drawable = new BitmapDrawable(context.Resources ?? Resources.System, bitmap);
		return Task.FromResult<IImageSourceServiceResult<Drawable>?>(new ImageSourceServiceResult(drawable, dispose: drawable.Dispose));
	}
}

internal sealed record ReproReport(
	int Cycles,
	int BitmapEdge,
	int PayloadBytesPerDrawable,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.AliveNativeImageViews == Cycles &&
		Current.AliveNativeImageViews == Cycles &&
		Control.PayloadSizedDrawableSlots == 0 &&
		Current.PayloadSizedDrawableSlots == Cycles &&
		Current.AliveRenderers == 0 &&
		Current.AliveShells == 0 &&
		Current.AliveShellHandlers == 0 &&
		Current.AliveImageSources == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidShellFlyoutBackgroundImageRetentionRepro",
			$"Cycles: {Cycles}",
			$"Bitmap edge: {BitmapEdge}px",
			$"Payload bytes per native drawable: {PayloadBytesPerDrawable:N0}",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained native drawable payload: {FormatBytes(Control.RetainedNativeDrawableBytes)}",
			$"Current retained native drawable payload: {FormatBytes(Current.RetainedNativeDrawableBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  alive native ImageView peers: {result.AliveNativeImageViews}/{result.TrackedCycles}",
			$"  alive ShellFlyoutTemplatedContentRenderers: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  alive Shells: {result.AliveShells}/{result.TrackedCycles}",
			$"  alive fake Shell handlers: {result.AliveShellHandlers}/{result.TrackedCycles}",
			$"  alive image sources: {result.AliveImageSources}/{result.TrackedCycles}",
			$"  assigned native drawable slots: {result.AssignedDrawableSlots}/{result.TrackedCycles}",
			$"  payload-sized native drawable slots: {result.PayloadSizedDrawableSlots}/{result.TrackedCycles}",
			$"  retained native drawable bytes: {result.RetainedNativeDrawableBytes:N0}");
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
