using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Android.Content;
using AndroidX.AppCompat.Widget;
using AndroidX.DrawerLayout.Widget;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Platform.Compatibility;
using AApplication = Android.App.Application;
using AToolbar = AndroidX.AppCompat.Widget.Toolbar;

namespace AndroidShellFlyoutAdapterGroupingRetentionRepro;

public sealed class MainPage : ContentPage
{
	const int ShellCount = 48;
	const int PayloadBytes = 1024 * 1024;
	const int SurvivorTolerance = 1;
	const string LogTag = "ShellFlyoutAdapterGroupingRetention";
	const string ResultFileName = "android-shell-flyout-adapter-grouping-retention-results.txt";

	static readonly FieldInfo FlyoutGroupingsField =
		typeof(ShellFlyoutRecyclerAdapter).GetField("_flyoutGroupings", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(ShellFlyoutRecyclerAdapter).FullName, "_flyoutGroupings");

	readonly Label _status;
	bool _started;

	public MainPage()
	{
		Title = "Android Shell Flyout Adapter Grouping Retention";

		_status = new Label
		{
			Text = "Running Android Shell flyout adapter grouping retention repro...",
			HorizontalOptions = LayoutOptions.Center,
			VerticalOptions = LayoutOptions.Center
		};

		Content = new Grid
		{
			Padding = 24,
			Children = { _status }
		};

		Loaded += OnLoaded;
	}

	async void OnLoaded(object? sender, EventArgs e)
	{
		if (_started)
			return;

		_started = true;

		try
		{
			var result = await RunReproAsync();
			var report = result.ToReport();

			_status.Text = result.Proven
				? "PROVEN: disposed Shell flyout adapters retained generated groupings."
				: "NOT PROVEN: discarded Shell graphs did not remain alive.";

			WriteReport(report);

			await Task.Delay(250);
			Environment.Exit(result.Proven ? 0 : 2);
		}
		catch (Exception ex)
		{
			var report = "Android Shell flyout adapter grouping retention repro failed." + Environment.NewLine + ex;
			_status.Text = report;
			WriteReport(report);

			await Task.Delay(250);
			Environment.Exit(3);
		}
	}

	static async Task<ReproResult> RunReproAsync()
	{
		var baseline = await RunScenarioAsync("Baseline: no retained adapter", retainDisposedAdapters: false, clearFlyoutGroupings: false);
		var control = await RunScenarioAsync("Control: retained disposed adapters with grouping field cleared", retainDisposedAdapters: true, clearFlyoutGroupings: true);
		var current = await RunScenarioAsync("Current MAUI behavior", retainDisposedAdapters: true, clearFlyoutGroupings: false);

		var baselineCollected = baseline.ShellSurvivors <= SurvivorTolerance
			&& baseline.ShellContentSurvivors <= SurvivorTolerance
			&& baseline.PayloadSurvivors <= SurvivorTolerance
			&& baseline.PayloadBufferSurvivors <= SurvivorTolerance;

		var controlCollected = control.ShellSurvivors <= SurvivorTolerance
			&& control.ShellContentSurvivors <= SurvivorTolerance
			&& control.PayloadSurvivors <= SurvivorTolerance
			&& control.PayloadBufferSurvivors <= SurvivorTolerance
			&& control.AdapterGroupingReferences == 0;

		var currentRetained = current.ShellContentSurvivors >= ShellCount - SurvivorTolerance
			&& current.PayloadSurvivors >= ShellCount - SurvivorTolerance
			&& current.PayloadBufferSurvivors >= ShellCount - SurvivorTolerance
			&& current.AdapterGroupingReferences >= ShellCount - SurvivorTolerance;

		return new ReproResult(baseline, control, current, baselineCollected && controlCollected && currentRetained);
	}

	static async Task<ScenarioResult> RunScenarioAsync(string name, bool retainDisposedAdapters, bool clearFlyoutGroupings)
	{
		var retainedAdapters = new List<ShellFlyoutRecyclerAdapter>(retainDisposedAdapters ? ShellCount : 0);
		var shellRefs = new List<WeakReference<Shell>>(ShellCount);
		var shellContentRefs = new List<WeakReference<ShellContent>>(ShellCount);
		var payloadRefs = new List<WeakReference<Payload>>(ShellCount);
		var payloadBufferRefs = new List<WeakReference<byte[]>>(ShellCount);

		ForceFullGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		for (var i = 0; i < ShellCount; i++)
		{
			CreateDisposedAdapterScenario(
				i,
				retainDisposedAdapters,
				clearFlyoutGroupings,
				retainedAdapters,
				shellRefs,
				shellContentRefs,
				payloadRefs,
				payloadBufferRefs);

			if (i % 8 == 0)
				await Task.Yield();
		}

		await WaitForCollectionAsync();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			name,
			retainDisposedAdapters,
			clearFlyoutGroupings,
			retainedAdapters.Count,
			CountAdapterGroupingReferences(retainedAdapters),
			CountAlive(shellRefs),
			CountAlive(shellContentRefs),
			CountAlive(payloadRefs),
			CountAlive(payloadBufferRefs),
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedAdapters);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateDisposedAdapterScenario(
		int index,
		bool retainDisposedAdapter,
		bool clearFlyoutGroupings,
		List<ShellFlyoutRecyclerAdapter> retainedAdapters,
		List<WeakReference<Shell>> shellRefs,
		List<WeakReference<ShellContent>> shellContentRefs,
		List<WeakReference<Payload>> payloadRefs,
		List<WeakReference<byte[]>> payloadBufferRefs)
	{
		var payload = new Payload(index, PayloadBytes);
		var shell = new Shell
		{
			Title = $"Tenant shell {index}",
			FlyoutBehavior = FlyoutBehavior.Flyout
		};

		var shellContent = new ShellContent
		{
			Title = $"Orders {index}",
			BindingContext = payload,
			Content = new ContentPage
			{
				Title = $"Orders {index}",
				Content = new Label { Text = $"Orders shell {index}" }
			}
		};

		var shellSection = new ShellSection
		{
			Title = $"Orders section {index}",
			FlyoutDisplayOptions = FlyoutDisplayOptions.AsSingleItem
		};
		shellSection.Items.Add(shellContent);
		shellSection.CurrentItem = shellContent;

		var shellItem = new ShellItem
		{
			Title = $"Tenant {index}",
			FlyoutDisplayOptions = FlyoutDisplayOptions.AsMultipleItems
		};
		shellItem.Items.Add(shellSection);
		shellItem.CurrentItem = shellSection;

		shell.Items.Add(shellItem);
		shell.CurrentItem = shellItem;

		var adapter = new ShellFlyoutRecyclerAdapter(new StubShellContext(shell), _ => { });
		var generatedReferences = CountAdapterGroupingReferences(adapter);
		if (generatedReferences == 0)
			throw new InvalidOperationException("Expected ShellFlyoutRecyclerAdapter to generate flyout grouping references.");

		adapter.Dispose();

		if (clearFlyoutGroupings)
			FlyoutGroupingsField.SetValue(adapter, null);

		if (retainDisposedAdapter)
			retainedAdapters.Add(adapter);

		shellRefs.Add(new WeakReference<Shell>(shell));
		shellContentRefs.Add(new WeakReference<ShellContent>(shellContent));
		payloadRefs.Add(new WeakReference<Payload>(payload));
		payloadBufferRefs.Add(new WeakReference<byte[]>(payload.Buffer));
	}

	static int CountAdapterGroupingReferences(IEnumerable<ShellFlyoutRecyclerAdapter> adapters)
	{
		var count = 0;
		foreach (var adapter in adapters)
			count += CountAdapterGroupingReferences(adapter);

		return count;
	}

	static int CountAdapterGroupingReferences(ShellFlyoutRecyclerAdapter adapter)
	{
		if (FlyoutGroupingsField.GetValue(adapter) is not List<List<Element>> groups)
			return 0;

		var count = 0;
		foreach (var group in groups)
			count += group.Count;

		return count;
	}

	static async Task WaitForCollectionAsync()
	{
		for (var i = 0; i < 6; i++)
		{
			ForceFullGc();
			await Task.Delay(50);
		}
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
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

	static void WriteReport(string report)
	{
		var path = Path.Combine(FileSystem.Current.AppDataDirectory, ResultFileName);
		File.WriteAllText(path, report);
		Android.Util.Log.Info(LogTag, report.Replace(Environment.NewLine, " | ", StringComparison.Ordinal));
	}

	sealed class StubShellContext : IShellContext
	{
		public StubShellContext(Shell shell)
		{
			Shell = shell;
		}

		public Context AndroidContext => AApplication.Context;
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

	readonly record struct ScenarioResult(
		string Name,
		bool RetainDisposedAdapters,
		bool ClearFlyoutGroupings,
		int RetainedAdapters,
		int AdapterGroupingReferences,
		int ShellSurvivors,
		int ShellContentSurvivors,
		int PayloadSurvivors,
		int PayloadBufferSurvivors,
		long HeapBeforeBytes,
		long HeapAfterBytes)
	{
		public long HeapDeltaBytes => HeapAfterBytes - HeapBeforeBytes;
		public double RetainedPayloadMiB => PayloadBufferSurvivors * PayloadBytes / 1024d / 1024d;

		public void AppendTo(StringBuilder builder)
		{
			builder.AppendLine(Name);
			builder.AppendLine($"  Retain disposed adapters: {RetainDisposedAdapters}");
			builder.AppendLine($"  Clear _flyoutGroupings: {ClearFlyoutGroupings}");
			builder.AppendLine($"  Retained disposed adapters: {RetainedAdapters}");
			builder.AppendLine($"  Adapter grouping references: {AdapterGroupingReferences}");
			builder.AppendLine($"  Shell survivors: {ShellSurvivors}/{ShellCount}");
			builder.AppendLine($"  ShellContent survivors: {ShellContentSurvivors}/{ShellCount}");
			builder.AppendLine($"  Payload survivors: {PayloadSurvivors}/{ShellCount}");
			builder.AppendLine($"  Payload buffer survivors: {PayloadBufferSurvivors}/{ShellCount}");
			builder.AppendLine($"  Retained payload estimate: {RetainedPayloadMiB:F1} MiB");
			builder.AppendLine($"  Managed heap before: {HeapBeforeBytes:N0} bytes");
			builder.AppendLine($"  Managed heap after: {HeapAfterBytes:N0} bytes");
			builder.AppendLine($"  Managed heap delta: {HeapDeltaBytes:N0} bytes");
		}
	}

	readonly record struct ReproResult(ScenarioResult Baseline, ScenarioResult Control, ScenarioResult Current, bool Proven)
	{
		public string ToReport()
		{
			var builder = new StringBuilder();
			builder.AppendLine("Android Shell flyout adapter grouping retention repro");
			builder.AppendLine($"Result: {(Proven ? "PROVEN" : "NOT PROVEN")}");
			builder.AppendLine();
			Baseline.AppendTo(builder);
			builder.AppendLine();
			Control.AppendTo(builder);
			builder.AppendLine();
			Current.AppendTo(builder);
			builder.AppendLine();
			builder.AppendLine("Expected proof signal:");
			builder.AppendLine($"- Baseline and control survivors <= {SurvivorTolerance} after forced GC.");
			builder.AppendLine($"- Current behavior survivors >= {ShellCount - SurvivorTolerance} while only disposed adapters remain intentionally rooted.");
			builder.AppendLine();
			builder.AppendLine("Retained graph under current behavior:");
			builder.AppendLine("Retained disposed ShellFlyoutRecyclerAdapter -> _flyoutGroupings -> ShellSection/ShellContent -> BindingContext payload");
			builder.AppendLine();
			builder.AppendLine("Why this is distinct from nearby tracked leaks:");
			builder.AppendLine("- C088 covers active ElementViewHolder logical-child and PropertyChanged retention.");
			builder.AppendLine("- C467 covers live Shell generated flyout projection after ShellContent.MenuItems removal.");
			builder.AppendLine("- This repro creates no view holders and releases the Shell; only the disposed adapter is intentionally retained.");
			return builder.ToString();
		}
	}
}
