using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Internals;
using Microsoft.Maui.Graphics;

namespace TitleBarPassthroughTemplateRetentionRepro;

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
			Text = "Running TitleBar passthrough template retention repro...",
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
			var text = "TitleBarPassthroughTemplateRetentionRepro ERROR" + Environment.NewLine + ex;
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(1);
		}
	}
}

static class ReproSession
{
	public const string ResultsPath = "/tmp/titlebar-passthrough-template-retention-results.txt";

	const int TitleBarCount = 48;
	const int RetiredTemplatesPerTitleBar = 4;
	const int PayloadBytes = 1024 * 1024;
	const int PassthroughSlotsPerTemplate = 3;

	public static ReproReport Run()
	{
		var control = RunScenario(clearPassthroughBeforeTemplateApply: true);
		var current = RunScenario(clearPassthroughBeforeTemplateApply: false);

		return new ReproReport(control, current);
	}

	static ScenarioResult RunScenario(bool clearPassthroughBeforeTemplateApply)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
		var retainedTitleBars = new List<TitleBar>(TitleBarCount);
		var payloadReferences = new List<WeakReference<TemplatePayload>>(TitleBarCount * RetiredTemplatesPerTitleBar);
		var payloadBufferReferences = new List<WeakReference<byte[]>>(TitleBarCount * RetiredTemplatesPerTitleBar);
		var passthroughCounts = new List<int>(TitleBarCount);

		for (var titleBarIndex = 0; titleBarIndex < TitleBarCount; titleBarIndex++)
		{
			CreateRetainedTitleBar(
				clearPassthroughBeforeTemplateApply,
				titleBarIndex,
				retainedTitleBars,
				payloadReferences,
				payloadBufferReferences,
				passthroughCounts);
		}

		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			retainedTitleBars.Count,
			passthroughCounts.Sum(),
			CountAlive(payloadReferences),
			CountAlive(payloadBufferReferences),
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedTitleBars);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateRetainedTitleBar(
		bool clearPassthroughBeforeTemplateApply,
		int titleBarIndex,
		List<TitleBar> retainedTitleBars,
		List<WeakReference<TemplatePayload>> payloadReferences,
		List<WeakReference<byte[]>> payloadBufferReferences,
		List<int> passthroughCounts)
	{
		var titleBar = new TitleBar
		{
			Title = $"Workflow dashboard {titleBarIndex:000}",
			Subtitle = "Customer operations",
			BackgroundColor = Colors.Black,
			ForegroundColor = Colors.White
		};

		if (clearPassthroughBeforeTemplateApply)
			titleBar.PassthroughElements.Clear();

		for (var templateIndex = 0; templateIndex < RetiredTemplatesPerTitleBar; templateIndex++)
		{
			if (clearPassthroughBeforeTemplateApply)
				titleBar.PassthroughElements.Clear();

			var payload = new TemplatePayload(
				$"titlebar-{titleBarIndex:000}-template-{templateIndex:00}",
				new byte[PayloadBytes]);
			payload.Buffer[0] = (byte)(titleBarIndex + templateIndex);

			titleBar.ControlTemplate = CreatePayloadTemplate(payload, titleBarIndex, templateIndex);
			payloadReferences.Add(new WeakReference<TemplatePayload>(payload));
			payloadBufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));
			payload = null!;
		}

		if (clearPassthroughBeforeTemplateApply)
			titleBar.PassthroughElements.Clear();

		titleBar.ControlTemplate = CreateFinalTemplate(titleBarIndex);
		passthroughCounts.Add(titleBar.PassthroughElements.Count);
		retainedTitleBars.Add(titleBar);
		titleBar = null!;
	}

	static ControlTemplate CreatePayloadTemplate(TemplatePayload payload, int titleBarIndex, int templateIndex)
	{
		return new ControlTemplate(() =>
		{
			var grid = CreateTemplateRoot();
			var leading = CreatePassthroughSlot($"Tenant {titleBarIndex:000}", payload);
			var content = CreatePassthroughSlot($"Open incidents {templateIndex + 1}", payload);
			var trailing = CreatePassthroughSlot("Sync queue", payload);

			grid.Add(leading);
			grid.Add(content);
			grid.Add(trailing);
			grid.SetColumn(leading, 0);
			grid.SetColumn(content, 1);
			grid.SetColumn(trailing, 2);

			RegisterTitleBarNames(grid, leading, content, trailing);
			return grid;
		});
	}

	static ControlTemplate CreateFinalTemplate(int titleBarIndex)
	{
		return new ControlTemplate(() =>
		{
			var grid = CreateTemplateRoot();
			var leading = CreatePassthroughSlot($"Tenant {titleBarIndex:000}", null);
			var content = CreatePassthroughSlot("Stable dashboard", null);
			var trailing = CreatePassthroughSlot("Ready", null);

			grid.Add(leading);
			grid.Add(content);
			grid.Add(trailing);
			grid.SetColumn(leading, 0);
			grid.SetColumn(content, 1);
			grid.SetColumn(trailing, 2);

			RegisterTitleBarNames(grid, leading, content, trailing);
			return grid;
		});
	}

	static Grid CreateTemplateRoot()
	{
		return new Grid
		{
			ColumnDefinitions =
			{
				new ColumnDefinition(GridLength.Auto),
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(GridLength.Auto)
			}
		};
	}

	static ContentView CreatePassthroughSlot(string text, TemplatePayload? payload)
	{
		return new ContentView
		{
			BindingContext = payload,
			Content = new Label
			{
				Text = text,
				FontSize = 12,
				LineBreakMode = LineBreakMode.NoWrap,
				Margin = new Thickness(8, 0)
			}
		};
	}

	static void RegisterTitleBarNames(Grid root, IView leading, IView content, IView trailing)
	{
		INameScope nameScope = new NameScope();
		NameScope.SetNameScope(root, nameScope);
		nameScope.RegisterName(TitleBar.TemplateRootName, root);
		nameScope.RegisterName(TitleBar.TitleBarLeading, leading);
		nameScope.RegisterName(TitleBar.TitleBarContent, content);
		nameScope.RegisterName(TitleBar.TitleBarTrailing, trailing);
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

	sealed class TemplatePayload
	{
		public TemplatePayload(string name, byte[] buffer)
		{
			Name = name;
			Buffer = buffer;
		}

		public string Name { get; }

		public byte[] Buffer { get; }
	}

	public readonly record struct ScenarioResult(
		int RetainedTitleBars,
		int PassthroughElementCount,
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
			Control.PayloadsAlive == 0 &&
			Control.PayloadBuffersAlive == 0 &&
			Control.PassthroughElementCount == TitleBarCount * PassthroughSlotsPerTemplate &&
			Current.PayloadsAlive == TitleBarCount * RetiredTemplatesPerTitleBar &&
			Current.PayloadBuffersAlive == TitleBarCount * RetiredTemplatesPerTitleBar &&
			Current.PassthroughElementCount >= TitleBarCount * (RetiredTemplatesPerTitleBar + 1) * PassthroughSlotsPerTemplate;

		public string ToText()
		{
			var retiredPayloadCount = TitleBarCount * RetiredTemplatesPerTitleBar;
			var retainedBytes = Current.PayloadBuffersAlive * PayloadBytes;
			var builder = new StringBuilder();
			builder.AppendLine(LeakProved ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			builder.AppendLine("TitleBarPassthroughTemplateRetentionRepro");
			builder.AppendLine($"Live TitleBar owners retained in both scenarios: {TitleBarCount}");
			builder.AppendLine($"Retired payload-bearing ControlTemplates per TitleBar: {RetiredTemplatesPerTitleBar}");
			builder.AppendLine($"Passthrough slots per template: {PassthroughSlotsPerTemplate}");
			builder.AppendLine($"Retired title-bar template payloads created per run: {retiredPayloadCount}");
			builder.AppendLine($"Payload per retired title-bar template view model: {PayloadBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Run: control: clear TitleBar.PassthroughElements before each ControlTemplate replacement");
			AppendScenario(builder, Control);
			builder.AppendLine();
			builder.AppendLine("Run: current: replace ControlTemplate with MAUI passthrough list unchanged");
			AppendScenario(builder, Current);
			builder.AppendLine($"  retained retired template payload bytes: {retainedBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Leak path: live TitleBar -> PassthroughElements list -> retired named template ContentViews -> BindingContext payload buffers.");
			builder.AppendLine("Distinct from Window.TitleBar replacement cleanup: the TitleBar owner remains live, and repeated template replacement leaves old passthrough children in its public passthrough list.");
			builder.AppendLine($"dotnet-version: {Environment.Version}");
			return builder.ToString();
		}

		static void AppendScenario(StringBuilder builder, ScenarioResult result)
		{
			builder.AppendLine($"  live TitleBars retained by app/window cache: {result.RetainedTitleBars}");
			builder.AppendLine($"  total TitleBar.PassthroughElements entries: {result.PassthroughElementCount}");
			builder.AppendLine($"  retired template payloads alive after full GC: {result.PayloadsAlive}/{TitleBarCount * RetiredTemplatesPerTitleBar}");
			builder.AppendLine($"  retired payload buffers alive after full GC: {result.PayloadBuffersAlive}/{TitleBarCount * RetiredTemplatesPerTitleBar}");
			builder.AppendLine($"  managed heap delta: {result.HeapDelta / 1024d / 1024d:0.0} MiB");
		}
	}
}
