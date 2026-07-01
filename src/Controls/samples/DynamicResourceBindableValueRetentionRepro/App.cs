using System.Reflection;
using System.Text;
using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace DynamicResourceBindableValueRetentionRepro;

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
			Text = "Running DynamicResource bindable value retention repro...",
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
			var text = "DynamicResourceBindableValueRetentionRepro ERROR" + Environment.NewLine + ex;
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(1);
		}
	}
}

static class ReproSession
{
	public const string ResultsPath = "/tmp/dynamicresource-bindable-value-retention-results.txt";

	const int Iterations = 128;
	const int PayloadBytes = 1024 * 1024;
	const string ResourceKey = "active-payload";

	static readonly FieldInfo BindableResourcesField =
		typeof(Element).GetField("_bindableResources", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new InvalidOperationException("Could not find Element._bindableResources.");

	public static ReproReport Run()
	{
		var control = RunScenario(clearBindableResources: true);
		var current = RunScenario(clearBindableResources: false);

		return new ReproReport(control, current);
	}

	static ScenarioResult RunScenario(bool clearBindableResources)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
		var liveHosts = new List<DynamicResourceHost>(capacity: 1);
		var payloadReferences = new List<WeakReference<PayloadResource>>(Iterations);
		var payloadBufferReferences = new List<WeakReference<byte[]>>(Iterations);

		var host = new DynamicResourceHost();
		host.SetDynamicResource(DynamicResourceHost.PayloadProperty, ResourceKey);
		liveHosts.Add(host);

		for (var i = 0; i < Iterations; i++)
			ApplyPayloadUpdate(host, i, payloadReferences, payloadBufferReferences, clearBindableResources);

		host.Resources[ResourceKey] = "released";
		host.RemoveDynamicResource(DynamicResourceHost.PayloadProperty);
		host.Payload = null;

		if (clearBindableResources)
			ClearBindableResources(host);

		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);
		var retainedListCount = GetBindableResourcesCount(host);

		var result = new ScenarioResult(
			liveHosts.Count,
			retainedListCount,
			CountAlive(payloadReferences),
			CountAlive(payloadBufferReferences),
			heapBefore,
			heapAfter);

		GC.KeepAlive(liveHosts);
		return result;
	}

	static void ApplyPayloadUpdate(
		DynamicResourceHost host,
		int iteration,
		List<WeakReference<PayloadResource>> payloadReferences,
		List<WeakReference<byte[]>> payloadBufferReferences,
		bool clearBindableResources)
	{
		var payload = new PayloadResource($"theme-payload-{iteration}", CreateRealWorldPayload(iteration));
		host.Resources[ResourceKey] = payload;

		if (!ReferenceEquals(host.Payload, payload))
			throw new InvalidOperationException("DynamicResource did not update the host payload property.");

		payloadReferences.Add(new WeakReference<PayloadResource>(payload));
		payloadBufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));

		if (clearBindableResources)
			ClearBindableResources(host);
	}

	static byte[] CreateRealWorldPayload(int iteration)
	{
		var buffer = new byte[PayloadBytes];
		var prefix = Encoding.UTF8.GetBytes($"tenant-theme-snapshot:{iteration:D4}:localized-gradient-icon-font-audit-state;");
		for (var i = 0; i < buffer.Length; i++)
			buffer[i] = prefix[i % prefix.Length];

		return buffer;
	}

	static int GetBindableResourcesCount(Element element)
	{
		if (BindableResourcesField.GetValue(element) is System.Collections.ICollection collection)
			return collection.Count;

		return 0;
	}

	static void ClearBindableResources(Element element)
	{
		if (BindableResourcesField.GetValue(element) is System.Collections.IList list)
			list.Clear();

		BindableResourcesField.SetValue(element, null);
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

	sealed class DynamicResourceHost : ContentView
	{
		public static readonly BindableProperty PayloadProperty =
			BindableProperty.Create(nameof(Payload), typeof(object), typeof(DynamicResourceHost), null);

		public object? Payload
		{
			get => GetValue(PayloadProperty);
			set => SetValue(PayloadProperty, value);
		}
	}

	sealed class PayloadResource : BindableObject
	{
		public PayloadResource(string name, byte[] buffer)
		{
			Name = name;
			Buffer = buffer;
		}

		public string Name { get; }

		public byte[] Buffer { get; }
	}

	public readonly record struct ScenarioResult(
		int LiveHosts,
		int BindableResourcesListCount,
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
			Control.BindableResourcesListCount == 0 &&
			Current.PayloadsAlive == Iterations &&
			Current.PayloadBuffersAlive == Iterations &&
			Current.BindableResourcesListCount == Iterations;

		public string ToText()
		{
			var retainedBytes = Current.PayloadBuffersAlive * PayloadBytes;
			var builder = new StringBuilder();
			builder.AppendLine(LeakProved ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			builder.AppendLine("DynamicResourceBindableValueRetentionRepro");
			builder.AppendLine($"Dynamic resource updates: {Iterations}");
			builder.AppendLine($"Live host elements per run: 1");
			builder.AppendLine($"Payload per resource value: {PayloadBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Run: control: live host after clearing Element._bindableResources");
			AppendScenario(builder, Control);
			builder.AppendLine();
			builder.AppendLine("Run: current: live host with Element._bindableResources intact");
			AppendScenario(builder, Current);
			builder.AppendLine($"  retained payload bytes: {retainedBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Leak path: live Element -> Element._bindableResources -> old DynamicResource BindableObject values -> payload buffers");
			builder.AppendLine("The final dynamic resource and target property are cleared before GC; only the owner-side private list can explain retained old values.");
			builder.AppendLine($"dotnet-version: {Environment.Version}");
			return builder.ToString();
		}

		static void AppendScenario(StringBuilder builder, ScenarioResult result)
		{
			builder.AppendLine($"  live hosts kept alive through measurement: {result.LiveHosts}");
			builder.AppendLine($"  Element._bindableResources entries: {result.BindableResourcesListCount}/{Iterations}");
			builder.AppendLine($"  payload resource objects alive after full GC: {result.PayloadsAlive}/{Iterations}");
			builder.AppendLine($"  payload buffers alive after full GC: {result.PayloadBuffersAlive}/{Iterations}");
			builder.AppendLine($"  managed heap delta: {result.HeapDelta / 1024d / 1024d:0.0} MiB");
		}
	}
}
