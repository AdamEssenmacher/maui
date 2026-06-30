using System.Collections;
using System.Collections.Specialized;
using System.Reflection;
using System.Text;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Maps;
using ControlsMap = Microsoft.Maui.Controls.Maps.Map;

namespace MapCollectionsRetentionRepro;

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
			Text = "Running Map public collection retention repro...",
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
			var text = "MapCollectionsRetentionRepro ERROR" + Environment.NewLine + ex;
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(1);
		}
	}
}

static class ReproSession
{
	public const string ResultsPath = "/tmp/map-collections-retention-results.txt";

	const int Iterations = 80;
	const int ItemsAddedThenRemovedPerCollection = 3;
	const int PayloadBytes = 1024 * 1024;

	public static ReproReport Run()
	{
		var control = RunScenario(clearMapCollectionHandlers: true);
		var current = RunScenario(clearMapCollectionHandlers: false);

		return new ReproReport(control, current);
	}

	static ScenarioResult RunScenario(bool clearMapCollectionHandlers)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
		var retainedCollections = new List<IList>(Iterations * 2);
		var mapReferences = new List<WeakReference<ControlsMap>>(Iterations * 2);
		var payloadReferences = new List<WeakReference<MapPayload>>(Iterations * 2);
		var payloadBufferReferences = new List<WeakReference<byte[]>>(Iterations * 2);

		for (var i = 0; i < Iterations; i++)
		{
			CreateRetainedMapCollection(CollectionKind.Pins, i, clearMapCollectionHandlers, retainedCollections, mapReferences, payloadReferences, payloadBufferReferences);
			CreateRetainedMapCollection(CollectionKind.MapElements, i, clearMapCollectionHandlers, retainedCollections, mapReferences, payloadReferences, payloadBufferReferences);
		}

		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			CountAlive(mapReferences),
			CountAlive(payloadReferences),
			CountAlive(payloadBufferReferences),
			retainedCollections.Count,
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedCollections);
		return result;
	}

	static void CreateRetainedMapCollection(
		CollectionKind kind,
		int iteration,
		bool clearMapCollectionHandlers,
		List<IList> retainedCollections,
		List<WeakReference<ControlsMap>> mapReferences,
		List<WeakReference<MapPayload>> payloadReferences,
		List<WeakReference<byte[]>> payloadBufferReferences)
	{
		var payload = new MapPayload($"{kind}-map-{iteration}", new byte[PayloadBytes]);
		payload.Buffer[0] = (byte)iteration;

		var map = new ControlsMap { BindingContext = payload };
		var collection = GetCollection(map, kind);

		for (var item = 0; item < ItemsAddedThenRemovedPerCollection; item++)
		{
			AddItem(collection, kind, iteration, item);
		}

		while (collection.Count > 0)
		{
			collection.RemoveAt(0);
		}

		if (clearMapCollectionHandlers)
			ClearCollectionChangedHandlers(collection);

		retainedCollections.Add(collection);
		mapReferences.Add(new WeakReference<ControlsMap>(map));
		payloadReferences.Add(new WeakReference<MapPayload>(payload));
		payloadBufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));
	}

	static IList GetCollection(ControlsMap map, CollectionKind kind)
	{
		return kind switch
		{
			CollectionKind.Pins => (IList)map.Pins,
			CollectionKind.MapElements => (IList)map.MapElements,
			_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
		};
	}

	static void AddItem(IList collection, CollectionKind kind, int iteration, int item)
	{
		var latitude = 47.6205 + iteration * 0.0001 + item * 0.00001;
		var longitude = -122.3493 - iteration * 0.0001 - item * 0.00001;

		switch (kind)
		{
			case CollectionKind.Pins:
				collection.Add(new Pin
				{
					Label = $"Service stop {iteration}-{item}",
					Address = "Seattle, WA",
					Location = new Location(latitude, longitude),
					Type = PinType.Place
				});
				break;
			case CollectionKind.MapElements:
				collection.Add(new Circle
				{
					Center = new Location(latitude, longitude),
					Radius = Distance.FromMeters(120 + item * 10),
					StrokeColor = Colors.DeepSkyBlue,
					FillColor = Colors.LightSkyBlue.WithAlpha(0.25f),
					StrokeWidth = 3
				});
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
		}
	}

	static void ClearCollectionChangedHandlers(IList collection)
	{
		if (collection is not INotifyCollectionChanged)
			throw new InvalidOperationException($"Expected {collection.GetType().FullName} to implement INotifyCollectionChanged.");

		var type = collection.GetType();
		while (type is not null)
		{
			var field = type.GetField("CollectionChanged", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			if (field is not null && typeof(NotifyCollectionChangedEventHandler).IsAssignableFrom(field.FieldType))
			{
				field.SetValue(collection, null);
				return;
			}

			type = type.BaseType;
		}

		throw new InvalidOperationException($"Could not find the CollectionChanged backing field on {collection.GetType().FullName}.");
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
		Pins,
		MapElements
	}

	sealed class MapPayload
	{
		public MapPayload(string name, byte[] buffer)
		{
			Name = name;
			Buffer = buffer;
		}

		public string Name { get; }

		public byte[] Buffer { get; }
	}

	public readonly record struct ScenarioResult(
		int MapsAlive,
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
		public bool LeakProved =>
			Control.MapsAlive == 0 &&
			Control.PayloadsAlive == 0 &&
			Control.PayloadBuffersAlive == 0 &&
			Current.MapsAlive == Iterations * 2 &&
			Current.PayloadsAlive == Iterations * 2 &&
			Current.PayloadBuffersAlive == Iterations * 2;

		public string ToText()
		{
			var retainedBytes = Current.PayloadBuffersAlive * PayloadBytes;
			var builder = new StringBuilder();
			builder.AppendLine(LeakProved ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			builder.AppendLine("MapCollectionsRetentionRepro");
			builder.AppendLine($"Iterations per collection type: {Iterations}");
			builder.AppendLine("Collection types: Pins, MapElements");
			builder.AppendLine($"Items added then removed per collection: {ItemsAddedThenRemovedPerCollection}");
			builder.AppendLine($"Retained empty map collections per run: {Iterations * 2}");
			builder.AppendLine($"Payload per discarded map: {PayloadBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Run: control: retained empty map collections after clearing MAUI CollectionChanged handlers");
			AppendScenario(builder, Control);
			builder.AppendLine();
			builder.AppendLine("Run: current: retained empty map collections with MAUI CollectionChanged handlers intact");
			AppendScenario(builder, Current);
			builder.AppendLine($"  retained payload bytes: {retainedBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Leak path: app map collection cache -> Map.Pins/Map.MapElements ObservableCollection -> CollectionChanged handler -> Map -> BindingContext payload");
			builder.AppendLine($"dotnet-version: {Environment.Version}");
			return builder.ToString();
		}

		static void AppendScenario(StringBuilder builder, ScenarioResult result)
		{
			builder.AppendLine($"  empty map collections retained by app cache: {result.RetainedCollections}");
			builder.AppendLine($"  maps alive after full GC: {result.MapsAlive}/{Iterations * 2}");
			builder.AppendLine($"  map payloads alive after full GC: {result.PayloadsAlive}/{Iterations * 2}");
			builder.AppendLine($"  map payload buffers alive after full GC: {result.PayloadBuffersAlive}/{Iterations * 2}");
			builder.AppendLine($"  managed heap delta: {result.HeapDelta / 1024d / 1024d:0.0} MiB");
		}
	}
}
