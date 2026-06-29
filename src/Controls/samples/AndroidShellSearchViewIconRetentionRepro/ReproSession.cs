#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.AppCompat.Widget;
using AndroidX.DrawerLayout.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Platform.Compatibility;
using AColor = Android.Graphics.Color;
using AImageButton = Android.Widget.ImageButton;
using AToolbar = AndroidX.AppCompat.Widget.Toolbar;
using AView = Android.Views.View;

namespace AndroidShellSearchViewIconRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 96;
	const int IconWidth = 384;
	const int IconHeight = 384;
	const int BytesPerPixel = 4;
	const int PayloadBytesPerIcon = IconWidth * IconHeight * BytesPerPixel;
	const int IconsPerCycle = 3;

	static readonly string[] IconTags =
	[
		"SearchIcon",
		nameof(SearchHandler.ClearIcon),
		nameof(SearchHandler.ClearPlaceholderIcon)
	];

	static readonly List<RetainedNativeImageButton> RetainedNativeButtons = new();
	static readonly List<NoFilterAdapter> RetainedNoFilterAdapters = new();
	static readonly List<Filter> RetainedOriginalFilters = new();

	public static async Task<ReproReport> RunAsync(AppCompatActivity activity)
	{
		RetainedNativeButtons.Clear();
		RetainedNoFilterAdapters.Clear();
		RetainedOriginalFilters.Clear();
		EnsureCurrentApplication(activity);

		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			activity,
			"control: clear ShellSearchView native icon drawables before dispose",
			clearNativeIcons: true);

		var current = await RunScenarioAsync(
			activity,
			"current: ShellSearchView.Dispose() leaves native icon drawables assigned",
			clearNativeIcons: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeButtons);
		GC.KeepAlive(RetainedNoFilterAdapters);
		GC.KeepAlive(RetainedOriginalFilters);

		return new ReproReport(
			Cycles,
			IconWidth,
			IconHeight,
			PayloadBytesPerIcon,
			IconsPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		AppCompatActivity activity,
		string name,
		bool clearNativeIcons)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			await CreateCycleAsync(activity, i, tracked, clearNativeIcons);

			if (i % 16 == 0)
				await Task.Yield();
		}

		await Task.Delay(250);
		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static async Task CreateCycleAsync(
		AppCompatActivity activity,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeIcons)
	{
		var services = new ReproServiceProvider(activity);
		var mauiContext = new MauiContext(services, activity);
		var shell = new Shell();
		var shellHandler = new FakeElementHandler(mauiContext);
		shellHandler.SetVirtualView(shell);
		shell.Handler = shellHandler;

		var shellContext = new FakeShellContext(activity, shell);
		var searchView = new ShellSearchView(activity, shellContext);
		var searchHandler = new SearchHandler
		{
			Query = string.Empty,
			Placeholder = $"Search {cycle:D4}",
			QueryIcon = new PayloadImageSource(cycle, 0),
			ClearIcon = new PayloadImageSource(cycle, 1),
			ClearPlaceholderIcon = new PayloadImageSource(cycle, 2),
			FontSize = 14,
			SearchBoxVisibility = SearchBoxVisibility.Expanded,
			ShowsResults = false
		};

		searchView.SearchHandler = searchHandler;
		((IShellSearchView)searchView).LoadView();

		if (FindDescendant<AppCompatAutoCompleteTextView>(searchView) is { } textField)
		{
			await Task.Delay(50);
			var originalAdapter = NeutralizeSuggestionAdapter(textField);
			await Task.Delay(50);
			originalAdapter?.Dispose();
			await Task.Delay(1);
		}

		var iconButtons = FindIconButtons(searchView);
		await WaitForIconDrawablesAsync(iconButtons);

		if (clearNativeIcons)
			ClearNativeIcons(iconButtons);

		var retainedButtons = iconButtons
			.Select(RetainedNativeImageButton.Create)
			.ToArray();

		((IShellSearchView)searchView).Dispose();
		shell.Handler = null;
		shellHandler.DisconnectHandler();

		RetainedNativeButtons.AddRange(retainedButtons);

		tracked.Add(TrackedCycle.Create(
			retainedButtons,
			searchView,
			searchHandler,
			shell,
			shellHandler));

		services = null!;
		mauiContext = null!;
		shell = null!;
		shellHandler = null!;
		shellContext = null!;
		searchView = null!;
		searchHandler = null!;
	}

	static void EnsureCurrentApplication(AppCompatActivity activity)
	{
		var services = new ReproServiceProvider(activity);
		var mauiContext = new MauiContext(services, activity);
		var application = Microsoft.Maui.Controls.Application.Current ?? new Microsoft.Maui.Controls.Application();
		application.Handler = new FakeElementHandler(mauiContext);
		Microsoft.Maui.Controls.Application.SetCurrentApplication(application);
	}

	static IReadOnlyList<AImageButton> FindIconButtons(ShellSearchView searchView)
	{
		var buttons = new List<AImageButton>(IconsPerCycle);
		CollectIconButtons(searchView, buttons);

		var missingTags = IconTags
			.Where(tag => !buttons.Any(button => string.Equals(button.Tag?.ToString(), tag, StringComparison.Ordinal)))
			.ToArray();

		if (buttons.Count != IconsPerCycle || missingTags.Length != 0)
			throw new InvalidOperationException(
				$"Expected {IconsPerCycle} ShellSearchView icon buttons; found {buttons.Count}; missing [{string.Join(", ", missingTags)}].");

		return buttons;
	}

	static void CollectIconButtons(AView view, ICollection<AImageButton> buttons)
	{
		if (view is AImageButton imageButton &&
			IconTags.Contains(imageButton.Tag?.ToString(), StringComparer.Ordinal))
		{
			buttons.Add(imageButton);
		}

		if (view is ViewGroup viewGroup)
		{
			for (var i = 0; i < viewGroup.ChildCount; i++)
			{
				if (viewGroup.GetChildAt(i) is AView child)
					CollectIconButtons(child, buttons);
			}
		}
	}

	static async Task WaitForIconDrawablesAsync(IReadOnlyList<AImageButton> buttons)
	{
		for (var i = 0; i < 40; i++)
		{
			if (buttons.All(button => button.Drawable is not null))
				return;

			await Task.Delay(25);
		}
	}

	static void ClearNativeIcons(IEnumerable<AImageButton> buttons)
	{
		foreach (var button in buttons)
			button.SetImageDrawable(null);
	}

	static IListAdapter? NeutralizeSuggestionAdapter(AppCompatAutoCompleteTextView textField)
	{
		var originalAdapter = textField.Adapter;
		if (originalAdapter is IFilterable filterable && filterable.Filter is { } filter)
			RetainedOriginalFilters.Add(filter);

		var adapter = new NoFilterAdapter();
		RetainedNoFilterAdapters.Add(adapter);
		textField.Adapter = adapter;
		textField.Threshold = int.MaxValue;
		return originalAdapter;
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
					FindDescendant<T>(child) is { } descendant)
				{
					return descendant;
				}
			}
		}

		return null;
	}

	static IconSlotSnapshot CaptureIconSlot(RetainedNativeImageButton button)
	{
		using var wrapper = button.CreateWrapper();
		var bytes = GetDrawableByteCount(wrapper.Drawable);

		return new IconSlotSnapshot(
			bytes > 0 ? 1 : 0,
			bytes >= PayloadBytesPerIcon ? 1 : 0,
			bytes);
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

	sealed class ReproServiceProvider : IServiceProvider
	{
		readonly AppCompatActivity _activity;
		readonly ReproFontManager _fontManager = new();
		readonly PayloadImageSourceService _payloadImageSourceService = new();
		readonly ReproImageSourceServiceProvider _imageSourceServiceProvider;

		public ReproServiceProvider(AppCompatActivity activity)
		{
			_activity = activity;
			_imageSourceServiceProvider = new ReproImageSourceServiceProvider(this, _payloadImageSourceService);
		}

		public object? GetService(Type serviceType)
		{
			if (serviceType == typeof(IFontManager))
				return _fontManager;
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

	sealed class ReproFontManager : IFontManager
	{
		public double DefaultFontSize => 14;

		public Typeface DefaultTypeface => Typeface.Default!;

		public Typeface? GetTypeface(Microsoft.Maui.Font font) => DefaultTypeface;

		public FontSize GetFontSize(Microsoft.Maui.Font font, float defaultFontSize = 0)
		{
			var size = font.Size > 0 && !double.IsNaN(font.Size)
				? (float)font.Size
				: (defaultFontSize > 0 ? defaultFontSize : (float)DefaultFontSize);

			return new FontSize(size, font.AutoScalingEnabled ? ComplexUnitType.Sp : ComplexUnitType.Dip);
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

	sealed class NoFilterAdapter : BaseAdapter, IFilterable
	{
		readonly Filter _filter = new NoopFilter();

		public override int Count => 0;

		public Filter Filter => _filter;

		public override Java.Lang.Object? GetItem(int position) => null;

		public override long GetItemId(int position) => -1;

		public override AView GetView(int position, AView? convertView, ViewGroup? parent) =>
			throw new NotSupportedException();

		protected override void Dispose(bool disposing)
		{
		}
	}

	sealed class NoopFilter : Filter
	{
		protected override FilterResults PerformFiltering(Java.Lang.ICharSequence? constraint) => new()
		{
			Count = 0
		};

		protected override void PublishResults(Java.Lang.ICharSequence? constraint, FilterResults? results)
		{
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

	internal sealed record IconSlotSnapshot(int AssignedSlots, int PayloadSlots, long RetainedBytes);

	internal sealed class RetainedNativeImageButton
	{
		readonly IntPtr _globalHandle;

		RetainedNativeImageButton(IntPtr globalHandle)
		{
			_globalHandle = globalHandle;
		}

		public bool IsAlive => _globalHandle != IntPtr.Zero;

		public static RetainedNativeImageButton Create(AImageButton button)
		{
			var globalHandle = JNIEnv.NewGlobalRef(button.Handle);
			return new RetainedNativeImageButton(globalHandle);
		}

		public AImageButton CreateWrapper()
		{
			if (_globalHandle == IntPtr.Zero)
				throw new ObjectDisposedException(nameof(RetainedNativeImageButton));

			return Java.Lang.Object.GetObject<AImageButton>(_globalHandle, JniHandleOwnership.DoNotTransfer)
				?? throw new InvalidOperationException("Could not re-wrap retained native ImageButton peer.");
		}
	}

	internal sealed record TrackedCycle(
		IReadOnlyList<RetainedNativeImageButton> NativeButtons,
		WeakReference<ShellSearchView> SearchView,
		WeakReference<SearchHandler> SearchHandler,
		WeakReference<Shell> Shell,
		WeakReference<FakeElementHandler> ShellHandler)
	{
		public static TrackedCycle Create(
			IReadOnlyList<RetainedNativeImageButton> nativeButtons,
			ShellSearchView searchView,
			SearchHandler searchHandler,
			Shell shell,
			FakeElementHandler shellHandler)
		{
			return new TrackedCycle(
				nativeButtons,
				new WeakReference<ShellSearchView>(searchView),
				new WeakReference<SearchHandler>(searchHandler),
				new WeakReference<Shell>(shell),
				new WeakReference<FakeElementHandler>(shellHandler));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeButtons,
		int AliveSearchViews,
		int AliveSearchHandlers,
		int AliveShells,
		int AliveShellHandlers,
		int AssignedIconSlots,
		int PayloadSizedIconSlots,
		long RetainedNativeIconBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var searchViewRefs = new List<WeakReference<ShellSearchView>>();
			var searchHandlerRefs = new List<WeakReference<SearchHandler>>();
			var shellRefs = new List<WeakReference<Shell>>();
			var shellHandlerRefs = new List<WeakReference<FakeElementHandler>>();

			var aliveNativeButtons = 0;
			var assignedSlots = 0;
			var payloadSlots = 0;
			long retainedBytes = 0;

			foreach (var cycle in tracked)
			{
				searchViewRefs.Add(cycle.SearchView);
				searchHandlerRefs.Add(cycle.SearchHandler);
				shellRefs.Add(cycle.Shell);
				shellHandlerRefs.Add(cycle.ShellHandler);

				foreach (var button in cycle.NativeButtons)
				{
					if (!button.IsAlive)
						continue;

					aliveNativeButtons++;
					var snapshot = CaptureIconSlot(button);
					assignedSlots += snapshot.AssignedSlots;
					payloadSlots += snapshot.PayloadSlots;
					retainedBytes += snapshot.RetainedBytes;
				}
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeButtons,
				CountAlive(searchViewRefs),
				CountAlive(searchHandlerRefs),
				CountAlive(shellRefs),
				CountAlive(shellHandlerRefs),
				assignedSlots,
				payloadSlots,
				retainedBytes);
		}
	}
}

internal sealed class PayloadImageSource : ImageSource
{
	public PayloadImageSource(int cycle, int slot)
	{
		Cycle = cycle;
		Slot = slot;
	}

	public int Cycle { get; }

	public int Slot { get; }
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

		var bitmap = Bitmap.CreateBitmap(ReproSessionReport.IconWidth, ReproSessionReport.IconHeight, Bitmap.Config.Argb8888!);
		var color = AColor.Argb(
			255,
			(payloadSource.Cycle * 37 + payloadSource.Slot * 53) % 255,
			(payloadSource.Cycle * 67 + payloadSource.Slot * 29) % 255,
			(payloadSource.Cycle * 97 + payloadSource.Slot * 71) % 255);
		bitmap.EraseColor(color);

		Drawable drawable = new BitmapDrawable(context.Resources, bitmap);
		return Task.FromResult<IImageSourceServiceResult<Drawable>?>(new ImageSourceServiceResult(drawable));
	}
}

static class ReproSessionReport
{
	public const int IconWidth = 384;
	public const int IconHeight = 384;
}

internal sealed record ReproReport(
	int Cycles,
	int IconWidth,
	int IconHeight,
	int PayloadBytesPerIcon,
	int IconsPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public int ExpectedIconSlots => Cycles * IconsPerCycle;

	public bool LeakProved =>
		Control.AliveNativeButtons == ExpectedIconSlots &&
		Current.AliveNativeButtons == ExpectedIconSlots &&
		Control.PayloadSizedIconSlots == 0 &&
		Current.PayloadSizedIconSlots == ExpectedIconSlots &&
		Current.AliveSearchHandlers == 0 &&
		Current.AliveShells == 0 &&
		Current.AliveShellHandlers == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidShellSearchViewIconRetentionRepro",
			$"Cycles: {Cycles}",
			$"Icons per cycle: {IconsPerCycle}",
			$"Icon size: {IconWidth}x{IconHeight}",
			$"Payload bytes per native icon: {PayloadBytesPerIcon:N0}",
			$"Expected native icon slots: {ExpectedIconSlots}",
			$"Search suggestion adapter neutralized in both runs to isolate icon drawable cleanup",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control, ExpectedIconSlots),
			string.Empty,
			Format(Current, ExpectedIconSlots),
			string.Empty,
			$"Control retained native icon payload: {FormatBytes(Control.RetainedNativeIconBytes)}",
			$"Current retained native icon payload: {FormatBytes(Current.RetainedNativeIconBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result, int expectedIconSlots)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  alive native ImageButton peers: {result.AliveNativeButtons}/{expectedIconSlots}",
			$"  alive ShellSearchView instances: {result.AliveSearchViews}/{result.TrackedCycles}",
			$"  alive SearchHandlers: {result.AliveSearchHandlers}/{result.TrackedCycles}",
			$"  alive Shells: {result.AliveShells}/{result.TrackedCycles}",
			$"  alive fake Shell handlers: {result.AliveShellHandlers}/{result.TrackedCycles}",
			$"  assigned native icon slots: {result.AssignedIconSlots}/{expectedIconSlots}",
			$"  payload-sized native icon slots: {result.PayloadSizedIconSlots}/{expectedIconSlots}",
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
