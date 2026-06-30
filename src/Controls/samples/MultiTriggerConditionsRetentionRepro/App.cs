using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui.Controls;

namespace MultiTriggerConditionsRetentionRepro;

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
			Text = "Running MultiTrigger Conditions retention repro...",
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
			var text = "MultiTriggerConditionsRetentionRepro ERROR" + Environment.NewLine + ex;
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(1);
		}
	}
}

static class ReproSession
{
	public const string ResultsPath = "/tmp/multitrigger-conditions-retention-results.txt";

	public const int TriggerCount = 160;
	const int PayloadBytes = 1024 * 1024;

	static readonly FieldInfo s_conditionChangedField =
		typeof(Condition).GetField("_conditionChanged", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(Condition).FullName, "_conditionChanged");

	public static ReproReport Run()
	{
		var control = RunScenario(clearChildConditionCallback: true);
		var current = RunScenario(clearChildConditionCallback: false);

		return new ReproReport(control, current);
	}

	static ScenarioResult RunScenario(bool clearChildConditionCallback)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
		var retainedConditionHandles = new List<IList<Condition>>(TriggerCount);
		var triggerReferences = new List<WeakReference<MultiTrigger>>(TriggerCount);
		var payloadReferences = new List<WeakReference<TriggerPayload>>(TriggerCount);
		var payloadBufferReferences = new List<WeakReference<byte[]>>(TriggerCount);
		var pageReferences = new List<WeakReference<ContentPage>>(TriggerCount);
		var retainedConditionCounts = new List<int>(TriggerCount);

		for (var triggerIndex = 0; triggerIndex < TriggerCount; triggerIndex++)
		{
			CreateAndDiscardTrigger(
				clearChildConditionCallback,
				triggerIndex,
				retainedConditionHandles,
				triggerReferences,
				payloadReferences,
				payloadBufferReferences,
				pageReferences,
				retainedConditionCounts);
		}

		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			retainedConditionHandles.Count,
			Sum(retainedConditionCounts),
			CountAlive(triggerReferences),
			CountAlive(payloadReferences),
			CountAlive(payloadBufferReferences),
			CountAlive(pageReferences),
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedConditionHandles);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateAndDiscardTrigger(
		bool clearChildConditionCallback,
		int triggerIndex,
		List<IList<Condition>> retainedConditionHandles,
		List<WeakReference<MultiTrigger>> triggerReferences,
		List<WeakReference<TriggerPayload>> payloadReferences,
		List<WeakReference<byte[]>> payloadBufferReferences,
		List<WeakReference<ContentPage>> pageReferences,
		List<int> retainedConditionCounts)
	{
		var payload = new TriggerPayload(
			$"trigger-{triggerIndex:000}",
			$"Responsive card rule set for tenant workspace {triggerIndex:000}; includes feature flags, pricing rules, and personalization state.",
			new byte[PayloadBytes]);
		payload.Buffer[0] = (byte)triggerIndex;
		payload.Buffer[^1] = (byte)(255 - triggerIndex);

		var trigger = new MultiTrigger(typeof(ContentPage))
		{
			BindingContext = payload
		};
		trigger.Conditions.Add(new PropertyCondition
		{
			Property = VisualElement.IsVisibleProperty,
			Value = true
		});

		var conditions = trigger.Conditions;
		var page = new ContentPage
		{
			Title = $"Transient trigger page {triggerIndex:000}",
			Content = new Label { Text = "Trigger target" }
		};

		page.Triggers.Add(trigger);
		page.Triggers.Remove(trigger);

		if (clearChildConditionCallback)
			ClearChildConditionCallbacks(conditions);

		retainedConditionCounts.Add(conditions.Count);
		retainedConditionHandles.Add(conditions);
		triggerReferences.Add(new WeakReference<MultiTrigger>(trigger));
		payloadReferences.Add(new WeakReference<TriggerPayload>(payload));
		payloadBufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));
		pageReferences.Add(new WeakReference<ContentPage>(page));

		trigger = null!;
		conditions = null!;
		page = null!;
		payload = null!;
	}

	static void ClearChildConditionCallbacks(IEnumerable<Condition> conditions)
	{
		foreach (var condition in conditions)
			s_conditionChangedField.SetValue(condition, null);
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

	static int Sum(IEnumerable<int> values)
	{
		var result = 0;
		foreach (var value in values)
			result += value;

		return result;
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

sealed class TriggerPayload
{
	public TriggerPayload(string id, string description, byte[] buffer)
	{
		Id = id;
		Description = description;
		Buffer = buffer;
	}

	public string Id { get; }
	public string Description { get; }
	public byte[] Buffer { get; }
}

readonly record struct ScenarioResult(
	int RetainedConditionHandles,
	int RetainedConditionCount,
	int TriggersAlive,
	int PayloadsAlive,
	int PayloadBuffersAlive,
	int PagesAlive,
	long HeapBefore,
	long HeapAfter)
{
	public long HeapDelta => HeapAfter - HeapBefore;
	public long RetainedPayloadBytes => (long)PayloadBuffersAlive * 1024 * 1024;
}

readonly record struct ReproReport(ScenarioResult Control, ScenarioResult Current)
{
	public bool LeakProved =>
		Control.TriggersAlive == 0 &&
		Control.PayloadBuffersAlive == 0 &&
		Current.RetainedConditionCount == ReproSession.TriggerCount &&
		Current.TriggersAlive == ReproSession.TriggerCount &&
		Current.PayloadBuffersAlive == ReproSession.TriggerCount;

	public string ToText()
	{
		var builder = new StringBuilder();
		builder.AppendLine(LeakProved ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
		builder.AppendLine("MultiTriggerConditionsRetentionRepro");
		builder.AppendLine($"MultiTrigger.Conditions handles retained in both scenarios: {Current.RetainedConditionHandles}");
		builder.AppendLine("Payload per discarded MultiTrigger: 1.0 MiB");
		builder.AppendLine();
		AppendScenario(builder, "control: clear child Condition.ConditionChanged after detach", Control);
		builder.AppendLine();
		AppendScenario(builder, "current: retain public Conditions handle after detach", Current);
		builder.AppendLine();
		builder.AppendLine("Leak path: app/helper retained MultiTrigger.Conditions -> child Condition._conditionChanged -> MultiCondition -> Condition.ConditionChanged -> discarded MultiTrigger -> BindingContext/Payload buffer.");
		builder.AppendLine("The target ContentPage collects in both scenarios; this isolates the public Conditions list handle retaining the discarded trigger owner.");
		builder.AppendLine($"dotnet-version: {Environment.Version}");
		return builder.ToString();
	}

	static void AppendScenario(StringBuilder builder, string title, ScenarioResult result)
	{
		builder.AppendLine($"Run: {title}");
		builder.AppendLine($"  retained Conditions handles: {result.RetainedConditionHandles}");
		builder.AppendLine($"  retained condition item count: {result.RetainedConditionCount}");
		builder.AppendLine($"  discarded MultiTriggers alive after full GC: {result.TriggersAlive}/{ReproSession.TriggerCount}");
		builder.AppendLine($"  trigger payloads alive after full GC: {result.PayloadsAlive}/{ReproSession.TriggerCount}");
		builder.AppendLine($"  trigger payload buffers alive after full GC: {result.PayloadBuffersAlive}/{ReproSession.TriggerCount}");
		builder.AppendLine($"  transient target pages alive after full GC: {result.PagesAlive}/{ReproSession.TriggerCount}");
		builder.AppendLine($"  retained payload bytes: {FormatBytes(result.RetainedPayloadBytes)}");
		builder.AppendLine($"  managed heap delta: {FormatBytes(result.HeapDelta)}");
	}

	static string FormatBytes(long bytes)
	{
		var mib = bytes / 1024d / 1024d;
		return $"{mib:0.0} MiB";
	}
}
