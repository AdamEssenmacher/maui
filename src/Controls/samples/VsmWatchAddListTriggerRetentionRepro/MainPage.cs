#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Foundation;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Devices;

namespace VsmWatchAddListTriggerRetentionRepro;

public sealed class MainPage : ContentPage
{
	const int TriggersPerMutationKind = 40;
	const int PayloadBytes = 1024 * 1024;

	static readonly MutationKind[] MutationKinds =
	[
		MutationKind.StateTriggersClear,
		MutationKind.StatesClear,
		MutationKind.GroupsClear
	];

	static readonly MethodInfo InvalidateStateTriggersMethod =
		typeof(VisualElement).GetMethod("InvalidateStateTriggers", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(VisualElement).FullName, "InvalidateStateTriggers");

	static readonly MethodInfo SendDetachedMethod =
		typeof(StateTriggerBase).GetMethod("SendDetached", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(StateTriggerBase).FullName, "SendDetached");

	readonly string? _resultsPath;
	readonly Label _status;

	public MainPage(string? resultsPath)
	{
		_resultsPath = resultsPath;
		_status = new Label
		{
			Text = "Running VSM WatchAddList trigger retention leak repro...",
			Margin = 24
		};

		Content = _status;
		Loaded += OnLoaded;
	}

	async void OnLoaded(object? sender, EventArgs e)
	{
		Loaded -= OnLoaded;

		string text;
		try
		{
			var result = await RunScenariosAsync();
			text = result.ToText();
		}
		catch (Exception ex)
		{
			text = "RESULT: FAILED" + Environment.NewLine + ex;
		}

		_status.Text = text;

		if (!string.IsNullOrWhiteSpace(_resultsPath))
			System.IO.File.WriteAllText(_resultsPath, text);

		await Task.Delay(250);
		Process.GetCurrentProcess().Kill();
	}

	static async Task<ReproResult> RunScenariosAsync()
	{
		var control = await RunScenarioAsync(
			"control: explicitly detach triggers before mutating VSM lists",
			detachBeforeMutation: true);

		var current = await RunScenarioAsync(
			"current: WatchAddList mutation leaves triggers attached",
			detachBeforeMutation: false);

		return new ReproResult(TriggersPerMutationKind, MutationKinds.Length, PayloadBytes, control, current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(string name, bool detachBeforeMutation)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		var targetReferences = new List<WeakReference<ContentView>>(TotalTriggers);
		var groupReferences = new List<WeakReference<VisualStateGroup>>(TotalTriggers);
		var stateReferences = new List<WeakReference<VisualState>>(TotalTriggers);
		var triggerReferences = new List<WeakReference<DisplayRotationStateTrigger>>(TotalTriggers);
		var payloadReferences = new List<WeakReference<Payload>>(TotalTriggers);
		var bufferReferences = new List<WeakReference<byte[]>>(TotalTriggers);

		foreach (var mutationKind in MutationKinds)
		{
			for (var i = 0; i < TriggersPerMutationKind; i++)
			{
				using (new NSAutoreleasePool())
				{
					var payloadIndex = ((int)mutationKind * TriggersPerMutationKind) + i;
					var payload = new Payload(payloadIndex, PayloadBytes);
					var trigger = new DisplayRotationStateTrigger
					{
						Rotation = DisplayRotation.Rotation0,
						BindingContext = payload
					};
					var state = new VisualState { Name = "LiveState" + payloadIndex };
					var group = new VisualStateGroup { Name = "LiveGroup" + payloadIndex };
					var groups = new VisualStateGroupList();
					var target = new ContentView
					{
						Content = new Label { Text = "VSM target " + payloadIndex }
					};

					state.StateTriggers.Add(trigger);
					group.States.Add(state);
					groups.Add(group);
					VisualStateManager.SetVisualStateGroups(target, groups);
					InvokeInvalidateStateTriggers(target, attach: true);

					if (detachBeforeMutation)
						InvokeSendDetached(trigger);

					ApplyMutation(mutationKind, groups, group, state);

					targetReferences.Add(new WeakReference<ContentView>(target));
					groupReferences.Add(new WeakReference<VisualStateGroup>(group));
					stateReferences.Add(new WeakReference<VisualState>(state));
					triggerReferences.Add(new WeakReference<DisplayRotationStateTrigger>(trigger));
					payloadReferences.Add(new WeakReference<Payload>(payload));
					bufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));

					target = null!;
					groups = null!;
					group = null!;
					state = null!;
					trigger = null!;
					payload = null!;
				}

				if (i % 10 == 0)
					await Task.Yield();
			}
		}

		await Task.Delay(250);
		ForceGc();
		await Task.Delay(250);
		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var liveTriggers = GetAlive(triggerReferences);
		return new ScenarioResult(
			name,
			CountAlive(targetReferences),
			CountAlive(groupReferences),
			CountAlive(stateReferences),
			liveTriggers.Count,
			liveTriggers.Count(static trigger => trigger.IsAttached),
			CountAlive(payloadReferences),
			CountAlive(bufferReferences),
			heapBefore,
			heapAfter);
	}

	static int TotalTriggers => TriggersPerMutationKind * MutationKinds.Length;

	static void ApplyMutation(MutationKind mutationKind, VisualStateGroupList groups, VisualStateGroup group, VisualState state)
	{
		switch (mutationKind)
		{
			case MutationKind.StateTriggersClear:
				state.StateTriggers.Clear();
				break;
			case MutationKind.StatesClear:
				group.States.Clear();
				break;
			case MutationKind.GroupsClear:
				groups.Clear();
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(mutationKind), mutationKind, null);
		}
	}

	static void InvokeInvalidateStateTriggers(VisualElement element, bool attach) =>
		InvalidateStateTriggersMethod.Invoke(element, [attach]);

	static void InvokeSendDetached(StateTriggerBase trigger) =>
		SendDetachedMethod.Invoke(trigger, null);

	static List<T> GetAlive<T>(IEnumerable<WeakReference<T>> references)
		where T : class
	{
		var alive = new List<T>();

		foreach (var reference in references)
		{
			if (reference.TryGetTarget(out var target))
				alive.Add(target);
		}

		return alive;
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
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
		}
	}
}

public sealed record ReproResult(
	int TriggersPerMutationKind,
	int MutationKindCount,
	int PayloadBytes,
	ScenarioResult Control,
	ScenarioResult Current)
{
	public int TotalTriggers => TriggersPerMutationKind * MutationKindCount;

	public bool LeakProved =>
		Control.AliveTriggers == 0 &&
		Control.AliveAttachedTriggers == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AlivePayloadBuffers == 0 &&
		Current.AliveTriggers == TotalTriggers &&
		Current.AliveAttachedTriggers == TotalTriggers &&
		Current.AlivePayloads == TotalTriggers &&
		Current.AlivePayloadBuffers == TotalTriggers;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"VsmWatchAddListTriggerRetentionRepro",
			$"Mutation kinds: {MutationKindCount} (StateTriggers.Clear, States.Clear, VisualStateGroupList.Clear)",
			$"Triggers per mutation kind: {TriggersPerMutationKind}",
			$"Total triggers: {TotalTriggers}",
			$"Payload per trigger: {PayloadBytes / 1024 / 1024} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Control.ToText(TotalTriggers, PayloadBytes),
			string.Empty,
			Current.ToText(TotalTriggers, PayloadBytes));
	}
}

public sealed record ScenarioResult(
	string Name,
	int AliveTargets,
	int AliveGroups,
	int AliveStates,
	int AliveTriggers,
	int AliveAttachedTriggers,
	int AlivePayloads,
	int AlivePayloadBuffers,
	long HeapBefore,
	long HeapAfter)
{
	public string ToText(int totalTriggers, int payloadBytes)
	{
		var retainedPayloadBytes = (long)AlivePayloadBuffers * payloadBytes;
		var totalPayloadBytes = (long)totalTriggers * payloadBytes;

		return string.Join(Environment.NewLine,
			$"Run: {Name}",
			$"  targets alive after full GC: {AliveTargets}/{totalTriggers}",
			$"  visual state groups alive after full GC: {AliveGroups}/{totalTriggers}",
			$"  visual states alive after full GC: {AliveStates}/{totalTriggers}",
			$"  removed triggers alive after full GC: {AliveTriggers}/{totalTriggers}",
			$"  removed triggers still attached: {AliveAttachedTriggers}/{totalTriggers}",
			$"  payloads alive after full GC: {AlivePayloads}/{totalTriggers}",
			$"  payload byte arrays alive after full GC: {AlivePayloadBuffers}/{totalTriggers}",
			$"  retained payload bytes: {FormatBytes(retainedPayloadBytes)} ({retainedPayloadBytes * 100.0 / totalPayloadBytes:0.0}%)",
			$"  managed heap before: {FormatBytes(HeapBefore)}",
			$"  managed heap after: {FormatBytes(HeapAfter)}",
			$"  managed heap delta: {FormatBytes(HeapAfter - HeapBefore)}");
	}

	static string FormatBytes(long bytes)
	{
		var sign = bytes < 0 ? "-" : "";
		var value = Math.Abs((double)bytes);
		if (value >= 1024 * 1024)
			return $"{sign}{value / 1024 / 1024:0.0} MiB";
		if (value >= 1024)
			return $"{sign}{value / 1024:0.0} KiB";
		return $"{bytes} B";
	}
}

public sealed class Payload
{
	public Payload(int index, int size)
	{
		Buffer = new byte[size];

		for (var i = 0; i < Buffer.Length; i += 4096)
			Buffer[i] = (byte)(index + i);
	}

	public byte[] Buffer { get; }
}

public enum MutationKind
{
	StateTriggersClear,
	StatesClear,
	GroupsClear
}
