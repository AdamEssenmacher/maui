#pragma warning disable CS0618

using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace ListViewHeaderFooterParentRetentionRepro;

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
			Text = "Running ListView header/footer parent retention repro...",
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
			var text = "ListViewHeaderFooterParentRetentionRepro ERROR" + Environment.NewLine + ex;
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(1);
		}
	}
}

static class ReproSession
{
	public const string ResultsPath = "/tmp/listview-headerfooter-parent-retention-results.txt";

	const int ListViewCount = 48;
	const int RetiredHeaderFooterPairsPerListView = 2;
	const int HeaderFooterSlotsPerPair = 2;
	const int PayloadBytes = 1024 * 1024;

	public static ReproReport Run()
	{
		var control = RunScenario(clearRetiredParentsBeforeReplacement: true);
		var current = RunScenario(clearRetiredParentsBeforeReplacement: false);

		return new ReproReport(control, current);
	}

	static ScenarioResult RunScenario(bool clearRetiredParentsBeforeReplacement)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
		var retainedListViews = new List<ListView>(ListViewCount);
		var viewReferences = new List<WeakReference<PayloadChromeView>>(ListViewCount * RetiredHeaderFooterPairsPerListView * HeaderFooterSlotsPerPair);
		var payloadReferences = new List<WeakReference<ChromePayload>>(ListViewCount * RetiredHeaderFooterPairsPerListView * HeaderFooterSlotsPerPair);
		var payloadBufferReferences = new List<WeakReference<byte[]>>(ListViewCount * RetiredHeaderFooterPairsPerListView * HeaderFooterSlotsPerPair);

		for (var listViewIndex = 0; listViewIndex < ListViewCount; listViewIndex++)
		{
			CreateRetainedListView(
				clearRetiredParentsBeforeReplacement,
				listViewIndex,
				retainedListViews,
				viewReferences,
				payloadReferences,
				payloadBufferReferences);
		}

		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			retainedListViews.Count,
			CountAlive(viewReferences),
			CountAliveWithLiveParent(viewReferences),
			CountAlive(payloadReferences),
			CountAlive(payloadBufferReferences),
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedListViews);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateRetainedListView(
		bool clearRetiredParentsBeforeReplacement,
		int listViewIndex,
		List<ListView> retainedListViews,
		List<WeakReference<PayloadChromeView>> viewReferences,
		List<WeakReference<ChromePayload>> payloadReferences,
		List<WeakReference<byte[]>> payloadBufferReferences)
	{
		var listView = new ListView
		{
			HasUnevenRows = true,
			RowHeight = 56,
			ItemsSource = new[]
			{
				$"Customer queue {listViewIndex:000}",
				$"Escalations {listViewIndex:000}",
				$"SLA watch {listViewIndex:000}"
			}
		};

		PayloadChromeView? previousHeader = null;
		PayloadChromeView? previousFooter = null;

		for (var pairIndex = 0; pairIndex < RetiredHeaderFooterPairsPerListView; pairIndex++)
		{
			if (clearRetiredParentsBeforeReplacement)
				ClearParents(previousHeader, previousFooter);

			var headerPayload = CreatePayload($"header-{listViewIndex:000}-{pairIndex:00}", listViewIndex, pairIndex);
			var footerPayload = CreatePayload($"footer-{listViewIndex:000}-{pairIndex:00}", listViewIndex, pairIndex);
			var header = new PayloadChromeView("Regional operations", headerPayload);
			var footer = new PayloadChromeView("Projected closeout", footerPayload);

			listView.Header = header;
			listView.Footer = footer;

			viewReferences.Add(new WeakReference<PayloadChromeView>(header));
			viewReferences.Add(new WeakReference<PayloadChromeView>(footer));
			payloadReferences.Add(new WeakReference<ChromePayload>(headerPayload));
			payloadReferences.Add(new WeakReference<ChromePayload>(footerPayload));
			payloadBufferReferences.Add(new WeakReference<byte[]>(headerPayload.Buffer));
			payloadBufferReferences.Add(new WeakReference<byte[]>(footerPayload.Buffer));

			previousHeader = header;
			previousFooter = footer;
			header = null!;
			footer = null!;
			headerPayload = null!;
			footerPayload = null!;
		}

		if (clearRetiredParentsBeforeReplacement)
			ClearParents(previousHeader, previousFooter);

		listView.Header = new Label
		{
			Text = $"Live operations header {listViewIndex:000}",
			FontSize = 12,
			Padding = new Thickness(12, 8)
		};

		listView.Footer = new Label
		{
			Text = $"Live operations footer {listViewIndex:000}",
			FontSize = 12,
			Padding = new Thickness(12, 8)
		};

		retainedListViews.Add(listView);
		listView = null!;
		previousHeader = null;
		previousFooter = null;
	}

	static ChromePayload CreatePayload(string name, int listViewIndex, int pairIndex)
	{
		var payload = new ChromePayload(name, new byte[PayloadBytes]);
		payload.Buffer[0] = (byte)(listViewIndex + pairIndex);
		return payload;
	}

	static void ClearParents(params Element?[] elements)
	{
		foreach (var element in elements)
			if (element is not null)
				element.Parent = null;
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

	static int CountAliveWithLiveParent(IEnumerable<WeakReference<PayloadChromeView>> references)
	{
		var count = 0;
		foreach (var reference in references)
		{
			if (reference.TryGetTarget(out var view) && view.Parent is ListView)
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

	sealed class PayloadChromeView : ContentView
	{
		public PayloadChromeView(string title, ChromePayload payload)
		{
			Payload = payload;
			BindingContext = payload;
			Content = new VerticalStackLayout
			{
				Padding = new Thickness(12, 8),
				Spacing = 2,
				Children =
				{
					new Label
					{
						Text = title,
						FontSize = 13,
						FontAttributes = FontAttributes.Bold
					},
					new Label
					{
						Text = payload.Name,
						FontSize = 11,
						LineBreakMode = LineBreakMode.TailTruncation
					}
				}
			};
		}

		public ChromePayload Payload { get; }
	}

	sealed class ChromePayload
	{
		public ChromePayload(string name, byte[] buffer)
		{
			Name = name;
			Buffer = buffer;
		}

		public string Name { get; }

		public byte[] Buffer { get; }
	}

	public readonly record struct ScenarioResult(
		int RetainedListViews,
		int RetiredViewsAlive,
		int RetiredViewsStillParented,
		int PayloadsAlive,
		int PayloadBuffersAlive,
		long HeapBefore,
		long HeapAfter)
	{
		public long HeapDelta => HeapAfter - HeapBefore;
	}

	public readonly record struct ReproReport(ScenarioResult Control, ScenarioResult Current)
	{
		public bool LeakProved =>
			Control.RetiredViewsAlive == 0 &&
			Control.PayloadsAlive == 0 &&
			Control.PayloadBuffersAlive == 0 &&
			Current.RetiredViewsAlive == RetiredPayloadCount &&
			Current.RetiredViewsStillParented == RetiredPayloadCount &&
			Current.PayloadsAlive == RetiredPayloadCount &&
			Current.PayloadBuffersAlive == RetiredPayloadCount;

		static int RetiredPayloadCount => ListViewCount * RetiredHeaderFooterPairsPerListView * HeaderFooterSlotsPerPair;

		public string ToText()
		{
			var retainedBytes = Current.PayloadBuffersAlive * PayloadBytes;
			var builder = new StringBuilder();
			builder.AppendLine(LeakProved ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			builder.AppendLine("ListViewHeaderFooterParentRetentionRepro");
			builder.AppendLine($"Live ListView owners retained in both scenarios: {ListViewCount}");
			builder.AppendLine($"Retired header/footer pairs per ListView: {RetiredHeaderFooterPairsPerListView}");
			builder.AppendLine($"Retired header/footer payload views created per run: {RetiredPayloadCount}");
			builder.AppendLine($"Payload per retired header/footer view model: {PayloadBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Run: control: clear retired Header/Footer Parent before replacement");
			AppendScenario(builder, Control);
			builder.AppendLine();
			builder.AppendLine("Run: current: replace Header/Footer through ListView.OnHeaderOrFooterChanged");
			AppendScenario(builder, Current);
			builder.AppendLine($"  retained retired header/footer payload bytes: {retainedBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Leak path: live ListView -> Element resource-listener delegates -> retired Header/Footer views -> BindingContext/Payload buffers.");
			builder.AppendLine("Distinct from iOS ListView header/footer handler-disconnect issues: no native handler is required; this is core parent cleanup while the ListView remains live.");
			builder.AppendLine($"dotnet-version: {Environment.Version}");
			return builder.ToString();
		}

		static void AppendScenario(StringBuilder builder, ScenarioResult result)
		{
			builder.AppendLine($"  live ListViews retained by app/page cache: {result.RetainedListViews}");
			builder.AppendLine($"  retired header/footer views alive after full GC: {result.RetiredViewsAlive}/{RetiredPayloadCount}");
			builder.AppendLine($"  retired header/footer views still parented to live ListViews: {result.RetiredViewsStillParented}/{RetiredPayloadCount}");
			builder.AppendLine($"  retired payloads alive after full GC: {result.PayloadsAlive}/{RetiredPayloadCount}");
			builder.AppendLine($"  retired payload buffers alive after full GC: {result.PayloadBuffersAlive}/{RetiredPayloadCount}");
			builder.AppendLine($"  managed heap delta: {result.HeapDelta / 1024d / 1024d:0.0} MiB");
		}
	}
}
