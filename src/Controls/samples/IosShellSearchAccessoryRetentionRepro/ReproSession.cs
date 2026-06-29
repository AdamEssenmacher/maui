#nullable enable

using System.Reflection;
using System.Runtime.InteropServices;
using Foundation;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Platform.Compatibility;
using Microsoft.Maui.Devices;
using ObjCRuntime;
using UIKit;

namespace IosShellSearchAccessoryRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 96;
	internal const int PayloadKiBPerCycle = 512;

	const long PayloadBytesPerCycle = PayloadKiBPerCycle * 1024L;

	static readonly FieldInfo FontManagerField =
		typeof(FontManager).GetField("_serviceProvider", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(FontManager).FullName, "_serviceProvider");

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");

	static readonly List<IReadOnlyList<RetainedNativeSearchBar>> RetainedNativeSearchBars = new();

	public static readonly string ResultsPath =
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ios-shell-search-accessory-retention-results.txt");

	public static async Task<ReproReport> RunAsync()
	{
		WriteProgress("Starting iOS Shell SearchHandler accessory retention repro.");

		if (DeviceInfo.Idiom != DeviceIdiom.Phone)
			throw new InvalidOperationException($"This repro must run on an iPhone idiom. Current idiom: {DeviceInfo.Idiom}.");

		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear UISearchBar.InputAccessoryView and toolbar items before tracker disposal",
			clearNativeAccessory: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: SearchHandlerAppearanceTracker.Dispose() leaves numeric InputAccessoryView assigned",
			clearNativeAccessory: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeSearchBars);

		return new ReproReport(
			Cycles,
			PayloadKiBPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(string name, bool clearNativeAccessory)
	{
		var retainedSearchBars = new List<RetainedNativeSearchBar>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 16 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var cycleResult = await RunCycleAsync(i, clearNativeAccessory);
			retainedSearchBars.Add(cycleResult.RetainedSearchBar);
			tracked.Add(cycleResult.Tracked);
		}

		RetainedNativeSearchBars.Add(retainedSearchBars);
		ForceFullGc();

		return ScenarioResult.From(name, retainedSearchBars, tracked);
	}

	static async Task<CycleResult> RunCycleAsync(int cycle, bool clearNativeAccessory)
	{
		var payload = new byte[(int)PayloadBytesPerCycle];
		payload[0] = (byte)(cycle % 251);
		payload[^1] = (byte)((cycle + 37) % 251);

		var serviceProvider = new PayloadServiceProvider(payload);
		var fontManager = new FontManager(new EmptyFontRegistrar(), serviceProvider);
		var searchHandler = new SearchHandler
		{
			Keyboard = Keyboard.Numeric,
			Placeholder = $"Search order {cycle:0000}",
			Query = $"SO-{cycle:0000}"
		};
		var searchBar = new UISearchBar();
		var tracker = new SearchHandlerAppearanceTracker(searchBar, searchHandler, fontManager);

		await DrainMainQueueAsync();

		if (searchBar.InputAccessoryView is not UIToolbar)
			throw new InvalidOperationException("SearchHandlerAppearanceTracker did not assign a numeric keyboard UIToolbar accessory.");

		if (clearNativeAccessory)
			ClearNativeAccessory(searchBar);

		var retainedSearchBar = RetainNativeSearchBar(searchBar);

		tracker.Dispose();
		searchBar.Dispose();
		searchHandler.ClearValue(SearchHandler.KeyboardProperty);

		await DrainMainQueueAsync();

		return new CycleResult(
			retainedSearchBar,
			TrackedCycle.Create(cycle, tracker, fontManager, serviceProvider, payload, searchHandler));
	}

	static void ClearNativeAccessory(UISearchBar searchBar)
	{
		if (searchBar.InputAccessoryView is UIToolbar toolbar)
			toolbar.SetItems(Array.Empty<UIBarButtonItem>(), false);

		searchBar.InputAccessoryView = null;
	}

	static int CountAssignedAccessorySlots(UISearchBar searchBar)
	{
		return searchBar.InputAccessoryView is UIToolbar toolbar && (toolbar.Items?.Length ?? 0) > 0 ? 1 : 0;
	}

	static RetainedNativeSearchBar RetainNativeSearchBar(UISearchBar searchBar)
	{
		var handle = searchBar.Handle;
		if (handle == IntPtr.Zero)
			throw new InvalidOperationException("Cannot retain a native UISearchBar with a zero handle.");

		var retained = IntPtr_objc_msgSend(handle, RetainSelector);
		if (retained == IntPtr.Zero)
			throw new InvalidOperationException("Objective-C retain returned a zero handle.");

		return new RetainedNativeSearchBar(retained);
	}

	static async Task DrainMainQueueAsync()
	{
		await Task.Delay(50);
		NSRunLoop.Main.RunUntil(NSDate.FromTimeIntervalSinceNow(0.02));
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			using var pool = new NSAutoreleasePool();
			NSRunLoop.Current.RunUntil(NSDate.FromTimeIntervalSinceNow(0.01));
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(75);
		}
	}

	static void WriteProgress(string message)
	{
		try
		{
			File.WriteAllText(ResultsPath, message + Environment.NewLine);
		}
		catch
		{
			// Progress output is diagnostic only; the final report write remains authoritative.
		}
	}

	[DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
	static extern IntPtr IntPtr_objc_msgSend(IntPtr receiver, IntPtr selector);

	internal sealed record CycleResult(RetainedNativeSearchBar RetainedSearchBar, TrackedCycle Tracked);

	internal sealed class RetainedNativeSearchBar
	{
		public RetainedNativeSearchBar(IntPtr handle)
		{
			Handle = handle;
		}

		public IntPtr Handle { get; }

		public UISearchBar? TryGetSearchBar()
		{
			if (Handle == IntPtr.Zero)
				return null;

			try
			{
				return Runtime.GetNSObject<UISearchBar>(Handle, false);
			}
			catch
			{
				return null;
			}
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<SearchHandlerAppearanceTracker> Tracker,
		WeakReference<FontManager> FontManager,
		WeakReference<PayloadServiceProvider> ServiceProvider,
		WeakReference<byte[]> Payload,
		WeakReference<SearchHandler> SearchHandler)
	{
		public static TrackedCycle Create(
			int cycle,
			SearchHandlerAppearanceTracker tracker,
			FontManager fontManager,
			PayloadServiceProvider serviceProvider,
			byte[] payload,
			SearchHandler searchHandler)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<SearchHandlerAppearanceTracker>(tracker),
				new WeakReference<FontManager>(fontManager),
				new WeakReference<PayloadServiceProvider>(serviceProvider),
				new WeakReference<byte[]>(payload),
				new WeakReference<SearchHandler>(searchHandler));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativeSearchBars,
		int AssignedNativeAccessorySlots,
		int AliveTrackers,
		int AliveFontManagers,
		int AliveServiceProviders,
		int AlivePayloads,
		int AliveSearchHandlers,
		long RetainedPayloadBytes)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedNativeSearchBar> retainedSearchBars,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var retainedNativeSearchBars = 0;
			var assignedNativeAccessorySlots = 0;

			foreach (var retainedSearchBar in retainedSearchBars)
			{
				var searchBar = retainedSearchBar.TryGetSearchBar();
				if (searchBar is null)
					continue;

				retainedNativeSearchBars++;
				assignedNativeAccessorySlots += CountAssignedAccessorySlots(searchBar);
			}

			var aliveTrackers = 0;
			var aliveFontManagers = 0;
			var aliveServiceProviders = 0;
			var alivePayloads = 0;
			var aliveSearchHandlers = 0;
			long retainedPayloadBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.Tracker.TryGetTarget(out _))
					aliveTrackers++;

				if (cycle.FontManager.TryGetTarget(out var fontManager))
				{
					aliveFontManagers++;
					if (FontManagerField.GetValue(fontManager) is PayloadServiceProvider provider)
						retainedPayloadBytes += provider.Payload.LongLength;
				}

				if (cycle.ServiceProvider.TryGetTarget(out _))
					aliveServiceProviders++;

				if (cycle.Payload.TryGetTarget(out _))
					alivePayloads++;

				if (cycle.SearchHandler.TryGetTarget(out _))
					aliveSearchHandlers++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedNativeSearchBars,
				assignedNativeAccessorySlots,
				aliveTrackers,
				aliveFontManagers,
				aliveServiceProviders,
				alivePayloads,
				aliveSearchHandlers,
				retainedPayloadBytes);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadKiBPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedNativeSearchBars == Cycles &&
		Control.AssignedNativeAccessorySlots == 0 &&
		Control.AliveTrackers <= 1 &&
		Control.AliveFontManagers <= 1 &&
		Control.AliveServiceProviders <= 1 &&
		Control.AlivePayloads <= 1 &&
		Current.RetainedNativeSearchBars == Cycles &&
		Current.AssignedNativeAccessorySlots == Cycles &&
		Current.AliveTrackers == Cycles &&
		Current.AliveFontManagers == Cycles &&
		Current.AliveServiceProviders == Cycles &&
		Current.AlivePayloads == Cycles &&
		Current.AliveSearchHandlers <= 1 &&
		Current.RetainedPayloadBytes >= Cycles * PayloadKiBPerCycle * 1024L;

	public string ToText()
	{
		var controlMiB = Control.RetainedPayloadBytes / 1024d / 1024d;
		var currentMiB = Current.RetainedPayloadBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosShellSearchAccessoryRetentionRepro",
			$"Device idiom: {DeviceInfo.Idiom}",
			$"Cycles: {Cycles}",
			$"Payload per FontManager service provider: {PayloadKiBPerCycle} KiB",
			"Source path mirrored: SearchHandlerAppearanceTracker.UpdateKeyboard -> CreateNumericKeyboardAccessoryView -> UISearchBar.InputAccessoryView",
			"Control isolation: the control clears UISearchBar.InputAccessoryView and toolbar items before tracker disposal",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained service-provider payload: {controlMiB:N1} MiB",
			$"Current retained service-provider payload: {currentMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var payloadMiB = result.RetainedPayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native search bars: {result.RetainedNativeSearchBars}/{result.TrackedCycles}",
			$"  assigned native accessory toolbar slots: {result.AssignedNativeAccessorySlots}/{result.TrackedCycles}",
			$"  alive SearchHandlerAppearanceTrackers: {result.AliveTrackers}/{result.TrackedCycles}",
			$"  alive FontManagers: {result.AliveFontManagers}/{result.TrackedCycles}",
			$"  alive service providers: {result.AliveServiceProviders}/{result.TrackedCycles}",
			$"  alive payload arrays: {result.AlivePayloads}/{result.TrackedCycles}",
			$"  alive SearchHandlers: {result.AliveSearchHandlers}/{result.TrackedCycles}",
			$"  retained service-provider payload bytes: {result.RetainedPayloadBytes:N0}",
			$"  retained service-provider payload MiB: {payloadMiB:N1}");
	}
}

internal sealed class PayloadServiceProvider : IServiceProvider
{
	public PayloadServiceProvider(byte[] payload)
	{
		Payload = payload;
	}

	public byte[] Payload { get; }

	public object? GetService(Type serviceType) => null;
}

internal sealed class EmptyFontRegistrar : IFontRegistrar
{
	public void Register(string filename, string? alias, Assembly assembly)
	{
	}

	public void Register(string filename, string? alias)
	{
	}

	public string? GetFont(string font) => null;
}
