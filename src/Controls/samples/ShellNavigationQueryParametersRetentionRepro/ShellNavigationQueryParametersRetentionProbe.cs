using System.Text;

namespace ShellNavigationQueryParametersRetentionRepro;

internal static class ShellNavigationQueryParametersRetentionProbe
{
	const int Iterations = 96;
	const int PayloadBytes = 1_048_576;
	const string PayloadKey = "payload";
	const string IndexKey = "index";

	public static async Task RunAsync(Window window)
	{
		try
		{
			await WriteProgressAsync("START ShellNavigationQueryParameters retention probe");
			await MainThread.InvokeOnMainThreadAsync(async () =>
			{
				await Task.Delay(500);

				var control = await RunScenarioAsync(window, PageKind.ContentPage);
				await MemorySampler.ForceFullCollectionAsync();

				var current = await RunScenarioAsync(window, PageKind.TabbedPage);
				var proven = control.DeliveredPayloads == Iterations &&
					current.DeliveredPayloads == Iterations &&
					control.AlivePages >= Iterations * 3 / 4 &&
					current.AlivePages >= Iterations * 3 / 4 &&
					control.AlivePayloads <= 2 &&
					current.AlivePayloads >= Iterations * 3 / 4;

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

	static async Task<ScenarioResult> RunScenarioAsync(Window window, PageKind pageKind)
	{
		var scenarioName = pageKind == PageKind.ContentPage ? "control-contentpage" : "current-tabbedpage";
		var route = "querypayload-" + scenarioName;
		var payloadRefs = new List<WeakReference<QueryPayload>>(Iterations);
		var pageRefs = new List<WeakReference<Page>>(Iterations);
		var deliveredPayloads = 0;
		var shell = CreateShell(scenarioName);

		Routing.UnRegisterRoute(route);
		Routing.RegisterRoute(route, pageKind == PageKind.ContentPage ? typeof(QueryContentPage) : typeof(QueryTabbedPage));

		window.Page = shell;
		await WaitForUiAsync();

		for (var i = 0; i < Iterations; i++)
		{
			await WriteProgressAsync(scenarioName + ": navigation " + (i + 1) + "/" + Iterations);
			if (await NavigateWithPayloadAsync(shell, route, i, payloadRefs, pageRefs))
				deliveredPayloads++;
		}

		await WriteProgressAsync(scenarioName + ": forcing full collection");
		await MemorySampler.ForceFullCollectionAsync();

		var alivePayloads = CountAlive(payloadRefs);
		var alivePages = CountAlive(pageRefs);
		var stackDepth = shell.Navigation.NavigationStack.Count;

		return new ScenarioResult(
			pageKind,
			deliveredPayloads,
			alivePayloads,
			alivePages,
			stackDepth,
			alivePayloads * PayloadBytes);
	}

	static async Task<bool> NavigateWithPayloadAsync(
		Shell shell,
		string route,
		int index,
		List<WeakReference<QueryPayload>> payloadRefs,
		List<WeakReference<Page>> pageRefs)
	{
		QueryPayload? payload = new(index, PayloadBytes);
		ShellNavigationQueryParameters? query = new ShellNavigationQueryParameters
		{
			{ PayloadKey, payload },
			{ IndexKey, index }
		};

		payloadRefs.Add(new WeakReference<QueryPayload>(payload));

		await shell.GoToAsync(new ShellNavigationState(route), animate: false, query)
			.WaitAsync(TimeSpan.FromSeconds(15));
		await WaitForUiAsync();

		var currentPage = shell.Navigation.NavigationStack.LastOrDefault();
		if (currentPage is not null)
			pageRefs.Add(new WeakReference<Page>(currentPage));

		var delivered = currentPage is IProbeQueryPage probe &&
			probe.AppliedCount == 1 &&
			probe.LastPayloadId == index &&
			probe.LastPayloadBytes == PayloadBytes;

		query = null;
		payload = null;
		await Task.Yield();

		return delivered;
	}

	static Shell CreateShell(string scenarioName)
	{
		var shell = new Shell
		{
			Title = "Query parameter probe " + scenarioName,
			FlyoutBehavior = FlyoutBehavior.Disabled
		};

		shell.Items.Add(new ShellContent
		{
			Route = "home-" + scenarioName,
			Title = "Home",
			Content = new ContentPage
			{
				Title = "Home",
				Content = new Label
				{
					Text = "Keeping routed pages alive while single-use payloads should collect.",
					HorizontalOptions = LayoutOptions.Center,
					VerticalOptions = LayoutOptions.Center
				}
			}
		});

		return shell;
	}

	static async Task WaitForUiAsync()
	{
		await Task.Delay(60);
		await Task.Yield();
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
		builder.AppendLine("Candidate: ShellNavigationQueryParameters payloads are not cleared for non-ContentPage Page targets.");
		builder.AppendLine("Iterations: " + Iterations);
		builder.AppendLine("Payload per navigation: " + PayloadBytes + " bytes");
		builder.AppendLine();
		AppendScenario(builder, "Control (ContentPage route target)", control);
		AppendScenario(builder, "Current MAUI (TabbedPage route target)", current);
		builder.AppendLine();
		builder.AppendLine("Severity signal: " + (current.AlivePayloadBytes / 1024d / 1024d).ToString("F1") + " MiB of single-use Shell navigation payload retained while routed pages remain in the navigation stack.");
		return builder.ToString();
	}

	static void AppendScenario(StringBuilder builder, string title, ScenarioResult result)
	{
		builder.AppendLine(title + ":");
		builder.AppendLine("  Delivered payloads: " + result.DeliveredPayloads + "/" + Iterations);
		builder.AppendLine("  Alive routed pages: " + result.AlivePages + "/" + Iterations);
		builder.AppendLine("  Shell navigation stack depth: " + result.NavigationStackDepth);
		builder.AppendLine("  Alive single-use payloads: " + result.AlivePayloads + "/" + Iterations);
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

	enum PageKind
	{
		ContentPage,
		TabbedPage
	}

	interface IProbeQueryPage
	{
		int AppliedCount { get; }
		int LastPayloadId { get; }
		int LastPayloadBytes { get; }
	}

	sealed class QueryContentPage : ContentPage, IProbeQueryPage, IQueryAttributable
	{
		public QueryContentPage()
		{
			Title = "ContentPage target";
			Content = new Label
			{
				Text = "ContentPage query target",
				HorizontalOptions = LayoutOptions.Center,
				VerticalOptions = LayoutOptions.Center
			};
		}

		public int AppliedCount { get; set; }
		public int LastPayloadId { get; set; } = -1;
		public int LastPayloadBytes { get; set; }

		public void ApplyQueryAttributes(IDictionary<string, object> query)
		{
			CaptureQuery(query, this);
		}
	}

	sealed class QueryTabbedPage : TabbedPage, IProbeQueryPage, IQueryAttributable
	{
		public QueryTabbedPage()
		{
			Title = "TabbedPage target";
			Children.Add(new ContentPage
			{
				Title = "First tab",
				Content = new Label
				{
					Text = "TabbedPage query target",
					HorizontalOptions = LayoutOptions.Center,
					VerticalOptions = LayoutOptions.Center
				}
			});
		}

		public int AppliedCount { get; set; }
		public int LastPayloadId { get; set; } = -1;
		public int LastPayloadBytes { get; set; }

		public void ApplyQueryAttributes(IDictionary<string, object> query)
		{
			CaptureQuery(query, this);
		}
	}

	sealed class QueryPayload
	{
		readonly byte[] _buffer;

		public QueryPayload(int id, int bytes)
		{
			Id = id;
			_buffer = new byte[bytes];
			_buffer[0] = (byte)(id % 251);
			_buffer[^1] = (byte)((id + 17) % 251);
		}

		public int Id { get; }
		public int Length => _buffer.Length;
	}

	readonly record struct ScenarioResult(
		PageKind PageKind,
		int DeliveredPayloads,
		int AlivePayloads,
		int AlivePages,
		int NavigationStackDepth,
		long AlivePayloadBytes);

	static void CaptureQuery(IDictionary<string, object> query, IProbeQueryPage target)
	{
		if (target is QueryContentPage contentPage)
			CaptureQuery(query, contentPage);
		else if (target is QueryTabbedPage tabbedPage)
			CaptureQuery(query, tabbedPage);
	}

	static void CaptureQuery(IDictionary<string, object> query, QueryContentPage page)
	{
		page.AppliedCount++;
		if (query.TryGetValue(PayloadKey, out var payloadValue) && payloadValue is QueryPayload payload)
		{
			page.LastPayloadId = payload.Id;
			page.LastPayloadBytes = payload.Length;
		}
	}

	static void CaptureQuery(IDictionary<string, object> query, QueryTabbedPage page)
	{
		page.AppliedCount++;
		if (query.TryGetValue(PayloadKey, out var payloadValue) && payloadValue is QueryPayload payload)
		{
			page.LastPayloadId = payload.Id;
			page.LastPayloadBytes = payload.Length;
		}
	}
}
