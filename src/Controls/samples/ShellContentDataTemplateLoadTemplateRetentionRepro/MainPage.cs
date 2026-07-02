using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace ShellContentDataTemplateLoadTemplateRetentionRepro;

public sealed class MainPage : ContentPage
{
	const int TemplateCount = 80;
	const int PayloadBytes = 1024 * 1024;
	const int SurvivorTolerance = 2;

	readonly string? _resultsPath;
	readonly Label _status;
	bool _started;

	public MainPage(string? resultsPath)
	{
		_resultsPath = resultsPath;
		Title = "ShellContent DataTemplate Retention";

		_status = new Label
		{
			Text = "Running ShellContent DataTemplate retention repro...",
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
			var mauiContext = Handler?.MauiContext ?? throw new InvalidOperationException("MainPage handler has no MauiContext.");
			var result = await RunReproAsync(mauiContext);
			var report = result.ToReport();

			_status.Text = result.Proven
				? "PROVEN: shared type-based DataTemplate retained discarded ShellContent graphs."
				: "NOT PROVEN: discarded ShellContent graphs did not remain alive.";

			if (!string.IsNullOrWhiteSpace(_resultsPath))
				File.WriteAllText(_resultsPath, report);

			await Task.Delay(250);
			Environment.Exit(result.Proven ? 0 : 2);
		}
		catch (Exception ex)
		{
			var report = "ShellContent DataTemplate LoadTemplate retention repro failed." + Environment.NewLine + ex;
			_status.Text = report;

			if (!string.IsNullOrWhiteSpace(_resultsPath))
				File.WriteAllText(_resultsPath, report);

			await Task.Delay(250);
			Environment.Exit(3);
		}
	}

	static async Task<ReproResult> RunReproAsync(IMauiContext mauiContext)
	{
		var control = await RunScenarioAsync(mauiContext, resetLoadTemplate: true);
		var current = await RunScenarioAsync(mauiContext, resetLoadTemplate: false);

		var controlCollected = control.ShellContentSurvivors <= SurvivorTolerance
			&& control.PayloadSurvivors <= SurvivorTolerance
			&& control.PayloadBufferSurvivors <= SurvivorTolerance;

		var currentRetained = current.ShellContentSurvivors >= TemplateCount - SurvivorTolerance
			&& current.PayloadSurvivors >= TemplateCount - SurvivorTolerance
			&& current.PayloadBufferSurvivors >= TemplateCount - SurvivorTolerance;

		return new ReproResult(control, current, controlCollected && currentRetained);
	}

	static async Task<ScenarioResult> RunScenarioAsync(IMauiContext mauiContext, bool resetLoadTemplate)
	{
		var shellContentRefs = new List<WeakReference<ShellContent>>(TemplateCount);
		var pageRefs = new List<WeakReference<PayloadPage>>(TemplateCount);
		var payloadRefs = new List<WeakReference<Payload>>(TemplateCount);
		var payloadBufferRefs = new List<WeakReference<byte[]>>(TemplateCount);
		var templates = new List<DataTemplate>(TemplateCount);

		ForceFullGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		for (var i = 0; i < TemplateCount; i++)
		{
			var template = new DataTemplate(typeof(PayloadPage));
			templates.Add(template);

			CreateAndDropShellContent(
				mauiContext,
				template,
				i,
				resetLoadTemplate,
				shellContentRefs,
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
			CountAlive(shellContentRefs),
			CountAlive(pageRefs),
			CountAlive(payloadRefs),
			CountAlive(payloadBufferRefs),
			heapBefore,
			heapAfter);

		GC.KeepAlive(templates);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateAndDropShellContent(
		IMauiContext mauiContext,
		DataTemplate template,
		int index,
		bool resetLoadTemplate,
		List<WeakReference<ShellContent>> shellContentRefs,
		List<WeakReference<PayloadPage>> pageRefs,
		List<WeakReference<Payload>> payloadRefs,
		List<WeakReference<byte[]>> payloadBufferRefs)
	{
		var payload = new Payload(index, PayloadBytes);
		var section = new ShellSection { Title = $"Section {index}" };
		var handler = new ContextOnlyHandler();
		handler.SetMauiContext(mauiContext);
		section.Handler = handler;

		var shellContent = new ShellContent
		{
			Title = $"Feature {index}",
			ContentTemplate = template,
			BindingContext = payload
		};

		section.Items.Add(shellContent);
		var page = ((IShellContentController)shellContent).GetOrCreateContent();
		if (page is not PayloadPage payloadPage)
			throw new InvalidOperationException($"Expected {nameof(PayloadPage)}, got {page.GetType().FullName}.");

		payloadPage.BindingContext = payload;

		shellContentRefs.Add(new WeakReference<ShellContent>(shellContent));
		pageRefs.Add(new WeakReference<PayloadPage>(payloadPage));
		payloadRefs.Add(new WeakReference<Payload>(payload));
		payloadBufferRefs.Add(new WeakReference<byte[]>(payload.Buffer));

		if (resetLoadTemplate)
			template.LoadTemplate = static () => new PayloadPage();
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

	sealed class ContextOnlyHandler : IElementHandler
	{
		public object? PlatformView => null;
		public IElement? VirtualView { get; private set; }
		public IMauiContext? MauiContext { get; private set; }

		public void SetMauiContext(IMauiContext mauiContext)
		{
			MauiContext = mauiContext;
		}

		public void SetVirtualView(IElement view)
		{
			VirtualView = view;
		}

		public void UpdateValue(string property)
		{
		}

		public void Invoke(string command, object? args = null)
		{
		}

		public void DisconnectHandler()
		{
			VirtualView = null;
			MauiContext = null;
		}
	}

	sealed class Payload
	{
		public Payload(int index, int byteCount)
		{
			Index = index;
			Buffer = new byte[byteCount];
			Buffer[0] = (byte)(index % byte.MaxValue);
			Buffer[^1] = (byte)((index + 1) % byte.MaxValue);
		}

		public int Index { get; }
		public byte[] Buffer { get; }
	}

	sealed class PayloadPage : ContentPage
	{
	}

	readonly record struct ScenarioResult(
		string Name,
		bool ResetLoadTemplate,
		int TemplatesKeptAlive,
		int ShellContentSurvivors,
		int PageSurvivors,
		int PayloadSurvivors,
		int PayloadBufferSurvivors,
		long HeapBefore,
		long HeapAfter)
	{
		public long RetainedPayloadBytes => (long)PayloadBufferSurvivors * PayloadBytes;
	}

	readonly record struct ReproResult(ScenarioResult Control, ScenarioResult Current, bool Proven)
	{
		public string ToReport()
		{
			var builder = new StringBuilder();
			builder.AppendLine("ShellContent DataTemplate LoadTemplate retention repro");
			builder.AppendLine($"Result: {(Proven ? "PROVEN" : "NOT PROVEN")}");
			builder.AppendLine($"Templates kept alive per scenario: {TemplateCount}");
			builder.AppendLine($"Payload per ShellContent: {PayloadBytes:N0} bytes");
			builder.AppendLine();
			AppendScenario(builder, Control);
			builder.AppendLine();
			AppendScenario(builder, Current);
			builder.AppendLine();
			builder.AppendLine("Expected proving signal:");
			builder.AppendLine($"- Control survivors <= {SurvivorTolerance} after resetting DataTemplate.LoadTemplate to a non-capturing factory.");
			builder.AppendLine($"- Current behavior survivors >= {TemplateCount - SurvivorTolerance} while only the shared DataTemplate instances remain rooted.");
			builder.AppendLine();
			builder.AppendLine("Retained graph under current behavior:");
			builder.AppendLine("Shared DataTemplate -> overwritten LoadTemplate closure -> ShellContent -> BindingContext payload and ContentCache page");
			return builder.ToString();
		}

		static void AppendScenario(StringBuilder builder, ScenarioResult scenario)
		{
			builder.AppendLine(scenario.Name);
			builder.AppendLine($"  Templates kept alive: {scenario.TemplatesKeptAlive}");
			builder.AppendLine($"  ShellContent survivors: {scenario.ShellContentSurvivors}/{TemplateCount}");
			builder.AppendLine($"  PayloadPage survivors: {scenario.PageSurvivors}/{TemplateCount}");
			builder.AppendLine($"  Payload survivors: {scenario.PayloadSurvivors}/{TemplateCount}");
			builder.AppendLine($"  Payload buffer survivors: {scenario.PayloadBufferSurvivors}/{TemplateCount}");
			builder.AppendLine($"  Retained payload estimate: {scenario.RetainedPayloadBytes / (1024 * 1024):N0} MiB");
			builder.AppendLine($"  Managed heap delta: {scenario.HeapAfter - scenario.HeapBefore:N0} bytes");
		}
	}
}
