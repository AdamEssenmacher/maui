using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Controls;

namespace RadioButtonGroupStaleControllerRetentionRepro;

public sealed class App : Application
{
	const int OwnerCount = 80;
	const int PayloadBytes = 1024 * 1024;
	const string ResultPath = "/tmp/radiobuttongroup-stale-controller-retention-results.txt";

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var page = new ContentPage
		{
			Content = new Label
			{
				Text = "Running RadioButtonGroup stale controller retention repro",
				HorizontalOptions = LayoutOptions.Center,
				VerticalOptions = LayoutOptions.Center
			}
		};

		page.Dispatcher.Dispatch(async () =>
		{
			await Task.Delay(250).ConfigureAwait(false);
			var report = Run();
			File.WriteAllText(ResultPath, report);
			Environment.Exit(0);
		});

		return new Window(page);
	}

	static string Run()
	{
		var control = CreateScenario("explicit stale controller-map cleanup", clearControllerMap: true);
		var current = CreateScenario("current MAUI stale controller map", clearControllerMap: false);

		ForceFullCollection();

		var controlSummary = control.Summarize();
		var currentSummary = current.Summarize();

		var proved = controlSummary.ControllerMappingsAlive == 0 &&
			controlSummary.LayoutsAlive == 0 &&
			controlSummary.SiblingLabelsAlive == 0 &&
			controlSummary.PayloadsAlive == 0 &&
			controlSummary.RetainedPayloadBytes == 0 &&
			currentSummary.ControllerMappingsAlive == OwnerCount &&
			currentSummary.LayoutsAlive == OwnerCount &&
			currentSummary.SiblingLabelsAlive == OwnerCount &&
			currentSummary.PayloadsAlive == OwnerCount &&
			currentSummary.PayloadBuffersAlive == OwnerCount &&
			currentSummary.RetainedPayloadBytes == (long)OwnerCount * PayloadBytes;

		return string.Join(Environment.NewLine,
			$"RESULT: {(proved ? "PROVEN" : "NOT PROVEN")}",
			$"Owners retained in both scenarios: {OwnerCount} RadioButton instances after GroupName changed away from the layout group",
			$"Sibling payload per old group layout: {PayloadBytes:N0} bytes",
			string.Empty,
			controlSummary.ToReportBlock(),
			string.Empty,
			currentSummary.ToReportBlock(),
			string.Empty,
			"Interpretation:",
			"RadioButtonGroupController stores each grouped RadioButton in a static ConditionalWeakTable keyed by the RadioButton.",
			"When a RadioButton.GroupName changes away from the layout's group, current MAUI clears the selected value but leaves the old weak-table entry.",
			"An app-retained RadioButton therefore keeps the old RadioButtonGroupController alive, and that controller strongly holds the old layout.",
			"The sibling payload is not stored on the retained RadioButton; it survives only through the stale controller-to-layout path.",
			$"Result file: {ResultPath}");
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static Scenario CreateScenario(string name, bool clearControllerMap)
	{
		var scenario = new Scenario(name);

		for (var i = 0; i < OwnerCount; i++)
		{
			var owner = CreateOwner(i, clearControllerMap);

			scenario.RetainedRadioButtons.Add(owner.RadioButton);
			scenario.Layouts.Add(new WeakReference<VerticalStackLayout>(owner.Layout));
			scenario.SiblingLabels.Add(new WeakReference<Label>(owner.SiblingLabel));
			scenario.Payloads.Add(new WeakReference<Payload>(owner.Payload));
			scenario.PayloadBuffers.Add(new WeakReference<byte[]>(owner.Payload.Buffer));
		}

		return scenario;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static Owner CreateOwner(int index, bool clearControllerMap)
	{
		var radioButton = new RadioButton
		{
			Content = $"Option {index}",
			Value = $"value-{index}"
		};

		var payload = new Payload(index, PayloadBytes);
		var siblingLabel = new Label
		{
			Text = $"Sibling payload holder {index}",
			BindingContext = payload
		};

		var layout = new VerticalStackLayout();
		layout.Children.Add(radioButton);
		layout.Children.Add(siblingLabel);
		RadioButtonGroup.SetGroupName(layout, $"initial-group-{index}");

		radioButton.GroupName = $"detached-group-{index}";

		if (clearControllerMap)
			RadioButtonGroupControllerAccess.Remove(radioButton);

		return new Owner(radioButton, layout, siblingLabel, payload);
	}

	static void ForceFullCollection()
	{
		for (var i = 0; i < 8; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(50);
		}
	}

	sealed class Payload
	{
		public Payload(int index, int size)
		{
			Buffer = new byte[size];
			for (var i = 0; i < Buffer.Length; i += 4096)
				Buffer[i] = (byte)(index + i);
		}

		public byte[] Buffer { get; }
	}

	readonly record struct Owner(RadioButton RadioButton, VerticalStackLayout Layout, Label SiblingLabel, Payload Payload);

	sealed class Scenario(string name)
	{
		public string Name { get; } = name;
		public List<RadioButton> RetainedRadioButtons { get; } = [];
		public List<WeakReference<VerticalStackLayout>> Layouts { get; } = [];
		public List<WeakReference<Label>> SiblingLabels { get; } = [];
		public List<WeakReference<Payload>> Payloads { get; } = [];
		public List<WeakReference<byte[]>> PayloadBuffers { get; } = [];

		public ScenarioSummary Summarize()
		{
			var mappings = 0;
			foreach (var radioButton in RetainedRadioButtons)
			{
				if (RadioButtonGroupControllerAccess.HasController(radioButton))
					mappings++;
			}

			var buffersAlive = CountAlive(PayloadBuffers);

			return new ScenarioSummary(
				Name,
				mappings,
				CountAlive(Layouts),
				CountAlive(SiblingLabels),
				CountAlive(Payloads),
				buffersAlive,
				(long)buffersAlive * PayloadBytes);
		}

		static int CountAlive<T>(IEnumerable<WeakReference<T>> refs)
			where T : class
		{
			var count = 0;
			foreach (var weak in refs)
			{
				if (weak.TryGetTarget(out _))
					count++;
			}

			return count;
		}
	}

	readonly record struct ScenarioSummary(
		string Name,
		int ControllerMappingsAlive,
		int LayoutsAlive,
		int SiblingLabelsAlive,
		int PayloadsAlive,
		int PayloadBuffersAlive,
		long RetainedPayloadBytes)
	{
		public string ToReportBlock() =>
			string.Join(Environment.NewLine,
				$"{Name}:",
				$"  stale RadioButtonGroupController mappings: {ControllerMappingsAlive}/{OwnerCount}",
				$"  old group layouts alive: {LayoutsAlive}/{OwnerCount}",
				$"  sibling labels alive: {SiblingLabelsAlive}/{OwnerCount}",
				$"  sibling payloads alive: {PayloadsAlive}/{OwnerCount}",
				$"  sibling payload buffers alive: {PayloadBuffersAlive}/{OwnerCount}",
				$"  retained sibling payload bytes: {RetainedPayloadBytes:N0}");
	}

	static class RadioButtonGroupControllerAccess
	{
		static readonly object? GroupControllers;
		static readonly MethodInfo? RemoveMethod;
		static readonly MethodInfo? TryGetValueMethod;

		static RadioButtonGroupControllerAccess()
		{
			var controllerType = typeof(RadioButton).Assembly.GetType("Microsoft.Maui.Controls.RadioButtonGroupController");
			var field = controllerType?.GetField("groupControllers", BindingFlags.Static | BindingFlags.NonPublic);
			GroupControllers = field?.GetValue(null);
			RemoveMethod = GroupControllers?.GetType().GetMethod("Remove", [typeof(RadioButton)]);
			TryGetValueMethod = GroupControllers?.GetType().GetMethod("TryGetValue", [typeof(RadioButton), controllerType!.MakeByRefType()]);
		}

		public static void Remove(RadioButton radioButton)
		{
			RemoveMethod?.Invoke(GroupControllers, [radioButton]);
		}

		public static bool HasController(RadioButton radioButton)
		{
			if (TryGetValueMethod is null || GroupControllers is null)
				return false;

			var args = new object?[] { radioButton, null };
			return (bool)(TryGetValueMethod.Invoke(GroupControllers, args) ?? false);
		}
	}
}
