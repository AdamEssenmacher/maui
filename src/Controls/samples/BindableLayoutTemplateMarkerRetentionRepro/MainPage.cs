using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Xaml;

namespace BindableLayoutTemplateMarkerRetentionRepro;

public sealed class MainPage : ContentPage
{
	const int PageCount = 80;
	const int ItemsPerPage = 3;
	const int PayloadBytes = 1024 * 1024;
	const int SurvivorTolerance = 2;
	const int ItemCount = PageCount * ItemsPerPage;

	const string RuntimeXaml = """
		<ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
		             xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml">
		  <ContentPage.Resources>
		    <ResourceDictionary>
		      <DataTemplate x:Key="ItemTemplate">
		        <Label Text="{Binding Title}" />
		      </DataTemplate>
		    </ResourceDictionary>
		  </ContentPage.Resources>
		  <VerticalStackLayout BindableLayout.ItemTemplate="{StaticResource ItemTemplate}" />
		</ContentPage>
		""";

	static readonly BindableProperty BindableLayoutTemplateProperty = GetBindableLayoutTemplateProperty();

	readonly string? _resultsPath;
	readonly Label _status;
	bool _started;

	public MainPage(string? resultsPath)
	{
		_resultsPath = resultsPath;
		Title = "BindableLayout Template Marker Retention";

		_status = new Label
		{
			Text = "Running BindableLayout template marker retention repro...",
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
				? "PROVEN: retained removed BindableLayout children retained page-local templates."
				: "NOT PROVEN: removed BindableLayout children did not retain pages.";

			if (!string.IsNullOrWhiteSpace(_resultsPath))
				File.WriteAllText(_resultsPath, report);

			await Task.Delay(250);
			Environment.Exit(result.Proven ? 0 : 2);
		}
		catch (Exception ex)
		{
			var report = "BindableLayout template marker retention repro failed." + Environment.NewLine + ex;
			_status.Text = report;

			if (!string.IsNullOrWhiteSpace(_resultsPath))
				File.WriteAllText(_resultsPath, report);

			await Task.Delay(250);
			Environment.Exit(3);
		}
	}

	static async Task<ReproResult> RunReproAsync()
	{
		var baseline = await RunScenarioAsync("Baseline: no retained generated child", retainRemovedChild: false, clearTemplateMarker: false);
		var control = await RunScenarioAsync("Control: retained generated child with template marker cleared", retainRemovedChild: true, clearTemplateMarker: true);
		var current = await RunScenarioAsync("Current MAUI behavior: retained generated child", retainRemovedChild: true, clearTemplateMarker: false);

		var baselineCollected = baseline.PageSurvivors <= SurvivorTolerance
			&& baseline.PagePayloadSurvivors <= SurvivorTolerance
			&& baseline.PagePayloadBufferSurvivors <= SurvivorTolerance
			&& baseline.ItemModelSurvivors <= SurvivorTolerance;

		var controlCollected = control.RetainedChildren == PageCount
			&& control.GeneratedChildSurvivors >= PageCount - SurvivorTolerance
			&& control.StaleTemplateMarkers <= SurvivorTolerance
			&& control.PageSurvivors <= SurvivorTolerance
			&& control.PagePayloadSurvivors <= SurvivorTolerance
			&& control.PagePayloadBufferSurvivors <= SurvivorTolerance
			&& control.ItemModelSurvivors <= SurvivorTolerance;

		var currentRetained = current.RetainedChildren == PageCount
			&& current.GeneratedChildSurvivors >= PageCount - SurvivorTolerance
			&& current.StaleTemplateMarkers >= PageCount - SurvivorTolerance
			&& current.PageSurvivors >= PageCount - SurvivorTolerance
			&& current.PagePayloadSurvivors >= PageCount - SurvivorTolerance
			&& current.PagePayloadBufferSurvivors >= PageCount - SurvivorTolerance
			&& current.ItemModelSurvivors <= SurvivorTolerance;

		return new ReproResult(baseline, control, current, baselineCollected && controlCollected && currentRetained);
	}

	static async Task<ScenarioResult> RunScenarioAsync(string name, bool retainRemovedChild, bool clearTemplateMarker)
	{
		var retainedChildren = new List<View>(retainRemovedChild ? PageCount : 0);
		var pageRefs = new List<WeakReference<ContentPage>>(PageCount);
		var pagePayloadRefs = new List<WeakReference<Payload>>(PageCount);
		var pagePayloadBufferRefs = new List<WeakReference<byte[]>>(PageCount);
		var generatedChildRefs = new List<WeakReference<View>>(PageCount);
		var itemModelRefs = new List<WeakReference<ItemModel>>(ItemCount);

		ForceFullGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		for (var pageIndex = 0; pageIndex < PageCount; pageIndex++)
		{
			CreateBindableLayoutScenario(
				pageIndex,
				retainRemovedChild,
				clearTemplateMarker,
				retainedChildren,
				pageRefs,
				pagePayloadRefs,
				pagePayloadBufferRefs,
				generatedChildRefs,
				itemModelRefs);

			if (pageIndex % 8 == 0)
				await Task.Yield();
		}

		await WaitForCollectionAsync();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			name,
			retainRemovedChild,
			clearTemplateMarker,
			retainedChildren.Count,
			CountStaleTemplateMarkers(retainedChildren),
			CountAlive(pageRefs),
			CountAlive(pagePayloadRefs),
			CountAlive(pagePayloadBufferRefs),
			CountAlive(generatedChildRefs),
			CountAlive(itemModelRefs),
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedChildren);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateBindableLayoutScenario(
		int pageIndex,
		bool retainRemovedChild,
		bool clearTemplateMarker,
		List<View> retainedChildren,
		List<WeakReference<ContentPage>> pageRefs,
		List<WeakReference<Payload>> pagePayloadRefs,
		List<WeakReference<byte[]>> pagePayloadBufferRefs,
		List<WeakReference<View>> generatedChildRefs,
		List<WeakReference<ItemModel>> itemModelRefs)
	{
		var pagePayload = new Payload(pageIndex, PayloadBytes);
		var page = new ContentPage();
		page.LoadFromXaml(RuntimeXaml);
		page.BindingContext = pagePayload;

		var host = (VerticalStackLayout)page.Content;
		var items = Enumerable.Range(0, ItemsPerPage)
			.Select(itemIndex => new ItemModel($"Account {pageIndex} item {itemIndex}"))
			.ToArray();

		foreach (var item in items)
			itemModelRefs.Add(new WeakReference<ItemModel>(item));

		BindableLayout.SetItemsSource(host, items);

		if (host.Children.Count != ItemsPerPage)
			throw new InvalidOperationException($"Expected {ItemsPerPage} generated children, got {host.Children.Count}.");

		var retainedChild = (View)host.Children[0];
		generatedChildRefs.Add(new WeakReference<View>(retainedChild));

		BindableLayout.SetItemsSource(host, Array.Empty<ItemModel>());

		if (host.Children.Count != 0)
			throw new InvalidOperationException("Expected BindableLayout to remove generated children after empty ItemsSource.");

		if (retainedChild.Parent is not null)
			throw new InvalidOperationException("Removed generated child still had a logical parent.");

		if (retainedChild.BindingContext is not null)
			throw new InvalidOperationException("Removed generated child still had an item BindingContext.");

		if (clearTemplateMarker)
			retainedChild.ClearValue(BindableLayoutTemplateProperty);

		if (retainRemovedChild)
			retainedChildren.Add(retainedChild);

		pageRefs.Add(new WeakReference<ContentPage>(page));
		pagePayloadRefs.Add(new WeakReference<Payload>(pagePayload));
		pagePayloadBufferRefs.Add(new WeakReference<byte[]>(pagePayload.Buffer));
	}

	static BindableProperty GetBindableLayoutTemplateProperty()
	{
		var controllerType = typeof(BindableLayout).Assembly.GetType("Microsoft.Maui.Controls.BindableLayoutController")
			?? throw new TypeLoadException("Microsoft.Maui.Controls.BindableLayoutController");

		return (BindableProperty)(controllerType.GetField("BindableLayoutTemplateProperty", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
			?? throw new MissingFieldException(controllerType.FullName, "BindableLayoutTemplateProperty"));
	}

	static int CountStaleTemplateMarkers(IEnumerable<View> generatedChildren)
	{
		var count = 0;
		foreach (var child in generatedChildren)
		{
			if (child.GetValue(BindableLayoutTemplateProperty) is not null)
				count++;
		}

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

	sealed class ItemModel
	{
		public ItemModel(string title)
		{
			Title = title;
		}

		public string Title { get; }
	}

	readonly record struct ScenarioResult(
		string Name,
		bool RetainRemovedChild,
		bool ClearTemplateMarker,
		int RetainedChildren,
		int StaleTemplateMarkers,
		int PageSurvivors,
		int PagePayloadSurvivors,
		int PagePayloadBufferSurvivors,
		int GeneratedChildSurvivors,
		int ItemModelSurvivors,
		long HeapBeforeBytes,
		long HeapAfterBytes)
	{
		public long HeapDeltaBytes => HeapAfterBytes - HeapBeforeBytes;
		public double RetainedPagePayloadMiB => PagePayloadBufferSurvivors * PayloadBytes / 1024d / 1024d;

		public void AppendTo(StringBuilder builder)
		{
			builder.AppendLine(Name);
			builder.AppendLine($"  Retain one removed generated child per page: {RetainRemovedChild}");
			builder.AppendLine($"  Clear hidden BindableLayoutTemplate marker: {ClearTemplateMarker}");
			builder.AppendLine($"  Retained generated children: {RetainedChildren}/{PageCount}");
			builder.AppendLine($"  Stale template markers on retained children: {StaleTemplateMarkers}/{PageCount}");
			builder.AppendLine($"  Page survivors: {PageSurvivors}/{PageCount}");
			builder.AppendLine($"  Page payload survivors: {PagePayloadSurvivors}/{PageCount}");
			builder.AppendLine($"  Page payload buffer survivors: {PagePayloadBufferSurvivors}/{PageCount}");
			builder.AppendLine($"  Generated child survivors: {GeneratedChildSurvivors}/{PageCount}");
			builder.AppendLine($"  Item model survivors after BindingContext cleanup: {ItemModelSurvivors}/{ItemCount}");
			builder.AppendLine($"  Retained page payload estimate: {RetainedPagePayloadMiB:F1} MiB");
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
			builder.AppendLine("BindableLayout template marker retention repro");
			builder.AppendLine($"Result: {(Proven ? "PROVEN" : "NOT PROVEN")}");
			builder.AppendLine();
			builder.AppendLine("Trigger:");
			builder.AppendLine("  BindableLayout-generated children store the DataTemplate that created them in a private attached property.");
			builder.AppendLine("  Removal clears generated item BindingContexts, but it does not clear the private template marker.");
			builder.AppendLine("  A retained removed child can therefore keep a page-local runtime-XAML DataTemplate alive, and that template factory keeps the discarded XAML root page alive.");
			builder.AppendLine();
			Baseline.AppendTo(builder);
			builder.AppendLine();
			Control.AppendTo(builder);
			builder.AppendLine();
			Current.AppendTo(builder);
			return builder.ToString();
		}
	}
}
