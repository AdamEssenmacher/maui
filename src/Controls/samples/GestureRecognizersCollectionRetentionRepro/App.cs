using System.Collections.Specialized;
using System.Reflection;
using System.Text;
using Microsoft.Maui.Controls;

namespace GestureRecognizersCollectionRetentionRepro;

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
			Text = "Running GestureRecognizers collection retention repro...",
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
			var text = "GestureRecognizersCollectionRetentionRepro ERROR" + Environment.NewLine + ex;
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(1);
		}
	}
}

static class ReproSession
{
	public const string ResultsPath = "/tmp/gesture-recognizers-collection-retention-results.txt";

	const int Iterations = 80;
	const int GesturesAddedThenClearedPerOwner = 3;
	const int PayloadBytes = 1024 * 1024;

	public static ReproReport Run()
	{
		var control = RunScenario(clearGestureCollectionHandlers: true);
		var current = RunScenario(clearGestureCollectionHandlers: false);

		return new ReproReport(control, current);
	}

	static ScenarioResult RunScenario(bool clearGestureCollectionHandlers)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
		var retainedGestureCollections = new List<IList<IGestureRecognizer>>(Iterations * 2);
		var ownerReferences = new List<WeakReference<Element>>(Iterations * 2);
		var payloadReferences = new List<WeakReference<GestureOwnerPayload>>(Iterations * 2);
		var payloadBufferReferences = new List<WeakReference<byte[]>>(Iterations * 2);

		for (var i = 0; i < Iterations; i++)
		{
			CreateRetainedGestureCollection(OwnerKind.View, i, clearGestureCollectionHandlers, retainedGestureCollections, ownerReferences, payloadReferences, payloadBufferReferences);
			CreateRetainedGestureCollection(OwnerKind.Span, i, clearGestureCollectionHandlers, retainedGestureCollections, ownerReferences, payloadReferences, payloadBufferReferences);
		}

		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			CountAlive(ownerReferences),
			CountAlive(payloadReferences),
			CountAlive(payloadBufferReferences),
			retainedGestureCollections.Count,
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedGestureCollections);
		return result;
	}

	static void CreateRetainedGestureCollection(
		OwnerKind kind,
		int iteration,
		bool clearGestureCollectionHandlers,
		List<IList<IGestureRecognizer>> retainedGestureCollections,
		List<WeakReference<Element>> ownerReferences,
		List<WeakReference<GestureOwnerPayload>> payloadReferences,
		List<WeakReference<byte[]>> payloadBufferReferences)
	{
		var payload = new GestureOwnerPayload($"{kind}-owner-{iteration}", new byte[PayloadBytes]);
		payload.Buffer[0] = (byte)iteration;

		var owner = CreateOwner(kind, iteration);
		var gestureRecognizers = GetGestureRecognizers(owner);
		for (var gesture = 0; gesture < GesturesAddedThenClearedPerOwner; gesture++)
		{
			gestureRecognizers.Add(new TapGestureRecognizer { NumberOfTapsRequired = 1 });
		}

		gestureRecognizers.Clear();
		if (gestureRecognizers.Count != 0)
			throw new InvalidOperationException($"Expected {kind} gesture collection to be empty after cleanup.");

		owner.BindingContext = payload;

		if (clearGestureCollectionHandlers)
			ClearCollectionChangedHandlers(gestureRecognizers);

		retainedGestureCollections.Add(gestureRecognizers);
		ownerReferences.Add(new WeakReference<Element>(owner));
		payloadReferences.Add(new WeakReference<GestureOwnerPayload>(payload));
		payloadBufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));
	}

	static Element CreateOwner(OwnerKind kind, int iteration)
	{
		return kind switch
		{
			OwnerKind.View => new Label { Text = $"discarded view {iteration}" },
			OwnerKind.Span => new Span { Text = $"discarded span {iteration}" },
			_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
		};
	}

	static IList<IGestureRecognizer> GetGestureRecognizers(Element owner)
	{
		return owner switch
		{
			View view => view.GestureRecognizers,
			GestureElement gestureElement => gestureElement.GestureRecognizers,
			_ => throw new ArgumentOutOfRangeException(nameof(owner), owner, null)
		};
	}

	static void ClearCollectionChangedHandlers(IList<IGestureRecognizer> gestureRecognizers)
	{
		if (gestureRecognizers is not INotifyCollectionChanged)
			throw new InvalidOperationException($"Expected {gestureRecognizers.GetType().FullName} to implement INotifyCollectionChanged.");

		var type = gestureRecognizers.GetType();
		while (type is not null)
		{
			var field = type.GetField("CollectionChanged", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			if (field is not null && typeof(NotifyCollectionChangedEventHandler).IsAssignableFrom(field.FieldType))
			{
				field.SetValue(gestureRecognizers, null);
				return;
			}

			type = type.BaseType;
		}

		throw new InvalidOperationException($"Could not find the CollectionChanged backing field on {gestureRecognizers.GetType().FullName}.");
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

	enum OwnerKind
	{
		View,
		Span
	}

	sealed class GestureOwnerPayload
	{
		public GestureOwnerPayload(string name, byte[] buffer)
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
		int RetainedGestureCollections,
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
			Current.OwnersAlive == Iterations * 2 &&
			Current.PayloadsAlive == Iterations * 2 &&
			Current.PayloadBuffersAlive == Iterations * 2;

		public string ToText()
		{
			var retainedBytes = Current.PayloadBuffersAlive * PayloadBytes;
			var builder = new StringBuilder();
			builder.AppendLine(LeakProved ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			builder.AppendLine("GestureRecognizersCollectionRetentionRepro");
			builder.AppendLine($"Iterations per owner type: {Iterations}");
			builder.AppendLine("Owner types: View, Span");
			builder.AppendLine($"Gesture recognizers added then cleared per owner: {GesturesAddedThenClearedPerOwner}");
			builder.AppendLine($"Retained empty gesture collections per run: {Iterations * 2}");
			builder.AppendLine($"Payload per discarded owner: {PayloadBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Run: control: retained empty gesture collections after clearing MAUI CollectionChanged handlers");
			AppendScenario(builder, Control);
			builder.AppendLine();
			builder.AppendLine("Run: current: retained empty gesture collections with MAUI CollectionChanged handlers intact");
			AppendScenario(builder, Current);
			builder.AppendLine($"  retained payload bytes: {retainedBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Leak path: app gesture collection cache -> View/Span.GestureRecognizers ObservableCollection -> anonymous CollectionChanged handler -> View/Span -> BindingContext payload");
			builder.AppendLine($"dotnet-version: {Environment.Version}");
			return builder.ToString();
		}

		static void AppendScenario(StringBuilder builder, ScenarioResult result)
		{
			builder.AppendLine($"  empty gesture collections retained by app cache: {result.RetainedGestureCollections}");
			builder.AppendLine($"  owners alive after full GC: {result.OwnersAlive}/{Iterations * 2}");
			builder.AppendLine($"  owner payloads alive after full GC: {result.PayloadsAlive}/{Iterations * 2}");
			builder.AppendLine($"  owner payload buffers alive after full GC: {result.PayloadBuffersAlive}/{Iterations * 2}");
			builder.AppendLine($"  managed heap delta: {result.HeapDelta / 1024d / 1024d:0.0} MiB");
		}
	}
}
