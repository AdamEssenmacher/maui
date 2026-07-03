using System.Collections;
using System.Reflection;
using System.Text;
using Foundation;
using Microsoft.Maui.Controls.Platform.Compatibility;
using UIKit;

namespace ShellSearchResultsParentRetentionRepro;

internal static class ShellSearchResultsParentRetentionProbe
{
	const int Iterations = 96;
	const int PayloadBytes = 1_048_576;

	static readonly FieldInfo ChangeHandlersField =
		typeof(Element).GetField("_changeHandlers", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(Element).FullName, "_changeHandlers");

	public static async Task RunAsync(Window window)
	{
		try
		{
			await WriteProgressAsync("START ShellSearchResultsRenderer parent retention probe");
			await MainThread.InvokeOnMainThreadAsync(async () =>
			{
				await Task.Delay(700);

				var control = await RunScenarioAsync(window, clearParentBeforeRelease: true);
				window.Page = CreateIdlePage("Between scenarios");
				await MemorySampler.ForceFullCollectionAsync();

				var current = await RunScenarioAsync(window, clearParentBeforeRelease: false);

				var proven = control.CreatedCells == Iterations &&
					current.CreatedCells == Iterations &&
					control.AlivePayloads <= 2 &&
					control.AliveResultViews <= 2 &&
					control.ShellResourceListeners <= 2 &&
					current.AlivePayloads >= Iterations * 3 / 4 &&
					current.AliveResultViews >= Iterations * 3 / 4 &&
					current.ShellResourceListeners >= Iterations * 3 / 4;

				var report = FormatReport(proven, control, current);
				await WriteReportAsync(report);

				Environment.Exit(proven ? 0 : 2);
			});
		}
		catch (Exception ex)
		{
			await WriteReportAsync("Result: ERROR" + Environment.NewLine + ex);
			Environment.Exit(3);
		}
	}

	static async Task<ScenarioResult> RunScenarioAsync(Window window, bool clearParentBeforeRelease)
	{
		var scenarioName = clearParentBeforeRelease ? "control" : "current";
		var payloadRefs = new List<WeakReference<SearchResultPayload>>(Iterations);
		var viewRefs = new List<WeakReference<View>>(Iterations);
		var shell = new PayloadShell(scenarioName);
		var context = new ProbeShellContext(shell);
		var searchHandler = new SearchHandler
		{
			ShowsResults = true,
			ItemTemplate = CreateResultTemplate()
		};
		var renderer = new ShellSearchResultsRenderer(context);
		((IShellSearchResultsRenderer)renderer).SearchHandler = searchHandler;

		window.Page = shell;
		await WaitForLoadedAsync(shell);

		using var tableView = new UITableView();
		var createdCells = 0;

		for (var i = 0; i < Iterations; i++)
		{
			var payload = new SearchResultPayload(i, PayloadBytes);
			var items = new[] { payload };
			searchHandler.ItemsSource = items;

			var cell = (UIContainerCell)renderer.GetCell(tableView, NSIndexPath.FromRowSection(0, 0));
			createdCells++;
			payloadRefs.Add(new(payload));
			viewRefs.Add(new(cell.View));

			if (clearParentBeforeRelease)
				cell.View.Parent = null;

			searchHandler.ItemsSource = null;
			cell.Dispose();

			if ((i + 1) % 12 == 0 || i + 1 == Iterations)
				await WriteProgressAsync(scenarioName + ": generated " + (i + 1) + "/" + Iterations);
		}

		renderer.Dispose();
		searchHandler = null!;
		context = null!;

		await WriteProgressAsync(scenarioName + ": forcing full collection");
		await MemorySampler.ForceFullCollectionAsync();

		var alivePayloads = CountAlive(payloadRefs);
		var aliveViews = CountAlive(viewRefs);
		var shellResourceListeners = CountShellResourceListeners(shell);

		GC.KeepAlive(shell);

		return new ScenarioResult(
			clearParentBeforeRelease,
			createdCells,
			alivePayloads,
			aliveViews,
			shellResourceListeners,
			alivePayloads * PayloadBytes);
	}

	static DataTemplate CreateResultTemplate()
	{
		return new DataTemplate(() =>
		{
			var title = new Label
			{
				FontSize = 13,
				Margin = new Thickness(8, 4, 8, 0)
			};
			title.SetBinding(Label.TextProperty, nameof(SearchResultPayload.Title));

			var details = new Label
			{
				FontSize = 11,
				Margin = new Thickness(8, 0, 8, 4)
			};
			details.SetBinding(Label.TextProperty, nameof(SearchResultPayload.Description));

			return new VerticalStackLayout
			{
				Children =
				{
					title,
					details
				}
			};
		});
	}

	static ContentPage CreateIdlePage(string title)
	{
		return new ContentPage
		{
			Title = title,
			Content = new Label
			{
				Text = title,
				HorizontalOptions = LayoutOptions.Center,
				VerticalOptions = LayoutOptions.Center
			}
		};
	}

	static async Task WaitForLoadedAsync(Page page)
	{
		for (var i = 0; i < 50; i++)
		{
			if (page.IsLoaded && page.Handler?.MauiContext is not null)
				return;

			await Task.Delay(25);
		}
	}

	static int CountShellResourceListeners(Shell shell)
	{
		if (ChangeHandlersField.GetValue(shell) is not ICollection handlers)
			return 0;

		return handlers.Count;
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

	static string FormatReport(bool proven, ScenarioResult control, ScenarioResult current)
	{
		var builder = new StringBuilder();
		builder.AppendLine(proven ? "Result: PROVEN" : "Result: NOT PROVEN");
		builder.AppendLine("Candidate: ShellSearchResultsRenderer result views remain parented to the live Shell");
		builder.AppendLine("Iterations: " + Iterations);
		builder.AppendLine("Payload per generated search result: " + PayloadBytes + " bytes");
		builder.AppendLine();
		AppendScenario(builder, "Control (clear generated result view Parent before release)", control);
		AppendScenario(builder, "Current MAUI (renderer leaves generated result view Parent assigned)", current);
		builder.AppendLine();
		builder.AppendLine("Severity signal: " + (current.AlivePayloadBytes / 1024d / 1024d).ToString("F1") + " MiB of abandoned search-result payload retained by a live Shell.");
		return builder.ToString();
	}

	static void AppendScenario(StringBuilder builder, string title, ScenarioResult result)
	{
		builder.AppendLine(title + ":");
		builder.AppendLine("  Created renderer cells: " + result.CreatedCells + "/" + Iterations);
		builder.AppendLine("  Alive result views: " + result.AliveResultViews + "/" + Iterations);
		builder.AppendLine("  Alive payloads: " + result.AlivePayloads + "/" + Iterations);
		builder.AppendLine("  Shell resource listeners: " + result.ShellResourceListeners);
		builder.AppendLine("  Alive payload bytes: " + result.AlivePayloadBytes);
	}

	static async Task WriteReportAsync(string report)
	{
		Console.WriteLine(report);
		await File.WriteAllTextAsync(AutoRunSettings.ResultsPath, report);
	}

	static async Task WriteProgressAsync(string message)
	{
		await File.AppendAllTextAsync(AutoRunSettings.ResultsPath, message + Environment.NewLine);
	}

	sealed class PayloadShell : Shell
	{
		public PayloadShell(string scenarioName)
		{
			Title = "Search results " + scenarioName;
			FlyoutBehavior = FlyoutBehavior.Disabled;

			Items.Add(new TabBar
			{
				Items =
				{
					new Tab
					{
						Title = scenarioName,
						Items =
						{
							new ShellContent
							{
								Title = scenarioName,
								Content = CreateIdlePage("Live Shell for " + scenarioName)
							}
						}
					}
				}
			});
		}
	}

	sealed class SearchResultPayload
	{
		public SearchResultPayload(int id, int bytes)
		{
			Id = id;
			Buffer = new byte[bytes];
			Buffer[0] = (byte)(id % 251);
			Buffer[^1] = (byte)((id + 41) % 251);
		}

		public int Id { get; }
		public byte[] Buffer { get; }
		public string Title => "Customer order search result " + Id;
		public string Description => "Generated payload bytes: " + Buffer.Length;
	}

	sealed class ProbeShellContext : IShellContext
	{
		public ProbeShellContext(Shell shell)
		{
			Shell = shell;
		}

		public bool AllowFlyoutGesture => false;
		public IShellItemRenderer CurrentShellItemRenderer => throw new NotSupportedException();
		public Shell Shell { get; }
		public IShellPageRendererTracker CreatePageRendererTracker() => throw new NotSupportedException();
		public IShellFlyoutContentRenderer CreateShellFlyoutContentRenderer() => throw new NotSupportedException();
		public IShellSectionRenderer CreateShellSectionRenderer(ShellSection shellSection) => throw new NotSupportedException();
		public IShellNavBarAppearanceTracker CreateNavBarAppearanceTracker() => throw new NotSupportedException();
		public IShellTabBarAppearanceTracker CreateTabBarAppearanceTracker() => throw new NotSupportedException();
		public IShellSearchResultsRenderer CreateShellSearchResultsRenderer() => throw new NotSupportedException();
	}

	readonly record struct ScenarioResult(
		bool ClearParentBeforeRelease,
		int CreatedCells,
		int AlivePayloads,
		int AliveResultViews,
		int ShellResourceListeners,
		long AlivePayloadBytes);
}
