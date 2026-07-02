using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Internals;
using Microsoft.Maui.Controls.Xaml;

namespace XamlNameScopeRemovedChildRetentionRepro;

public sealed class MainPage : ContentPage
{
	const int PageCount = 120;
	const int PayloadBytes = 1024 * 1024;
	const int SurvivorTolerance = 2;
	const string RemovedChildName = "RemovedChild";

	const string RuntimePageXaml =
		"""
		<ContentPage
		    x:Name="RuntimeRoot"
		    xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
		    xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml">
		    <Grid x:Name="RootGrid" Padding="4">
		        <Label x:Name="RemovedChild" Text="Removed named child" />
		    </Grid>
		</ContentPage>
		""";

	readonly string? _resultsPath;
	readonly Label _status;
	bool _started;

	public MainPage(string? resultsPath)
	{
		_resultsPath = resultsPath;
		Title = "XAML NameScope Removed Child Retention";

		_status = new Label
		{
			Text = "Running XAML NameScope removed-child retention repro...",
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
				? "PROVEN: live XAML roots retained removed named children through NameScope."
				: "NOT PROVEN: removed named children did not remain alive.";

			if (!string.IsNullOrWhiteSpace(_resultsPath))
				File.WriteAllText(_resultsPath, report);

			await Task.Delay(250);
			Environment.Exit(result.Proven ? 0 : 2);
		}
		catch (Exception ex)
		{
			var report = "XAML NameScope removed-child retention repro failed." + Environment.NewLine + ex;
			_status.Text = report;

			if (!string.IsNullOrWhiteSpace(_resultsPath))
				File.WriteAllText(_resultsPath, report);

			await Task.Delay(250);
			Environment.Exit(3);
		}
	}

	static async Task<ReproResult> RunReproAsync()
	{
		var control = await RunScenarioAsync(unregisterRemovedName: true);
		var current = await RunScenarioAsync(unregisterRemovedName: false);

		var controlCollected = control.RemovedChildSurvivors <= SurvivorTolerance
			&& control.PayloadSurvivors <= SurvivorTolerance
			&& control.PayloadBufferSurvivors <= SurvivorTolerance;

		var currentRetained = current.RemovedChildSurvivors >= PageCount - SurvivorTolerance
			&& current.PayloadSurvivors >= PageCount - SurvivorTolerance
			&& current.PayloadBufferSurvivors >= PageCount - SurvivorTolerance;

		return new ReproResult(control, current, controlCollected && currentRetained);
	}

	static async Task<ScenarioResult> RunScenarioAsync(bool unregisterRemovedName)
	{
		var retainedPages = new List<ContentPage>(PageCount);
		var removedChildRefs = new List<WeakReference<Label>>(PageCount);
		var payloadRefs = new List<WeakReference<Payload>>(PageCount);
		var payloadBufferRefs = new List<WeakReference<byte[]>>(PageCount);

		ForceFullGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		for (var i = 0; i < PageCount; i++)
		{
			CreateLivePageAndRemoveNamedChild(
				i,
				unregisterRemovedName,
				retainedPages,
				removedChildRefs,
				payloadRefs,
				payloadBufferRefs);

			if (i % 12 == 0)
				await Task.Yield();
		}

		await WaitForCollectionAsync();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			unregisterRemovedName ? "Control: unregister removed child name" : "Current MAUI behavior",
			unregisterRemovedName,
			retainedPages.Count,
			CountAlive(removedChildRefs),
			CountAlive(payloadRefs),
			CountAlive(payloadBufferRefs),
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedPages);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateLivePageAndRemoveNamedChild(
		int index,
		bool unregisterRemovedName,
		List<ContentPage> retainedPages,
		List<WeakReference<Label>> removedChildRefs,
		List<WeakReference<Payload>> payloadRefs,
		List<WeakReference<byte[]>> payloadBufferRefs)
	{
		var payload = new Payload(index, PayloadBytes);
		var page = new ContentPage
		{
			Title = $"Runtime XAML page {index}"
		}.LoadFromXaml(RuntimePageXaml);

		var rootGrid = page.FindByName<Grid>("RootGrid")
			?? throw new InvalidOperationException("RootGrid was not registered in the runtime XAML NameScope.");
		var removedChild = page.FindByName<Label>(RemovedChildName)
			?? throw new InvalidOperationException("RemovedChild was not registered in the runtime XAML NameScope.");

		removedChild.BindingContext = payload;
		rootGrid.Remove(removedChild);

		if (removedChild.Parent is not null || removedChild.RealParent is not null)
			throw new InvalidOperationException("The named child was not detached from its XAML parent.");

		if (unregisterRemovedName)
			((INameScope)page).UnregisterName(RemovedChildName);

		retainedPages.Add(page);
		removedChildRefs.Add(new WeakReference<Label>(removedChild));
		payloadRefs.Add(new WeakReference<Payload>(payload));
		payloadBufferRefs.Add(new WeakReference<byte[]>(payload.Buffer));
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

	readonly record struct ScenarioResult(
		string Name,
		bool UnregisterRemovedName,
		int RetainedRootPages,
		int RemovedChildSurvivors,
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
			builder.AppendLine($"  Retained root pages: {RetainedRootPages}");
			builder.AppendLine($"  Unregister removed child name: {UnregisterRemovedName}");
			builder.AppendLine($"  Removed child survivors: {RemovedChildSurvivors}/{PageCount}");
			builder.AppendLine($"  Payload survivors: {PayloadSurvivors}/{PageCount}");
			builder.AppendLine($"  Payload buffer survivors: {PayloadBufferSurvivors}/{PageCount}");
			builder.AppendLine($"  Retained payload estimate: {RetainedPayloadMiB:F1} MiB");
			builder.AppendLine($"  Managed heap before: {HeapBeforeBytes:N0} bytes");
			builder.AppendLine($"  Managed heap after: {HeapAfterBytes:N0} bytes");
			builder.AppendLine($"  Managed heap delta: {HeapDeltaBytes:N0} bytes");
		}
	}

	readonly record struct ReproResult(ScenarioResult Control, ScenarioResult Current, bool Proven)
	{
		public string ToReport()
		{
			var builder = new StringBuilder();
			builder.AppendLine("XAML NameScope removed-child retention repro");
			builder.AppendLine($"Result: {(Proven ? "PROVEN" : "NOT PROVEN")}");
			builder.AppendLine();
			Control.AppendTo(builder);
			builder.AppendLine();
			Current.AppendTo(builder);
			builder.AppendLine();
			builder.AppendLine("Expected proof signal:");
			builder.AppendLine($"- Control removed-child survivors <= {SurvivorTolerance} after unregistering `{RemovedChildName}` from the root NameScope.");
			builder.AppendLine($"- Current behavior removed-child survivors >= {PageCount - SurvivorTolerance} while only root pages remain intentionally rooted.");
			builder.AppendLine();
			builder.AppendLine("Retained graph under current behavior:");
			builder.AppendLine("Retained runtime XAML root page -> NameScope -> x:Name removed child -> BindingContext payload");
			return builder.ToString();
		}
	}
}
