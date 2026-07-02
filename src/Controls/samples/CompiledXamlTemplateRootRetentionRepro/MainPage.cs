using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui.Controls;

namespace CompiledXamlTemplateRootRetentionRepro;

public sealed class MainPage : ContentPage
{
	const int PageCount = 80;
	const int PayloadBytes = 1024 * 1024;
	const int SurvivorTolerance = 2;

	readonly string? _resultsPath;
	readonly Label _status;
	bool _started;

	public MainPage(string? resultsPath)
	{
		_resultsPath = resultsPath;
		Title = "Compiled XAML Template Root Retention";

		_status = new Label
		{
			Text = "Running compiled XAML template root-retention repro...",
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
				? "PROVEN: escaped compiled-XAML DataTemplates retained discarded page roots."
				: "NOT PROVEN: discarded page roots did not remain alive.";

			if (!string.IsNullOrWhiteSpace(_resultsPath))
				File.WriteAllText(_resultsPath, report);

			await Task.Delay(250);
			Environment.Exit(result.Proven ? 0 : 2);
		}
		catch (Exception ex)
		{
			var report = "Compiled XAML DataTemplate root-retention repro failed." + Environment.NewLine + ex;
			_status.Text = report;

			if (!string.IsNullOrWhiteSpace(_resultsPath))
				File.WriteAllText(_resultsPath, report);

			await Task.Delay(250);
			Environment.Exit(3);
		}
	}

	static async Task<ReproResult> RunReproAsync()
	{
		var control = await RunScenarioAsync(resetLoadTemplate: true);
		var current = await RunScenarioAsync(resetLoadTemplate: false);

		var controlCollected = control.PageSurvivors <= SurvivorTolerance
			&& control.PayloadSurvivors <= SurvivorTolerance
			&& control.PayloadBufferSurvivors <= SurvivorTolerance;

		var currentRetained = current.PageSurvivors >= PageCount - SurvivorTolerance
			&& current.PayloadSurvivors >= PageCount - SurvivorTolerance
			&& current.PayloadBufferSurvivors >= PageCount - SurvivorTolerance;

		return new ReproResult(control, current, controlCollected && currentRetained);
	}

	static async Task<ScenarioResult> RunScenarioAsync(bool resetLoadTemplate)
	{
		var pageRefs = new List<WeakReference<CompiledTemplatePage>>(PageCount);
		var payloadRefs = new List<WeakReference<Payload>>(PageCount);
		var payloadBufferRefs = new List<WeakReference<byte[]>>(PageCount);
		var templates = new List<DataTemplate>(PageCount);

		ForceFullGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		for (var i = 0; i < PageCount; i++)
		{
			CreateAndDropPage(
				i,
				resetLoadTemplate,
				templates,
				pageRefs,
				payloadRefs,
				payloadBufferRefs);

			if (i % 10 == 0)
				await Task.Yield();
		}

		await WaitForCollectionAsync();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			resetLoadTemplate ? "Control: reset LoadTemplate" : "Current MAUI behavior",
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
		int index,
		bool resetLoadTemplate,
		List<DataTemplate> templates,
		List<WeakReference<CompiledTemplatePage>> pageRefs,
		List<WeakReference<Payload>> payloadRefs,
		List<WeakReference<byte[]>> payloadBufferRefs)
	{
		var payload = new Payload(index, PayloadBytes);
		var page = new CompiledTemplatePage
		{
			Title = $"Compiled XAML page {index}",
			BindingContext = payload
		};

		if (page.Resources["EscapedTemplate"] is not DataTemplate template)
			throw new InvalidOperationException("The compiled XAML page did not contain the expected DataTemplate resource.");

		var createdContent = template.CreateContent();
		if (createdContent is not Grid)
			throw new InvalidOperationException($"Expected the template to create a Grid, got {createdContent.GetType().FullName}.");

		templates.Add(template);
		pageRefs.Add(new WeakReference<CompiledTemplatePage>(page));
		payloadRefs.Add(new WeakReference<Payload>(payload));
		payloadBufferRefs.Add(new WeakReference<byte[]>(payload.Buffer));

		page.Resources.Clear();

		if (resetLoadTemplate)
			template.LoadTemplate = static () => new Grid { Children = { new Label { Text = "Control template content" } } };
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

	readonly record struct ReproResult(ScenarioResult Control, ScenarioResult Current, bool Proven)
	{
		public string ToReport()
		{
			var builder = new StringBuilder();
			builder.AppendLine("Compiled XAML DataTemplate root-retention repro");
			builder.AppendLine($"Result: {(Proven ? "PROVEN" : "NOT PROVEN")}");
			builder.AppendLine();
			Control.AppendTo(builder);
			builder.AppendLine();
			Current.AppendTo(builder);
			builder.AppendLine();
			builder.AppendLine("Expected proof signal:");
			builder.AppendLine($"- Control survivors <= {SurvivorTolerance} after replacing DataTemplate.LoadTemplate with a non-capturing factory.");
			builder.AppendLine($"- Current behavior survivors >= {PageCount - SurvivorTolerance} while only escaped DataTemplate instances remain rooted.");
			builder.AppendLine();
			builder.AppendLine("Retained graph under current behavior:");
			builder.AppendLine("Escaped DataTemplate -> XamlC-generated LoadTemplate target -> root field -> CompiledTemplatePage -> BindingContext payload");
			return builder.ToString();
		}
	}
}
