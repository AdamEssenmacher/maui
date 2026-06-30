using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui.Controls;

namespace VsmWatchAddListCollectionHandleRetentionRepro;

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
			Text = "Running VSM WatchAddList collection handle retention repro...",
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
			var text = "VsmWatchAddListCollectionHandleRetentionRepro ERROR" + Environment.NewLine + ex;
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(1);
		}
	}
}

static class ReproSession
{
	public const string ResultsPath = "/tmp/vsm-watchaddlist-collection-handle-retention-results.txt";

	const int Iterations = 80;
	const int ItemsAddedThenRemovedPerCollection = 3;
	const int PayloadBytes = 1024 * 1024;

	static readonly CollectionKind[] Kinds = Enum.GetValues<CollectionKind>();
	static readonly ConditionalWeakTable<object, CollectionOwnerPayload> Payloads = new();

	public static ReproReport Run()
	{
		var control = RunScenario(clearWatchAddCallback: true);
		var current = RunScenario(clearWatchAddCallback: false);

		return new ReproReport(control, current);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static ScenarioResult RunScenario(bool clearWatchAddCallback)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
		var retainedCollections = new List<object>(Iterations * Kinds.Length);
		var ownerReferences = new List<WeakReference<object>>(Iterations * Kinds.Length);
		var payloadReferences = new List<WeakReference<CollectionOwnerPayload>>(Iterations * Kinds.Length);
		var payloadBufferReferences = new List<WeakReference<byte[]>>(Iterations * Kinds.Length);

		for (var i = 0; i < Iterations; i++)
		{
			foreach (var kind in Kinds)
				CreateRetainedCollection(kind, i, clearWatchAddCallback, retainedCollections, ownerReferences, payloadReferences, payloadBufferReferences);
		}

		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			CountAlive(ownerReferences),
			CountAlive(payloadReferences),
			CountAlive(payloadBufferReferences),
			retainedCollections.Count,
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedCollections);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateRetainedCollection(
		CollectionKind kind,
		int iteration,
		bool clearWatchAddCallback,
		List<object> retainedCollections,
		List<WeakReference<object>> ownerReferences,
		List<WeakReference<CollectionOwnerPayload>> payloadReferences,
		List<WeakReference<byte[]>> payloadBufferReferences)
	{
		var payload = new CollectionOwnerPayload($"{kind}-owner-{iteration}", new byte[PayloadBytes]);
		payload.Buffer[0] = (byte)iteration;

		object owner;
		object collection;

		switch (kind)
		{
			case CollectionKind.VisualStateStateTriggers:
			{
				var state = new VisualState { Name = "State" + iteration };
				Payloads.Add(state, payload);
				var triggers = state.StateTriggers;

				for (var i = 0; i < ItemsAddedThenRemovedPerCollection; i++)
					triggers.Add(new StateTrigger { IsActive = (i % 2) == 0 });

				RemoveAll(triggers);
				owner = state;
				collection = triggers;
				break;
			}
			case CollectionKind.VisualStateGroupStates:
			{
				var group = new VisualStateGroup { Name = "Group" + iteration };
				Payloads.Add(group, payload);
				var states = group.States;

				for (var i = 0; i < ItemsAddedThenRemovedPerCollection; i++)
					states.Add(new VisualState { Name = "State" + iteration + "_" + i });

				RemoveAll(states);
				owner = group;
				collection = states;
				break;
			}
			default:
				throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
		}

		if (clearWatchAddCallback)
			ClearWatchAddCallback(collection);

		retainedCollections.Add(collection);
		ownerReferences.Add(new WeakReference<object>(owner));
		payloadReferences.Add(new WeakReference<CollectionOwnerPayload>(payload));
		payloadBufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));
	}

	static void RemoveAll<T>(IList<T> collection)
	{
		while (collection.Count > 0)
			collection.RemoveAt(0);
	}

	static void ClearWatchAddCallback(object collection)
	{
		var field = collection.GetType().GetField("_onAdd", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(collection.GetType().FullName, "_onAdd");

		field.SetValue(collection, null);
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

	enum CollectionKind
	{
		VisualStateStateTriggers,
		VisualStateGroupStates
	}

	sealed class CollectionOwnerPayload
	{
		public CollectionOwnerPayload(string name, byte[] buffer)
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
		int RetainedCollections,
		long HeapBefore,
		long HeapAfter)
	{
		public long HeapDelta => HeapAfter - HeapBefore;
	}

	public readonly record struct ReproReport(ScenarioResult Control, ScenarioResult Current)
	{
		static int ExpectedOwners => Iterations * Kinds.Length;

		public bool LeakProved =>
			Control.OwnersAlive == 0 &&
			Control.PayloadsAlive == 0 &&
			Control.PayloadBuffersAlive == 0 &&
			Current.OwnersAlive == ExpectedOwners &&
			Current.PayloadsAlive == ExpectedOwners &&
			Current.PayloadBuffersAlive == ExpectedOwners;

		public string ToText()
		{
			var retainedBytes = Current.PayloadBuffersAlive * PayloadBytes;
			var builder = new StringBuilder();
			builder.AppendLine(LeakProved ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			builder.AppendLine("VsmWatchAddListCollectionHandleRetentionRepro");
			builder.AppendLine($"Iterations per collection surface: {Iterations}");
			builder.AppendLine($"Collection surfaces: {string.Join(", ", Kinds)}");
			builder.AppendLine($"Items added then removed per collection: {ItemsAddedThenRemovedPerCollection}");
			builder.AppendLine($"Retained empty public collections per run: {ExpectedOwners}");
			builder.AppendLine($"Payload per discarded owner: {PayloadBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Run: control: retained empty public collections after clearing WatchAddList._onAdd");
			AppendScenario(builder, Control);
			builder.AppendLine();
			builder.AppendLine("Run: current: retained empty public collections with WatchAddList._onAdd intact");
			AppendScenario(builder, Current);
			builder.AppendLine($"  retained payload bytes: {retainedBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Leak path: app public collection cache -> WatchAddList._onAdd delegate -> discarded VisualState/VisualStateGroup owner -> ConditionalWeakTable owner payload");
			builder.AppendLine("Distinct from C144: all child states/triggers are removed individually before retaining the empty collection handles.");
			builder.AppendLine($"dotnet-version: {Environment.Version}");
			return builder.ToString();
		}

		static void AppendScenario(StringBuilder builder, ScenarioResult result)
		{
			builder.AppendLine($"  public collections retained by app cache: {result.RetainedCollections}");
			builder.AppendLine($"  owners alive after full GC: {result.OwnersAlive}/{ExpectedOwners}");
			builder.AppendLine($"  owner payloads alive after full GC: {result.PayloadsAlive}/{ExpectedOwners}");
			builder.AppendLine($"  owner payload buffers alive after full GC: {result.PayloadBuffersAlive}/{ExpectedOwners}");
			builder.AppendLine($"  managed heap delta: {result.HeapDelta / 1024d / 1024d:0.0} MiB");
		}
	}
}
