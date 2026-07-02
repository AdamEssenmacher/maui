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

	public const int TriggerCount = 180;
	const int TriggerKindCount = 3;
	const int TriggersPerKind = TriggerCount / TriggerKindCount;
	const int PayloadBytes = 1024 * 1024;

	static readonly PropertyInfo s_conditionProperty =
		typeof(TriggerBase).GetProperty("Condition", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(typeof(TriggerBase).FullName, "Condition");

	static readonly FieldInfo s_statePropertyField =
		typeof(PropertyCondition).GetField("_stateProperty", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(PropertyCondition).FullName, "_stateProperty");

	static readonly FieldInfo s_boundPropertyField =
		typeof(BindingCondition).GetField("_boundProperty", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(BindingCondition).FullName, "_boundProperty");

	static readonly Type s_multiConditionType =
		typeof(MultiTrigger).Assembly.GetType("Microsoft.Maui.Controls.MultiCondition")
		?? throw new TypeLoadException("Microsoft.Maui.Controls.MultiCondition");

	static readonly FieldInfo s_aggregatedStatePropertyField =
		s_multiConditionType.GetField("_aggregatedStateProperty", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(s_multiConditionType.FullName, "_aggregatedStateProperty");

	static readonly PropertyInfo s_multiConditionsProperty =
		s_multiConditionType.GetProperty("Conditions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
		?? throw new MissingMemberException(s_multiConditionType.FullName, "Conditions");

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
		var triggerReferences = new List<WeakReference<TriggerBase>>(TriggerCount);
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
		List<WeakReference<TriggerBase>> triggerReferences,
		List<WeakReference<TriggerPayload>> payloadReferences,
		List<WeakReference<byte[]>> payloadBufferReferences)
	{
		var triggerKind = triggerIndex switch
		{
			< TriggersPerKind => TriggerKind.PropertyTrigger,
			< TriggersPerKind * 2 => TriggerKind.DataTrigger,
			_ => TriggerKind.MultiTrigger
		};
		var payload = new TriggerPayload(
			$"{triggerKind}-{triggerIndex:000}",
			$"Dynamic {triggerKind} rule for dashboard row {triggerIndex:000}; includes validation state, feature flags, and tenant-specific style decisions.",
			new byte[PayloadBytes]);
		payload.Buffer[0] = (byte)triggerIndex;
		payload.Buffer[^1] = (byte)(255 - triggerIndex);

		var target = new Label
		{
			Text = $"Retained target {triggerIndex:000}",
			IsVisible = true
		};

		var trigger = CreateTrigger(triggerKind, target, payload);
		var conditionProperties = GetConditionProperties(trigger);

		target.Triggers.Add(trigger);
		target.Triggers.Remove(trigger);

		if (removeConditionPropertyContext)
		{
			foreach (var conditionProperty in conditionProperties)
				RemoveBindablePropertyContext(target, conditionProperty);
		}

		retainedTargets.Add(target);
		targetReferences.Add(new WeakReference<Label>(target));
		triggerReferences.Add(new WeakReference<TriggerBase>(trigger));
		payloadReferences.Add(new WeakReference<TriggerPayload>(payload));
		payloadBufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));

		trigger = null!;
		target = null!;
		payload = null!;
	}

	static TriggerBase CreateTrigger(TriggerKind triggerKind, Label target, TriggerPayload payload)
	{
		return triggerKind switch
		{
			TriggerKind.PropertyTrigger => new Trigger(typeof(Label))
			{
				Property = VisualElement.IsVisibleProperty,
				Value = true,
				BindingContext = payload
			},
			TriggerKind.DataTrigger => new DataTrigger(typeof(Label))
			{
				Binding = new Binding(nameof(Label.IsVisible)) { Source = target },
				Value = true,
				BindingContext = payload
			},
			TriggerKind.MultiTrigger => CreateMultiTrigger(payload),
			_ => throw new ArgumentOutOfRangeException(nameof(triggerKind), triggerKind, null)
		};
	}

	static MultiTrigger CreateMultiTrigger(TriggerPayload payload)
	{
		var trigger = new MultiTrigger(typeof(Label))
		{
			BindingContext = payload
		};
		trigger.Conditions.Add(new PropertyCondition
		{
			Property = VisualElement.IsVisibleProperty,
			Value = true
		});
		return trigger;
	}

	static IReadOnlyList<BindableProperty> GetConditionProperties(TriggerBase trigger)
	{
		var condition = s_conditionProperty.GetValue(trigger)
			?? throw new InvalidOperationException("Trigger did not expose its internal Condition.");

		var properties = new List<BindableProperty>();
		AddConditionProperties(condition, properties);
		return properties;
	}

	static void AddConditionProperties(object condition, List<BindableProperty> properties)
	{
		if (condition is PropertyCondition)
		{
			properties.Add((BindableProperty?)s_statePropertyField.GetValue(condition)
				?? throw new InvalidOperationException("PropertyCondition did not expose its state property."));
			return;
		}

		if (condition is BindingCondition)
		{
			properties.Add((BindableProperty?)s_boundPropertyField.GetValue(condition)
				?? throw new InvalidOperationException("BindingCondition did not expose its bound property."));
			return;
		}

		if (condition.GetType() == s_multiConditionType)
		{
			properties.Add((BindableProperty?)s_aggregatedStatePropertyField.GetValue(condition)
				?? throw new InvalidOperationException("MultiCondition did not expose its aggregated state property."));

			var childConditions = (IEnumerable?)s_multiConditionsProperty.GetValue(condition)
				?? throw new InvalidOperationException("MultiCondition did not expose its child conditions.");
			foreach (var childCondition in childConditions)
				AddConditionProperties(childCondition, properties);

			return;
		}

		throw new InvalidOperationException($"Unsupported condition type: {condition.GetType().FullName}");
	}

	enum TriggerKind
	{
		PropertyTrigger,
		DataTrigger,
		MultiTrigger
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
		builder.AppendLine("Removed triggers: 60 PropertyCondition Trigger instances, 60 BindingCondition DataTrigger instances, and 60 MultiCondition MultiTrigger instances");
		builder.AppendLine("Payload per removed trigger: 1.0 MiB");
		builder.AppendLine();
		AppendScenario(builder, "control: remove stale condition attached-property contexts after trigger removal", Control);
		builder.AppendLine();
		AppendScenario(builder, "current: remove Trigger/DataTrigger/MultiTrigger from retained target labels", Current);
		builder.AppendLine();
		builder.AppendLine("Leak path: retained target Label -> stale condition-owned BindablePropertyContext -> BindableProperty propertyChanged delegate -> PropertyCondition/BindingCondition/MultiCondition -> Condition.ConditionChanged -> removed Trigger/DataTrigger/MultiTrigger -> BindingContext/Payload buffer.");
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
