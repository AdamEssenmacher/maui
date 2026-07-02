using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Xaml;

namespace XamlControlTemplateRootRetentionRepro;

public sealed class MainPage : ContentPage
{
	const int PageCount = 80;
	const int PayloadBytes = 1024 * 1024;
	const int SurvivorTolerance = 2;

	const string RuntimeXaml = """
		<ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
		             xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml">
		  <ContentPage.Resources>
		    <ControlTemplate x:Key="EscapedTemplate">
		      <Grid Padding="4">
		        <Label Text="Runtime XAML ControlTemplate content" />
		      </Grid>
		    </ControlTemplate>
		  </ContentPage.Resources>
		</ContentPage>
		""";

	readonly string _resultsPath;
	readonly Label _status;
	bool _started;

	public MainPage(string resultsPath)
	{
		_resultsPath = resultsPath;
		Title = "XAML ControlTemplate Root Retention";

		_status = new Label
		{
			Text = "Running XAML ControlTemplate root-retention repro...",
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
				? "PROVEN: escaped XAML ControlTemplates retained discarded page roots."
				: "NOT PROVEN: discarded page roots did not remain alive.";

			File.WriteAllText(_resultsPath, report);
			await Task.Delay(250);
			Environment.Exit(result.Proven ? 0 : 2);
		}
		catch (Exception ex)
		{
			var report = "XAML ControlTemplate root-retention repro failed." + Environment.NewLine + ex;
			_status.Text = report;
			File.WriteAllText(_resultsPath, report);
			await Task.Delay(250);
			Environment.Exit(3);
		}
	}

	static async Task<ReproResult> RunReproAsync()
	{
		var runtimeControl = await RunScenarioAsync(TemplateKind.RuntimeXaml, resetLoadTemplate: true);
		var runtimeCurrent = await RunScenarioAsync(TemplateKind.RuntimeXaml, resetLoadTemplate: false);
		var compiledControl = await RunScenarioAsync(TemplateKind.CompiledXaml, resetLoadTemplate: true);
		var compiledCurrent = await RunScenarioAsync(TemplateKind.CompiledXaml, resetLoadTemplate: false);

		return new ReproResult(
			runtimeControl,
			runtimeCurrent,
			compiledControl,
			compiledCurrent,
			IsCollected(runtimeControl)
				&& IsRetained(runtimeCurrent)
				&& IsCollected(compiledControl)
				&& IsRetained(compiledCurrent));
	}

	static bool IsCollected(ScenarioResult result)
		=> result.PageSurvivors <= SurvivorTolerance
			&& result.PayloadSurvivors <= SurvivorTolerance
			&& result.PayloadBufferSurvivors <= SurvivorTolerance;

	static bool IsRetained(ScenarioResult result)
		=> result.PageSurvivors >= PageCount - SurvivorTolerance
			&& result.PayloadSurvivors >= PageCount - SurvivorTolerance
			&& result.PayloadBufferSurvivors >= PageCount - SurvivorTolerance;

	static async Task<ScenarioResult> RunScenarioAsync(TemplateKind kind, bool resetLoadTemplate)
	{
		var pageRefs = new List<WeakReference<ContentPage>>(PageCount);
		var payloadRefs = new List<WeakReference<Payload>>(PageCount);
		var payloadBufferRefs = new List<WeakReference<byte[]>>(PageCount);
		var templates = new List<ControlTemplate>(PageCount);

		ForceFullGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		for (var i = 0; i < PageCount; i++)
		{
			CreateAndDropPage(kind, i, resetLoadTemplate, templates, pageRefs, payloadRefs, payloadBufferRefs);

			if (i % 10 == 0)
				await Task.Yield();
		}

		await WaitForCollectionAsync();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			$"{kind}: {(resetLoadTemplate ? "Control: reset LoadTemplate" : "Current MAUI behavior")}",
			kind,
			resetLoadTemplate,
			templates.Count,
			CountAlive(pageRefs),
			CountAlive(payloadRefs),
			CountAlive(payloadBufferRefs),
			heapBefore,
			heapAfter);

		GC.KeepAlive(templates);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateAndDropPage(
		TemplateKind kind,
		int index,
		bool resetLoadTemplate,
		List<ControlTemplate> templates,
		List<WeakReference<ContentPage>> pageRefs,
		List<WeakReference<Payload>> payloadRefs,
		List<WeakReference<byte[]>> payloadBufferRefs)
	{
		var payload = new Payload(index, PayloadBytes);
		var page = CreatePage(kind, index);
		page.BindingContext = payload;

		if (page.Resources["EscapedTemplate"] is not ControlTemplate template)
			throw new InvalidOperationException($"{kind} page did not contain the expected ControlTemplate resource.");

		var createdContent = template.CreateContent();
		if (createdContent is not Grid)
			throw new InvalidOperationException($"Expected the template to create a Grid, got {createdContent.GetType().FullName}.");

		templates.Add(template);
		pageRefs.Add(new WeakReference<ContentPage>(page));
		payloadRefs.Add(new WeakReference<Payload>(payload));
		payloadBufferRefs.Add(new WeakReference<byte[]>(payload.Buffer));

		page.Resources.Clear();

		if (resetLoadTemplate)
			template.LoadTemplate = static () => new Grid { Children = { new Label { Text = "Control template content" } } };
	}

	static ContentPage CreatePage(TemplateKind kind, int index)
	{
		if (kind == TemplateKind.CompiledXaml)
			return new CompiledControlTemplatePage { Title = $"Compiled XAML page {index}" };

		var page = new ContentPage { Title = $"Runtime XAML page {index}" };
		page.LoadFromXaml(RuntimeXaml);
		return page;
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

	enum TemplateKind
	{
		RuntimeXaml,
		CompiledXaml
	}

	sealed class Payload
	{
		public Payload(int index, int byteCount)
		{
			Index = index;
			Buffer = new byte[byteCount];
			Buffer[0] = (byte)(index % 251);
			Buffer[^1] = (byte)((index + 17) % 251);
		}

		public int Index { get; }
		public byte[] Buffer { get; }
	}

	readonly record struct ScenarioResult(
		string Name,
		TemplateKind Kind,
		bool ResetLoadTemplate,
		int RetainedTemplates,
		int PageSurvivors,
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
			builder.AppendLine($"  Retained templates: {RetainedTemplates}");
			builder.AppendLine($"  Reset LoadTemplate: {ResetLoadTemplate}");
			builder.AppendLine($"  Page survivors: {PageSurvivors}/{PageCount}");
			builder.AppendLine($"  Payload survivors: {PayloadSurvivors}/{PageCount}");
			builder.AppendLine($"  Payload buffer survivors: {PayloadBufferSurvivors}/{PageCount}");
			builder.AppendLine($"  Retained payload estimate: {RetainedPayloadMiB:F1} MiB");
			builder.AppendLine($"  Managed heap before: {HeapBeforeBytes:N0} bytes");
			builder.AppendLine($"  Managed heap after: {HeapAfterBytes:N0} bytes");
			builder.AppendLine($"  Managed heap delta: {HeapDeltaBytes:N0} bytes");
		}
	}

	readonly record struct ReproResult(
		ScenarioResult RuntimeControl,
		ScenarioResult RuntimeCurrent,
		ScenarioResult CompiledControl,
		ScenarioResult CompiledCurrent,
		bool Proven)
	{
		public string ToReport()
		{
			var builder = new StringBuilder();
			builder.AppendLine("XAML ControlTemplate root-retention repro");
			builder.AppendLine($"Result: {(Proven ? "PROVEN" : "NOT PROVEN")}");
			builder.AppendLine();
			RuntimeControl.AppendTo(builder);
			builder.AppendLine();
			RuntimeCurrent.AppendTo(builder);
			builder.AppendLine();
			CompiledControl.AppendTo(builder);
			builder.AppendLine();
			CompiledCurrent.AppendTo(builder);
			builder.AppendLine();
			builder.AppendLine("Expected proof signal:");
			builder.AppendLine($"- Control survivors <= {SurvivorTolerance} after replacing ControlTemplate.LoadTemplate with a non-capturing factory.");
			builder.AppendLine($"- Current behavior survivors >= {PageCount - SurvivorTolerance} while only escaped ControlTemplate instances remain rooted.");
			builder.AppendLine();
			builder.AppendLine("Retained graphs under current behavior:");
			builder.AppendLine("Runtime XAML: escaped ControlTemplate -> LoadTemplate closure -> outer HydrationContext -> RootElement ContentPage -> BindingContext payload");
			builder.AppendLine("Compiled XAML: escaped ControlTemplate -> XamlC-generated LoadTemplate target -> root field -> CompiledControlTemplatePage -> BindingContext payload");
			return builder.ToString();
		}
	}
}
