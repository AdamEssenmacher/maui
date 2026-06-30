using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Platform.Compatibility;
using UIKit;

namespace IosShellSearchResultsControllerRetentionRepro;

public sealed class App : Application
{
	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new RunnerPage());
	}
}

sealed class RunnerPage : ContentPage
{
	bool _ran;

	public RunnerPage()
	{
		Content = new Label
		{
			Text = "Running Shell search results controller retention repro...",
			HorizontalOptions = LayoutOptions.Center,
			VerticalOptions = LayoutOptions.Center
		};
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		await TryRunAsync();
	}

	protected override async void OnHandlerChanged()
	{
		base.OnHandlerChanged();
		await TryRunAsync();
	}

	async Task TryRunAsync()
	{
		if (_ran || Handler?.MauiContext is null)
			return;

		_ran = true;
		await Task.Delay(250);

		try
		{
			var report = ReproSession.Run();
			var text = report.ToText();
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(report.LeakProved ? 0 : 2);
		}
		catch (Exception ex)
		{
			var text = "IosShellSearchResultsControllerRetentionRepro ERROR" + Environment.NewLine + ex;
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(1);
		}
	}
}

static class ReproSession
{
	public const string ResultsPath = "/tmp/ios-shell-searchresultscontroller-retention-results.txt";

	const int Iterations = 96;
	const int ShellItemsPerShell = 3;
	const int SearchResultsPerShell = 8;
	const int PayloadBytes = 1024 * 1024;

	public static ReproReport Run()
	{
		var control = RunScenario(createSearchResultsController: false);
		var current = RunScenario(createSearchResultsController: true);

		return new ReproReport(control, current);
	}

	static ScenarioResult RunScenario(bool createSearchResultsController)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
		var retainedControllers = new List<UISearchController>(Iterations);
		var shellReferences = new List<WeakReference<Shell>>(Iterations);
		var payloadReferences = new List<WeakReference<ShellPayload>>(Iterations);
		var payloadBufferReferences = new List<WeakReference<byte[]>>(Iterations);
		var searchHandlerReferences = new List<WeakReference<SearchHandler>>(Iterations);
		var resultsRendererReferences = new List<WeakReference<ShellSearchResultsRenderer>>(Iterations);

		for (var i = 0; i < Iterations; i++)
		{
			CreateRetainedSearchController(
				createSearchResultsController,
				i,
				retainedControllers,
				shellReferences,
				payloadReferences,
				payloadBufferReferences,
				searchHandlerReferences,
				resultsRendererReferences);
		}

		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			CountAlive(shellReferences),
			CountAlive(payloadReferences),
			CountAlive(payloadBufferReferences),
			CountAlive(searchHandlerReferences),
			CountAlive(resultsRendererReferences),
			retainedControllers.Count,
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedControllers);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateRetainedSearchController(
		bool createSearchResultsController,
		int iteration,
		List<UISearchController> retainedControllers,
		List<WeakReference<Shell>> shellReferences,
		List<WeakReference<ShellPayload>> payloadReferences,
		List<WeakReference<byte[]>> payloadBufferReferences,
		List<WeakReference<SearchHandler>> searchHandlerReferences,
		List<WeakReference<ShellSearchResultsRenderer>> resultsRendererReferences)
	{
		var payload = new ShellPayload($"shell-search-results-{iteration}", new byte[PayloadBytes]);
		payload.Buffer[0] = (byte)iteration;

		var shell = CreateShell(iteration, payload);
		var context = new TestShellContext(shell);
		var searchHandler = CreateSearchHandler(iteration);

		UISearchController searchController;
		ShellSearchResultsRenderer? resultsRenderer = null;

		if (createSearchResultsController)
		{
			resultsRenderer = new ShellSearchResultsRenderer(context);
			((IShellSearchResultsRenderer)resultsRenderer).SearchHandler = searchHandler;
			searchController = new UISearchController(resultsRenderer);

			// This matches ShellPageRendererTracker teardown: the result renderer is disposed,
			// but a retained native UISearchController still owns its SearchResultsController.
			resultsRenderer.Dispose();
			resultsRendererReferences.Add(new WeakReference<ShellSearchResultsRenderer>(resultsRenderer));
		}
		else
		{
			searchController = new UISearchController(searchResultsController: null);
		}

		retainedControllers.Add(searchController);
		shellReferences.Add(new WeakReference<Shell>(shell));
		payloadReferences.Add(new WeakReference<ShellPayload>(payload));
		payloadBufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));
		searchHandlerReferences.Add(new WeakReference<SearchHandler>(searchHandler));

		searchController = null!;
		resultsRenderer = null;
		searchHandler = null!;
		context = null!;
		shell = null!;
		payload = null!;
	}

	static Shell CreateShell(int iteration, ShellPayload payload)
	{
		var shell = new Shell
		{
			BindingContext = payload,
			Title = $"Customer search workspace {iteration}"
		};

		for (var itemIndex = 0; itemIndex < ShellItemsPerShell; itemIndex++)
		{
			var flyoutItem = new FlyoutItem
			{
				Title = $"Region {itemIndex + 1}"
			};

			var section = new ShellSection
			{
				Title = $"Orders {itemIndex + 1}"
			};

			section.Items.Add(new ShellContent
			{
				Title = $"Open orders {itemIndex + 1}",
				Content = new ContentPage
				{
					Title = $"Order queue {iteration}-{itemIndex}",
					BindingContext = payload,
					Content = new Label { Text = $"Order search payload {iteration}-{itemIndex}" }
				}
			});

			flyoutItem.Items.Add(section);
			shell.Items.Add(flyoutItem);
		}

		return shell;
	}

	static SearchHandler CreateSearchHandler(int iteration)
	{
		var results = new ObservableCollection<SearchResultItem>();
		for (var resultIndex = 0; resultIndex < SearchResultsPerShell; resultIndex++)
		{
			results.Add(new SearchResultItem($"Order {iteration:000}-{resultIndex:00}"));
		}

		return new SearchHandler
		{
			Query = $"customer:{iteration:000}",
			Placeholder = "Search orders, invoices, and customer notes",
			ShowsResults = true,
			ItemsSource = results,
			DisplayMemberName = nameof(SearchResultItem.Title)
		};
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

	static void ForceGc()
	{
		for (var i = 0; i < 6; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(75);
		}
	}

	sealed class TestShellContext : IShellContext
	{
		public TestShellContext(Shell shell)
		{
			Shell = shell;
		}

		public bool AllowFlyoutGesture => true;

		public IShellItemRenderer CurrentShellItemRenderer => throw new NotSupportedException();

		public Shell Shell { get; }

		public IShellPageRendererTracker CreatePageRendererTracker() => throw new NotSupportedException();

		public IShellFlyoutContentRenderer CreateShellFlyoutContentRenderer() => throw new NotSupportedException();

		public IShellSectionRenderer CreateShellSectionRenderer(ShellSection shellSection) => throw new NotSupportedException();

		public IShellNavBarAppearanceTracker CreateNavBarAppearanceTracker() => throw new NotSupportedException();

		public IShellTabBarAppearanceTracker CreateTabBarAppearanceTracker() => throw new NotSupportedException();

		public IShellSearchResultsRenderer CreateShellSearchResultsRenderer() => throw new NotSupportedException();
	}

	sealed class SearchResultItem
	{
		public SearchResultItem(string title)
		{
			Title = title;
		}

		public string Title { get; }
	}

	sealed class ShellPayload
	{
		public ShellPayload(string name, byte[] buffer)
		{
			Name = name;
			Buffer = buffer;
		}

		public string Name { get; }

		public byte[] Buffer { get; }
	}

	public readonly record struct ScenarioResult(
		int ShellsAlive,
		int PayloadsAlive,
		int PayloadBuffersAlive,
		int SearchHandlersAlive,
		int ResultsRenderersAlive,
		int RetainedSearchControllers,
		long HeapBefore,
		long HeapAfter)
	{
		public long HeapDelta => HeapAfter - HeapBefore;
	}

	public readonly record struct ReproReport(ScenarioResult Control, ScenarioResult Current)
	{
		public bool LeakProved =>
			Control.ShellsAlive == 0 &&
			Control.PayloadsAlive == 0 &&
			Control.PayloadBuffersAlive == 0 &&
			Current.ShellsAlive == Iterations &&
			Current.PayloadsAlive == Iterations &&
			Current.PayloadBuffersAlive == Iterations &&
			Current.ResultsRenderersAlive == Iterations;

		public string ToText()
		{
			var retainedBytes = Current.PayloadBuffersAlive * PayloadBytes;
			var builder = new StringBuilder();
			builder.AppendLine(LeakProved ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			builder.AppendLine("IosShellSearchResultsControllerRetentionRepro");
			builder.AppendLine($"Shell graphs created: {Iterations}");
			builder.AppendLine($"Realistic Shell shape: {ShellItemsPerShell} flyout items x 1 section x 1 content page");
			builder.AppendLine($"Search result rows per SearchHandler: {SearchResultsPerShell}");
			builder.AppendLine($"Payload per Shell graph: {PayloadBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Run: control: retained UISearchController peers with no search-results controller");
			AppendScenario(builder, Control);
			builder.AppendLine();
			builder.AppendLine("Run: current: retained UISearchController peers after ShellSearchResultsRenderer.Dispose()");
			AppendScenario(builder, Current);
			builder.AppendLine($"  retained payload bytes: {retainedBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Leak path: retained native UISearchController -> SearchResultsController/ShellSearchResultsRenderer -> readonly _context -> Shell -> Shell page BindingContext payload");
			builder.AppendLine("Distinct from Shell search-bar text/icon/accessory retention: the retained object is the search results controller, SearchHandlers collect, and no native search text/icon payload is needed.");
			builder.AppendLine($"dotnet-version: {Environment.Version}");
			return builder.ToString();
		}

		static void AppendScenario(StringBuilder builder, ScenarioResult result)
		{
			builder.AppendLine($"  retained UISearchController peers: {result.RetainedSearchControllers}");
			builder.AppendLine($"  ShellSearchResultsRenderers alive after full GC: {result.ResultsRenderersAlive}/{Iterations}");
			builder.AppendLine($"  Shells alive after full GC: {result.ShellsAlive}/{Iterations}");
			builder.AppendLine($"  Shell payloads alive after full GC: {result.PayloadsAlive}/{Iterations}");
			builder.AppendLine($"  payload buffers alive after full GC: {result.PayloadBuffersAlive}/{Iterations}");
			builder.AppendLine($"  SearchHandlers alive after full GC: {result.SearchHandlersAlive}/{Iterations}");
			builder.AppendLine($"  managed heap delta: {result.HeapDelta / 1024d / 1024d:0.0} MiB");
		}
	}
}
