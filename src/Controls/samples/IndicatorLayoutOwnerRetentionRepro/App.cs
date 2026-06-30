using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;

namespace IndicatorLayoutOwnerRetentionRepro;

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
			Text = "Running IndicatorLayout owner retention repro...",
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
			var text = "IndicatorLayoutOwnerRetentionRepro ERROR" + Environment.NewLine + ex;
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(1);
		}
	}
}

static class ReproSession
{
	public const string ResultsPath = "/tmp/indicatorlayout-owner-retention-results.txt";

	const int Iterations = 160;
	const int ItemsPerIndicator = 8;
	const int PayloadBytes = 1024 * 1024;

	public static ReproReport Run()
	{
		var control = RunScenario(clearOwnerReference: true);
		var current = RunScenario(clearOwnerReference: false);

		return new ReproReport(control, current);
	}

	static ScenarioResult RunScenario(bool clearOwnerReference)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
		var retainedLayouts = new List<object>(Iterations);
		var ownerReferences = new List<WeakReference<IndicatorView>>(Iterations);
		var payloadReferences = new List<WeakReference<IndicatorOwnerPayload>>(Iterations);
		var payloadBufferReferences = new List<WeakReference<byte[]>>(Iterations);

		for (var i = 0; i < Iterations; i++)
			CreateRetainedIndicatorLayout(i, clearOwnerReference, retainedLayouts, ownerReferences, payloadReferences, payloadBufferReferences);

		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			CountAlive(ownerReferences),
			CountAlive(payloadReferences),
			CountAlive(payloadBufferReferences),
			retainedLayouts.Count,
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedLayouts);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateRetainedIndicatorLayout(
		int iteration,
		bool clearOwnerReference,
		List<object> retainedLayouts,
		List<WeakReference<IndicatorView>> ownerReferences,
		List<WeakReference<IndicatorOwnerPayload>> payloadReferences,
		List<WeakReference<byte[]>> payloadBufferReferences)
	{
		var payload = new IndicatorOwnerPayload($"story-indicator-{iteration}", new byte[PayloadBytes]);
		payload.Buffer[0] = (byte)iteration;

		var owner = new IndicatorView
		{
			AutomationId = $"story-indicator-{iteration}",
			BindingContext = payload,
			MaximumVisible = ItemsPerIndicator,
			Count = ItemsPerIndicator,
			IndicatorSize = 10,
			IndicatorColor = Color.FromArgb("#B7C7BD"),
			SelectedIndicatorColor = Color.FromArgb("#146C5A"),
			IndicatorTemplate = CreateIndicatorTemplate()
		};

		var layout = owner.IndicatorLayout
			?? throw new InvalidOperationException("IndicatorView.IndicatorLayout was not created for a non-null IndicatorTemplate.");

		_ = layout.Children.Count;

		if (clearOwnerReference)
			ClearIndicatorViewReference(layout);

		retainedLayouts.Add(layout);
		ownerReferences.Add(new WeakReference<IndicatorView>(owner));
		payloadReferences.Add(new WeakReference<IndicatorOwnerPayload>(payload));
		payloadBufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));

		owner = null!;
		payload = null!;
		layout = null!;
	}

	static DataTemplate CreateIndicatorTemplate()
	{
		return new DataTemplate(() =>
		{
			return new Border
			{
				WidthRequest = 40,
				HeightRequest = 24,
				StrokeThickness = 0,
				BackgroundColor = Color.FromArgb("#24313A"),
				StrokeShape = new RoundRectangle { CornerRadius = 12 },
				Content = new Label
				{
					Text = "ST",
					FontSize = 10,
					FontAttributes = FontAttributes.Bold,
					TextColor = Colors.White,
					HorizontalTextAlignment = TextAlignment.Center,
					VerticalTextAlignment = TextAlignment.Center
				}
			};
		});
	}

	static void ClearIndicatorViewReference(object indicatorLayout)
	{
		var type = indicatorLayout.GetType();
		while (type is not null)
		{
			var field = type.GetField("_indicatorView", BindingFlags.Instance | BindingFlags.NonPublic);
			if (field is not null)
			{
				field.SetValue(indicatorLayout, null);
				return;
			}

			type = type.BaseType;
		}

		throw new InvalidOperationException("Could not find IndicatorStackLayout._indicatorView.");
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

	sealed class IndicatorOwnerPayload
	{
		public IndicatorOwnerPayload(string name, byte[] buffer)
		{
			Name = name;
			Buffer = buffer;
		}

		public string Name { get; }

		public byte[] Buffer { get; }
	}

	public readonly record struct ScenarioResult(
		int OwnersAlive,
		int PayloadsAlive,
		int PayloadBuffersAlive,
		int RetainedLayouts,
		long HeapBefore,
		long HeapAfter)
	{
		public long HeapDelta => HeapAfter - HeapBefore;
	}

	public readonly record struct ReproReport(ScenarioResult Control, ScenarioResult Current)
	{
		public bool LeakProved =>
			Control.OwnersAlive == 0 &&
			Control.PayloadsAlive == 0 &&
			Control.PayloadBuffersAlive == 0 &&
			Current.OwnersAlive == Iterations &&
			Current.PayloadsAlive == Iterations &&
			Current.PayloadBuffersAlive == Iterations;

		public string ToText()
		{
			var retainedBytes = Current.PayloadBuffersAlive * PayloadBytes;
			var builder = new StringBuilder();
			builder.AppendLine(LeakProved ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			builder.AppendLine("IndicatorLayoutOwnerRetentionRepro");
			builder.AppendLine($"IndicatorView owners created: {Iterations}");
			builder.AppendLine($"Indicator items per owner: {ItemsPerIndicator}");
			builder.AppendLine($"Retained IndicatorLayout handles per run: {Iterations}");
			builder.AppendLine($"Payload per discarded IndicatorView: {PayloadBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Run: control: retained IndicatorLayout handles after clearing IndicatorStackLayout._indicatorView");
			AppendScenario(builder, Control);
			builder.AppendLine();
			builder.AppendLine("Run: current: retained IndicatorLayout handles with MAUI owner reference intact");
			AppendScenario(builder, Current);
			builder.AppendLine($"  retained payload bytes: {retainedBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Leak path: app IndicatorLayout cache -> IndicatorStackLayout._indicatorView -> discarded IndicatorView -> BindingContext payload");
			builder.AppendLine("Distinct from IndicatorView template-swap/native-child leaks: the retained object is the public IndicatorLayout handle after the owner is discarded.");
			builder.AppendLine($"dotnet-version: {Environment.Version}");
			return builder.ToString();
		}

		static void AppendScenario(StringBuilder builder, ScenarioResult result)
		{
			builder.AppendLine($"  IndicatorLayout handles retained by app cache: {result.RetainedLayouts}");
			builder.AppendLine($"  IndicatorView owners alive after full GC: {result.OwnersAlive}/{Iterations}");
			builder.AppendLine($"  owner payloads alive after full GC: {result.PayloadsAlive}/{Iterations}");
			builder.AppendLine($"  owner payload buffers alive after full GC: {result.PayloadBuffersAlive}/{Iterations}");
			builder.AppendLine($"  managed heap delta: {result.HeapDelta / 1024d / 1024d:0.0} MiB");
		}
	}
}
