using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui.Controls;

namespace TriggerConditionPropertyContextRetentionRepro;

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
			Text = "Running Trigger condition property-context retention repro...",
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
			var text = "TriggerConditionPropertyContextRetentionRepro ERROR" + Environment.NewLine + ex;
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(1);
		}
	}
}

static class ReproSession
{
	public const string ResultsPath = "/tmp/trigger-condition-propertycontext-retention-results.txt";

	public const int TriggerCount = 160;
	const int PayloadBytes = 1024 * 1024;

	static readonly PropertyInfo s_conditionProperty =
		typeof(TriggerBase).GetProperty("Condition", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(typeof(TriggerBase).FullName, "Condition");

	static readonly FieldInfo s_statePropertyField =
		typeof(PropertyCondition).GetField("_stateProperty", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(PropertyCondition).FullName, "_stateProperty");

	static readonly FieldInfo s_propertiesField =
		typeof(BindableObject).GetField("_properties", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(BindableObject).FullName, "_properties");

	static readonly FieldInfo s_contextPropertyField =
		typeof(BindableObject).GetNestedType("BindablePropertyContext", BindingFlags.NonPublic)
			?.GetField("Property", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
		?? throw new MissingFieldException($"{typeof(BindableObject).FullName}.BindablePropertyContext", "Property");

	public static ReproReport Run()
	{
		var control = RunScenario(removeConditionPropertyContext: true);
		var current = RunScenario(removeConditionPropertyContext: false);

		return new ReproReport(control, current);
	}

	static ScenarioResult RunScenario(bool removeConditionPropertyContext)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
		var retainedTargets = new List<Label>(TriggerCount);
		var targetReferences = new List<WeakReference<Label>>(TriggerCount);
		var triggerReferences = new List<WeakReference<Trigger>>(TriggerCount);
		var payloadReferences = new List<WeakReference<TriggerPayload>>(TriggerCount);
		var payloadBufferReferences = new List<WeakReference<byte[]>>(TriggerCount);

		for (var triggerIndex = 0; triggerIndex < TriggerCount; triggerIndex++)
		{
			CreateAndRemoveTrigger(
				removeConditionPropertyContext,
				triggerIndex,
				retainedTargets,
				targetReferences,
				triggerReferences,
				payloadReferences,
				payloadBufferReferences);
		}

		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			retainedTargets.Count,
			CountAlive(targetReferences),
			CountAlive(triggerReferences),
			CountAlive(payloadReferences),
			CountAlive(payloadBufferReferences),
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedTargets);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateAndRemoveTrigger(
		bool removeConditionPropertyContext,
		int triggerIndex,
		List<Label> retainedTargets,
		List<WeakReference<Label>> targetReferences,
		List<WeakReference<Trigger>> triggerReferences,
		List<WeakReference<TriggerPayload>> payloadReferences,
		List<WeakReference<byte[]>> payloadBufferReferences)
	{
		var payload = new TriggerPayload(
			$"trigger-{triggerIndex:000}",
			$"Dynamic trigger rule for dashboard row {triggerIndex:000}; includes validation state, feature flags, and tenant-specific style decisions.",
			new byte[PayloadBytes]);
		payload.Buffer[0] = (byte)triggerIndex;
		payload.Buffer[^1] = (byte)(255 - triggerIndex);

		var target = new Label
		{
			Text = $"Retained target {triggerIndex:000}",
			IsVisible = true
		};

		var trigger = new Trigger(typeof(Label))
		{
			Property = VisualElement.IsVisibleProperty,
			Value = true,
			BindingContext = payload
		};

		var stateProperty = GetStateProperty(trigger);

		target.Triggers.Add(trigger);
		target.Triggers.Remove(trigger);

		if (removeConditionPropertyContext)
			RemoveBindablePropertyContext(target, stateProperty);

		retainedTargets.Add(target);
		targetReferences.Add(new WeakReference<Label>(target));
		triggerReferences.Add(new WeakReference<Trigger>(trigger));
		payloadReferences.Add(new WeakReference<TriggerPayload>(payload));
		payloadBufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));

		trigger = null!;
		target = null!;
		payload = null!;
	}

	static BindableProperty GetStateProperty(Trigger trigger)
	{
		var condition = s_conditionProperty.GetValue(trigger)
			?? throw new InvalidOperationException("Trigger did not expose its internal PropertyCondition.");

		return (BindableProperty?)s_statePropertyField.GetValue(condition)
			?? throw new InvalidOperationException("PropertyCondition did not expose its state property.");
	}

	static void RemoveBindablePropertyContext(BindableObject bindable, BindableProperty property)
	{
		var properties = (IDictionary?)s_propertiesField.GetValue(bindable)
			?? throw new InvalidOperationException("BindableObject did not expose its property context dictionary.");

		object? keyToRemove = null;
		foreach (DictionaryEntry propertyContext in properties)
		{
			var contextProperty = s_contextPropertyField.GetValue(propertyContext.Value);
			if (ReferenceEquals(contextProperty, property))
			{
				keyToRemove = propertyContext.Key;
				break;
			}
		}

		if (keyToRemove is null)
			throw new InvalidOperationException("The target did not retain the condition state property context.");

		properties.Remove(keyToRemove);
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
	int RetainedTargets,
	int TargetsAlive,
	int TriggersAlive,
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
		Control.RetainedTargets == ReproSession.TriggerCount &&
		Control.TargetsAlive == ReproSession.TriggerCount &&
		Control.TriggersAlive == 0 &&
		Control.PayloadBuffersAlive == 0 &&
		Current.RetainedTargets == ReproSession.TriggerCount &&
		Current.TargetsAlive == ReproSession.TriggerCount &&
		Current.TriggersAlive == ReproSession.TriggerCount &&
		Current.PayloadBuffersAlive == ReproSession.TriggerCount;

	public string ToText()
	{
		var builder = new StringBuilder();
		builder.AppendLine(LeakProved ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
		builder.AppendLine("TriggerConditionPropertyContextRetentionRepro");
		builder.AppendLine($"Live target labels retained in both scenarios: {Current.RetainedTargets}");
		builder.AppendLine("Payload per removed Trigger: 1.0 MiB");
		builder.AppendLine();
		AppendScenario(builder, "control: remove the PropertyCondition attached-property context after trigger removal", Control);
		builder.AppendLine();
		AppendScenario(builder, "current: remove Trigger from a retained target label", Current);
		builder.AppendLine();
		builder.AppendLine("Leak path: retained target Label -> stale PropertyCondition._stateProperty BindablePropertyContext -> BindableProperty propertyChanged delegate -> PropertyCondition -> Condition.ConditionChanged -> removed Trigger -> BindingContext/Payload buffer.");
		builder.AppendLine("The target labels remain alive in both scenarios; the signal is whether removed Trigger payloads collect after full GC.");
		builder.AppendLine($"dotnet-version: {Environment.Version}");
		return builder.ToString();
	}

	static void AppendScenario(StringBuilder builder, string title, ScenarioResult result)
	{
		builder.AppendLine($"Run: {title}");
		builder.AppendLine($"  retained target labels: {result.RetainedTargets}");
		builder.AppendLine($"  target labels alive after full GC: {result.TargetsAlive}/{ReproSession.TriggerCount}");
		builder.AppendLine($"  removed Triggers alive after full GC: {result.TriggersAlive}/{ReproSession.TriggerCount}");
		builder.AppendLine($"  trigger payloads alive after full GC: {result.PayloadsAlive}/{ReproSession.TriggerCount}");
		builder.AppendLine($"  trigger payload buffers alive after full GC: {result.PayloadBuffersAlive}/{ReproSession.TriggerCount}");
		builder.AppendLine($"  retained payload bytes: {FormatBytes(result.RetainedPayloadBytes)}");
		builder.AppendLine($"  managed heap delta: {FormatBytes(result.HeapDelta)}");
	}

	static string FormatBytes(long bytes)
	{
		var mib = bytes / 1024d / 1024d;
		return $"{mib:0.0} MiB";
	}
}
