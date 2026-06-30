using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui.Controls;
using iOSConfig = Microsoft.Maui.Controls.PlatformConfiguration.iOS;
using iOSPage = Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific.Page;
using LargeTitleDisplayMode = Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific.LargeTitleDisplayMode;
using StatusBarHiddenMode = Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific.StatusBarHiddenMode;

namespace PlatformConfigurationOwnerRetentionRepro;

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
			Text = "Running platform configuration owner retention repro...",
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
			var text = "PlatformConfigurationOwnerRetentionRepro ERROR" + Environment.NewLine + ex;
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(1);
		}
	}
}

static partial class ReproSession
{
	public const string ResultsPath = "/tmp/platformconfiguration-owner-retention-results.txt";

	public const int OwnerCount = 160;
	const int PayloadBytes = 1024 * 1024;

	static readonly FieldInfo s_configurationElementField =
		typeof(Configuration<iOSConfig, Page>).GetField("<Element>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(Configuration<iOSConfig, Page>).FullName, "<Element>k__BackingField");

	public static ReproReport Run()
	{
		var control = RunScenario(clearConfigurationElement: true);
		var current = RunScenario(clearConfigurationElement: false);

		return new ReproReport(control, current);
	}

	static ScenarioResult RunScenario(bool clearConfigurationElement)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
		var retainedConfigurationHandles = new List<object>(OwnerCount);
		var ownerReferences = new List<WeakReference<ContentPage>>(OwnerCount);
		var payloadReferences = new List<WeakReference<OwnerPayload>>(OwnerCount);
		var payloadBufferReferences = new List<WeakReference<byte[]>>(OwnerCount);

		for (var ownerIndex = 0; ownerIndex < OwnerCount; ownerIndex++)
		{
			CreateOwnerAndRetainConfigurationHandle(
				clearConfigurationElement,
				ownerIndex,
				retainedConfigurationHandles,
				ownerReferences,
				payloadReferences,
				payloadBufferReferences);
		}

		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			retainedConfigurationHandles.Count,
			CountAlive(ownerReferences),
			CountAlive(payloadReferences),
			CountAlive(payloadBufferReferences),
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedConfigurationHandles);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateOwnerAndRetainConfigurationHandle(
		bool clearConfigurationElement,
		int ownerIndex,
		List<object> retainedConfigurationHandles,
		List<WeakReference<ContentPage>> ownerReferences,
		List<WeakReference<OwnerPayload>> payloadReferences,
		List<WeakReference<byte[]>> payloadBufferReferences)
	{
		var payload = new OwnerPayload(
			$"tenant-dashboard-{ownerIndex:000}",
			$"Tenant {ownerIndex:000} field service dashboard with cached filters, rows, and summary cards",
			new byte[PayloadBytes]);
		payload.Buffer[0] = (byte)ownerIndex;
		payload.Buffer[^1] = (byte)(255 - ownerIndex);

		var owner = new ContentPage
		{
			Title = $"Regional dashboard {ownerIndex:000}",
			BindingContext = payload,
			Content = new VerticalStackLayout
			{
				Padding = 16,
				Spacing = 8,
				Children =
				{
					new Label { Text = payload.DisplayName, FontSize = 18 },
					new Label { Text = payload.Description, FontSize = 12 },
					new Button { Text = $"Open route batch {ownerIndex:000}" }
				}
			}
		};

		var config = owner.On<iOSConfig>();
		iOSPage.SetPrefersStatusBarHidden(config, StatusBarHiddenMode.False);
		iOSPage.SetLargeTitleDisplay(config, LargeTitleDisplayMode.Automatic);

		if (clearConfigurationElement)
			s_configurationElementField.SetValue(config, null);

		retainedConfigurationHandles.Add(config);
		ownerReferences.Add(new WeakReference<ContentPage>(owner));
		payloadReferences.Add(new WeakReference<OwnerPayload>(payload));
		payloadBufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));

		owner = null!;
		payload = null!;
		config = null!;
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

sealed class OwnerPayload
{
	public OwnerPayload(string name, string description, byte[] buffer)
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
	int RetainedConfigurationHandles,
	int OwnersAlive,
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
		Control.OwnersAlive == 0 &&
		Control.PayloadBuffersAlive == 0 &&
		Current.OwnersAlive == ReproSession.OwnerCount &&
		Current.PayloadBuffersAlive == ReproSession.OwnerCount;

	public string ToText()
	{
		var builder = new StringBuilder();
		builder.AppendLine(LeakProved ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
		builder.AppendLine("PlatformConfigurationOwnerRetentionRepro");
		builder.AppendLine($"Configuration handles retained in both scenarios: {Current.RetainedConfigurationHandles}");
		builder.AppendLine("Retained handle type: IPlatformElementConfiguration<iOS, Page> returned by Page.On<iOS>()");
		builder.AppendLine("Payload per discarded page owner: 1.0 MiB");
		builder.AppendLine();
		AppendScenario(builder, "control: clear Configuration<iOS, Page>.Element backing field after platform-specific setup", Control);
		builder.AppendLine();
		AppendScenario(builder, "current: retain public platform-specific configuration handles", Current);
		builder.AppendLine();
		builder.AppendLine("Leak path: app/helper cache -> IPlatformElementConfiguration<iOS, Page> -> Configuration.Element -> discarded Page -> BindingContext/Payload buffer.");
		builder.AppendLine($"dotnet-version: {Environment.Version}");
		return builder.ToString();
	}

	static void AppendScenario(StringBuilder builder, string title, ScenarioResult result)
	{
		builder.AppendLine($"Run: {title}");
		builder.AppendLine($"  retained configuration handles: {result.RetainedConfigurationHandles}");
		builder.AppendLine($"  discarded page owners alive after full GC: {result.OwnersAlive}/{ReproSession.OwnerCount}");
		builder.AppendLine($"  owner payloads alive after full GC: {result.PayloadsAlive}/{ReproSession.OwnerCount}");
		builder.AppendLine($"  owner payload buffers alive after full GC: {result.PayloadBuffersAlive}/{ReproSession.OwnerCount}");
		builder.AppendLine($"  retained owner payload bytes: {FormatBytes(result.RetainedPayloadBytes)}");
		builder.AppendLine($"  managed heap delta: {FormatBytes(result.HeapDelta)}");
	}

	static string FormatBytes(long bytes)
	{
		var mib = bytes / 1024d / 1024d;
		return $"{mib:0.0} MiB";
	}
}
