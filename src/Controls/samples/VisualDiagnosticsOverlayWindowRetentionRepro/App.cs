using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui.Controls;

namespace VisualDiagnosticsOverlayWindowRetentionRepro;

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
			Text = "Running visual diagnostics overlay window retention repro...",
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
			var text = "VisualDiagnosticsOverlayWindowRetentionRepro ERROR" + Environment.NewLine + ex;
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(1);
		}
	}
}

static partial class ReproSession
{
	public const string ResultsPath = "/tmp/visualdiagnosticsoverlay-window-retention-results.txt";

	public const int OwnerCount = 160;
	const int PayloadBytes = 1024 * 1024;

	static readonly FieldInfo s_windowOverlayWindowField =
		typeof(WindowOverlay).GetField("<Window>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(WindowOverlay).FullName, "<Window>k__BackingField");

	public static ReproReport Run()
	{
		var control = RunScenario(clearOverlayWindow: true);
		var current = RunScenario(clearOverlayWindow: false);

		return new ReproReport(control, current);
	}

	static ScenarioResult RunScenario(bool clearOverlayWindow)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
		var retainedOverlayHandles = new List<object>(OwnerCount);
		var windowReferences = new List<WeakReference<Window>>(OwnerCount);
		var pageReferences = new List<WeakReference<ContentPage>>(OwnerCount);
		var payloadReferences = new List<WeakReference<WindowPayload>>(OwnerCount);
		var payloadBufferReferences = new List<WeakReference<byte[]>>(OwnerCount);

		for (var ownerIndex = 0; ownerIndex < OwnerCount; ownerIndex++)
		{
			CreateWindowAndRetainOverlayHandle(
				clearOverlayWindow,
				ownerIndex,
				retainedOverlayHandles,
				windowReferences,
				pageReferences,
				payloadReferences,
				payloadBufferReferences);
		}

		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			retainedOverlayHandles.Count,
			CountAlive(windowReferences),
			CountAlive(pageReferences),
			CountAlive(payloadReferences),
			CountAlive(payloadBufferReferences),
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedOverlayHandles);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateWindowAndRetainOverlayHandle(
		bool clearOverlayWindow,
		int ownerIndex,
		List<object> retainedOverlayHandles,
		List<WeakReference<Window>> windowReferences,
		List<WeakReference<ContentPage>> pageReferences,
		List<WeakReference<WindowPayload>> payloadReferences,
		List<WeakReference<byte[]>> payloadBufferReferences)
	{
		var payload = new WindowPayload(
			$"support-dashboard-window-{ownerIndex:000}",
			$"Support dashboard {ownerIndex:000} with cached queue rows, charts, filters, and session metadata",
			new byte[PayloadBytes]);
		payload.Buffer[0] = (byte)ownerIndex;
		payload.Buffer[^1] = (byte)(255 - ownerIndex);

		var page = new ContentPage
		{
			Title = $"Support dashboard {ownerIndex:000}",
			BindingContext = payload,
			Content = new VerticalStackLayout
			{
				Padding = 16,
				Spacing = 8,
				Children =
				{
					new Label { Text = payload.DisplayName, FontSize = 18 },
					new Label { Text = payload.Description, FontSize = 12 },
					new Button { Text = $"Open case batch {ownerIndex:000}" }
				}
			}
		};
		var window = new Window(page)
		{
			Title = $"Diagnostics target {ownerIndex:000}"
		};

		var overlay = window.VisualDiagnosticsOverlay;
		overlay.EnableElementSelector = ownerIndex % 2 == 0;
		overlay.ScrollToElement = ownerIndex % 3 == 0;

		if (clearOverlayWindow)
			s_windowOverlayWindowField.SetValue(overlay, null);

		retainedOverlayHandles.Add(overlay);
		windowReferences.Add(new WeakReference<Window>(window));
		pageReferences.Add(new WeakReference<ContentPage>(page));
		payloadReferences.Add(new WeakReference<WindowPayload>(payload));
		payloadBufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));

		window = null!;
		page = null!;
		payload = null!;
		overlay = null!;
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
}

sealed class WindowPayload
{
	public WindowPayload(string name, string description, byte[] buffer)
	{
		Name = name;
		Description = description;
		Buffer = buffer;
	}

	public string Name { get; }
	public string Description { get; }
	public byte[] Buffer { get; }
	public string DisplayName => $"{Name} ({Buffer.Length / (1024 * 1024)} MiB payload)";
}

readonly record struct ScenarioResult(
	int RetainedOverlayHandles,
	int WindowsAlive,
	int PagesAlive,
	int PayloadsAlive,
	int PayloadBuffersAlive,
	long HeapBefore,
	long HeapAfter)
{
	public long HeapDelta => HeapAfter - HeapBefore;
	public long RetainedPayloadBytes => (long)PayloadBuffersAlive * 1024 * 1024;
}

readonly record struct ReproReport(ScenarioResult Control, ScenarioResult Current)
{
	public bool LeakProved =>
		Control.WindowsAlive == 0 &&
		Control.PayloadBuffersAlive == 0 &&
		Current.WindowsAlive == ReproSession.OwnerCount &&
		Current.PayloadBuffersAlive == ReproSession.OwnerCount;

	public string ToText()
	{
		var builder = new StringBuilder();
		builder.AppendLine(LeakProved ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
		builder.AppendLine("VisualDiagnosticsOverlayWindowRetentionRepro");
		builder.AppendLine($"Diagnostics overlay handles retained in both scenarios: {Current.RetainedOverlayHandles}");
		builder.AppendLine("Retained handle type: IVisualDiagnosticsOverlay returned by Window.VisualDiagnosticsOverlay");
		builder.AppendLine("Payload per discarded window page: 1.0 MiB");
		builder.AppendLine();
		AppendScenario(builder, "control: clear WindowOverlay.Window backing field after reading VisualDiagnosticsOverlay", Control);
		builder.AppendLine();
		AppendScenario(builder, "current: retain public VisualDiagnosticsOverlay handles", Current);
		builder.AppendLine();
		builder.AppendLine("Leak path: diagnostics/helper cache -> IVisualDiagnosticsOverlay -> WindowOverlay.Window -> discarded Window -> Page -> BindingContext/Payload buffer.");
		builder.AppendLine($"dotnet-version: {Environment.Version}");
		return builder.ToString();
	}

	static void AppendScenario(StringBuilder builder, string title, ScenarioResult result)
	{
		builder.AppendLine($"Run: {title}");
		builder.AppendLine($"  retained overlay handles: {result.RetainedOverlayHandles}");
		builder.AppendLine($"  discarded windows alive after full GC: {result.WindowsAlive}/{ReproSession.OwnerCount}");
		builder.AppendLine($"  discarded pages alive after full GC: {result.PagesAlive}/{ReproSession.OwnerCount}");
		builder.AppendLine($"  page payloads alive after full GC: {result.PayloadsAlive}/{ReproSession.OwnerCount}");
		builder.AppendLine($"  page payload buffers alive after full GC: {result.PayloadBuffersAlive}/{ReproSession.OwnerCount}");
		builder.AppendLine($"  retained page payload bytes: {FormatBytes(result.RetainedPayloadBytes)}");
		builder.AppendLine($"  managed heap delta: {FormatBytes(result.HeapDelta)}");
	}

	static string FormatBytes(long bytes)
	{
		var mib = bytes / 1024d / 1024d;
		return $"{mib:0.0} MiB";
	}
}
