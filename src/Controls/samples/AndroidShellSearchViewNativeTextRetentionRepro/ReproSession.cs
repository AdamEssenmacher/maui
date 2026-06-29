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
using Android.Runtime;
using Android.Text;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.AppCompat.Widget;
using AndroidX.DrawerLayout.Widget;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Platform.Compatibility;
using Microsoft.Maui.Graphics;
using AToolbar = AndroidX.AppCompat.Widget.Toolbar;
using AView = Android.Views.View;

namespace AndroidShellSearchViewNativeTextRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 384;
	const int PayloadCharsPerSlot = 16 * 1024;
	const int PayloadBytesPerSlot = PayloadCharsPerSlot * sizeof(char);
	const string PayloadPrefix = "android-shell-searchview-native-text-";

	static readonly List<RetainedNativeTextField> RetainedNativeTextFields = new();
	static readonly List<NoFilterAdapter> RetainedNoFilterAdapters = new();
	static readonly List<Filter> RetainedOriginalFilters = new();

	public static async Task<ReproReport> RunAsync(AppCompatActivity activity)
	{
		RetainedNativeTextFields.Clear();
		RetainedNoFilterAdapters.Clear();
		RetainedOriginalFilters.Clear();
		EnsureCurrentApplication(activity);

		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			activity,
			"control: clear native EditText Text/Hint/ContentDescription before dispose",
			clearNativeTextSlots: true);

		var current = await RunScenarioAsync(
			activity,
			"current: ShellSearchView.Dispose() leaves native EditText text slots assigned",
			clearNativeTextSlots: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeTextFields);
		GC.KeepAlive(RetainedNoFilterAdapters);
		GC.KeepAlive(RetainedOriginalFilters);

		return new ReproReport(
			Cycles,
			PayloadCharsPerSlot,
			PayloadBytesPerSlot,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		AppCompatActivity activity,
		string name,
		bool clearNativeTextSlots)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			await CreateCycleAsync(activity, i, tracked, clearNativeTextSlots);

			if (i % 32 == 0)
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
		bool clearNativeTextSlots)
	{
		var services = new ReproServiceProvider(activity);
		var mauiContext = new MauiContext(services, activity);
		var shell = new Shell();
		var shellHandler = new FakeElementHandler(mauiContext);
		shellHandler.SetVirtualView(shell);
		shell.Handler = shellHandler;

		var shellContext = new FakeShellContext(activity, shell);
		var searchView = new ShellSearchView(activity, shellContext);
		var queryPayload = CreatePayload("query", cycle);
		var searchHandler = new SearchHandler
		{
			Placeholder = CreatePayload("placeholder", cycle),
			AutomationId = CreatePayload("automation", cycle),
			FontSize = 14,
			SearchBoxVisibility = SearchBoxVisibility.Expanded,
			ShowsResults = false
		};

		searchView.SearchHandler = searchHandler;
		((IShellSearchView)searchView).LoadView();

		var textField = FindDescendant<EditText>(searchView)
			?? throw new InvalidOperationException("ShellSearchView did not create an EditText descendant.");

		await Task.Delay(1);

		var originalAdapter = default(IListAdapter);
		if (textField is AppCompatAutoCompleteTextView autoCompleteTextView)
			originalAdapter = NeutralizeSuggestionAdapter(autoCompleteTextView);

		await Task.Delay(1);
		originalAdapter?.Dispose();

		textField.Text = queryPayload;
		await Task.Delay(1);

		if (clearNativeTextSlots)
			ClearNativeTextSlots(textField);

		var retainedTextField = RetainedNativeTextField.Create(textField);

		((IShellSearchView)searchView).Dispose();
		shell.Handler = null;
		shellHandler.DisconnectHandler();

		RetainedNativeTextFields.Add(retainedTextField);

		tracked.Add(TrackedCycle.Create(
			retainedTextField,
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
		textField = null!;
	}

	static void EnsureCurrentApplication(AppCompatActivity activity)
	{
		var services = new ReproServiceProvider(activity);
		var mauiContext = new MauiContext(services, activity);
		var application = Microsoft.Maui.Controls.Application.Current ?? new Microsoft.Maui.Controls.Application();
		application.Handler = new FakeElementHandler(mauiContext);
		Microsoft.Maui.Controls.Application.SetCurrentApplication(application);
	}

	static void ClearNativeTextSlots(EditText textField)
	{
		textField.Text = string.Empty;
		textField.Hint = string.Empty;
		textField.ContentDescription = null;
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

	static TextSlotSnapshot CaptureTextSlots(RetainedNativeTextField textField)
	{
		var assigned = 0;
		var payload = 0;
		long bytes = 0;

		using var wrapper = textField.CreateWrapper();
		Accumulate(wrapper.Text, ref assigned, ref payload, ref bytes);
		Accumulate(wrapper.Hint, ref assigned, ref payload, ref bytes);
		Accumulate(wrapper.ContentDescription, ref assigned, ref payload, ref bytes);

		return new TextSlotSnapshot(assigned, payload, bytes);
	}

	static void Accumulate(string? text, ref int assigned, ref int payload, ref long bytes)
	{
		if (string.IsNullOrEmpty(text))
			return;

		assigned++;
		bytes += (long)text.Length * sizeof(char);

		if (text.StartsWith(PayloadPrefix, StringComparison.Ordinal) &&
			text.Length >= PayloadCharsPerSlot)
		{
			payload++;
		}
	}

	static string CreatePayload(string slot, int cycle)
	{
		var prefix = $"{PayloadPrefix}{slot}-{cycle:D4}-";
		return prefix + new string((char)('A' + (cycle % 26)), PayloadCharsPerSlot - prefix.Length);
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

		public ReproServiceProvider(AppCompatActivity activity)
		{
			_activity = activity;
		}

		public object? GetService(Type serviceType)
		{
			if (serviceType == typeof(IFontManager))
				return _fontManager;
			if (serviceType == typeof(Activity))
				return _activity;
			if (serviceType == typeof(Context))
				return _activity;
			if (serviceType == typeof(LayoutInflater))
				return LayoutInflater.From(_activity);

			return null;
		}
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

	internal sealed record TextSlotSnapshot(int AssignedSlots, int PayloadSlots, long RetainedBytes)
	{
		public static TextSlotSnapshot Empty { get; } = new(0, 0, 0);
	}

	internal sealed class RetainedNativeTextField
	{
		readonly IntPtr _globalHandle;

		RetainedNativeTextField(IntPtr globalHandle)
		{
			_globalHandle = globalHandle;
		}

		public bool IsAlive => _globalHandle != IntPtr.Zero;

		public static RetainedNativeTextField Create(EditText textField)
		{
			var globalHandle = JNIEnv.NewGlobalRef(textField.Handle);
			return new RetainedNativeTextField(globalHandle);
		}

		public EditText CreateWrapper()
		{
			if (_globalHandle == IntPtr.Zero)
				throw new ObjectDisposedException(nameof(RetainedNativeTextField));

			return Java.Lang.Object.GetObject<EditText>(_globalHandle, JniHandleOwnership.DoNotTransfer)
				?? throw new InvalidOperationException("Could not re-wrap retained native EditText peer.");
		}
	}

	internal sealed record TrackedCycle(
		RetainedNativeTextField NativeTextField,
		WeakReference<ShellSearchView> SearchView,
		WeakReference<SearchHandler> SearchHandler,
		WeakReference<Shell> Shell,
		WeakReference<FakeElementHandler> ShellHandler)
	{
		public static TrackedCycle Create(
			RetainedNativeTextField nativeTextField,
			ShellSearchView searchView,
			SearchHandler searchHandler,
			Shell shell,
			FakeElementHandler shellHandler)
		{
			return new TrackedCycle(
				nativeTextField,
				new WeakReference<ShellSearchView>(searchView),
				new WeakReference<SearchHandler>(searchHandler),
				new WeakReference<Shell>(shell),
				new WeakReference<FakeElementHandler>(shellHandler));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeTextFields,
		int AliveSearchViews,
		int AliveSearchHandlers,
		int AliveShells,
		int AliveShellHandlers,
		int AssignedNativeTextSlots,
		int PayloadNativeTextSlots,
		long RetainedNativeTextBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var searchViewRefs = new List<WeakReference<ShellSearchView>>();
			var searchHandlerRefs = new List<WeakReference<SearchHandler>>();
			var shellRefs = new List<WeakReference<Shell>>();
			var shellHandlerRefs = new List<WeakReference<FakeElementHandler>>();

			var aliveNativeTextFields = 0;
			var assignedSlots = 0;
			var payloadSlots = 0;
			long retainedBytes = 0;

			foreach (var cycle in tracked)
			{
				searchViewRefs.Add(cycle.SearchView);
				searchHandlerRefs.Add(cycle.SearchHandler);
				shellRefs.Add(cycle.Shell);
				shellHandlerRefs.Add(cycle.ShellHandler);

				if (cycle.NativeTextField.IsAlive)
				{
					aliveNativeTextFields++;
					var snapshot = CaptureTextSlots(cycle.NativeTextField);
					assignedSlots += snapshot.AssignedSlots;
					payloadSlots += snapshot.PayloadSlots;
					retainedBytes += snapshot.RetainedBytes;
				}
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeTextFields,
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

internal sealed record ReproReport(
	int Cycles,
	int PayloadCharsPerSlot,
	int PayloadBytesPerSlot,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public int ExpectedPayloadSlots => Cycles * 2;

	public bool LeakProved =>
		Control.AliveNativeTextFields == Cycles &&
		Current.AliveNativeTextFields == Cycles &&
		Control.PayloadNativeTextSlots == 0 &&
		Current.PayloadNativeTextSlots >= ExpectedPayloadSlots &&
		Current.AliveSearchViews == 0 &&
		Current.AliveSearchHandlers == 0 &&
		Current.AliveShells == 0 &&
		Current.AliveShellHandlers == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidShellSearchViewNativeTextRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload chars per native text slot: {PayloadCharsPerSlot}",
			$"Payload bytes per native text slot: {PayloadBytesPerSlot}",
			$"Expected payload slots (Text + Hint): {ExpectedPayloadSlots}",
			$"Native slots checked: Text, Hint, ContentDescription",
			$"SearchHandler payload bindables left assigned until their owning SearchHandler graph collects",
			$"Search suggestion adapter neutralized in both runs to isolate native text-slot cleanup",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained native text payload: {FormatBytes(Control.RetainedNativeTextBytes)}",
			$"Current retained native text payload: {FormatBytes(Current.RetainedNativeTextBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  alive native EditText peers: {result.AliveNativeTextFields}/{result.TrackedCycles}",
			$"  alive ShellSearchView instances: {result.AliveSearchViews}/{result.TrackedCycles}",
			$"  alive SearchHandlers: {result.AliveSearchHandlers}/{result.TrackedCycles}",
			$"  alive Shells: {result.AliveShells}/{result.TrackedCycles}",
			$"  alive fake Shell handlers: {result.AliveShellHandlers}/{result.TrackedCycles}",
			$"  assigned native text slots: {result.AssignedNativeTextSlots}",
			$"  payload-sized native text slots: {result.PayloadNativeTextSlots}",
			$"  retained native text bytes: {result.RetainedNativeTextBytes:N0}");
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
