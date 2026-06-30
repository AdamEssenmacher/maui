using System.Collections.Specialized;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui.Controls;

namespace ShellContentMenuItemsCollectionRetentionRepro;

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
			Text = "Running ShellContent.MenuItems collection retention repro...",
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
			var text = "ShellContentMenuItemsCollectionRetentionRepro ERROR" + Environment.NewLine + ex;
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(1);
		}
	}
}

static class ReproSession
{
	public const string ResultsPath = "/tmp/shellcontent-menuitems-collection-retention-results.txt";

	const int Iterations = 160;
	const int MenuItemsAddedThenRemovedPerOwner = 3;
	const int PayloadBytes = 1024 * 1024;

	public static ReproReport Run()
	{
		var control = RunScenario(clearCollectionHandlers: true);
		var current = RunScenario(clearCollectionHandlers: false);

		return new ReproReport(control, current);
	}

	static ScenarioResult RunScenario(bool clearCollectionHandlers)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
		var retainedMenuItemsCollections = new List<object>(Iterations);
		var ownerReferences = new List<WeakReference<ShellContent>>(Iterations);
		var payloadReferences = new List<WeakReference<CollectionOwnerPayload>>(Iterations);
		var payloadBufferReferences = new List<WeakReference<byte[]>>(Iterations);

		for (var i = 0; i < Iterations; i++)
			CreateRetainedMenuItemsCollection(i, clearCollectionHandlers, retainedMenuItemsCollections, ownerReferences, payloadReferences, payloadBufferReferences);

		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			CountAlive(ownerReferences),
			CountAlive(payloadReferences),
			CountAlive(payloadBufferReferences),
			retainedMenuItemsCollections.Count,
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedMenuItemsCollections);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateRetainedMenuItemsCollection(
		int iteration,
		bool clearCollectionHandlers,
		List<object> retainedMenuItemsCollections,
		List<WeakReference<ShellContent>> ownerReferences,
		List<WeakReference<CollectionOwnerPayload>> payloadReferences,
		List<WeakReference<byte[]>> payloadBufferReferences)
	{
		var payload = new CollectionOwnerPayload($"customer-shell-content-{iteration}", new byte[PayloadBytes]);
		payload.Buffer[0] = (byte)iteration;

		var owner = new ShellContent
		{
			Title = $"Customer {iteration}",
			Content = new ContentPage
			{
				Title = $"Customer {iteration}",
				Content = new Label { Text = $"Customer record {iteration}" }
			},
			BindingContext = payload
		};

		var menuItems = owner.MenuItems;
		for (var i = 0; i < MenuItemsAddedThenRemovedPerOwner; i++)
		{
			menuItems.Add(new MenuItem
			{
				Text = $"Action {iteration}-{i}",
				CommandParameter = $"customer:{iteration}:action:{i}"
			});
		}

		while (menuItems.Count > 0)
			menuItems.RemoveAt(0);

		if (clearCollectionHandlers)
			ClearRetainingCollectionEvents(menuItems);

		retainedMenuItemsCollections.Add(menuItems);
		ownerReferences.Add(new WeakReference<ShellContent>(owner));
		payloadReferences.Add(new WeakReference<CollectionOwnerPayload>(payload));
		payloadBufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));

		owner = null!;
		payload = null!;
		menuItems = null!;
	}

	static void ClearRetainingCollectionEvents(object collection)
	{
		ClearEventFieldsRecursive(collection, new HashSet<object>(ReferenceEqualityComparer.Instance));
	}

	static void ClearEventFieldsRecursive(object value, HashSet<object> visited)
	{
		if (!visited.Add(value))
			return;

		ClearEventField(value, "CollectionChanged", typeof(NotifyCollectionChangedEventHandler));

		var type = value.GetType();
		while (type is not null)
		{
			foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
			{
				if (field.FieldType == typeof(string) || field.FieldType.IsValueType)
					continue;

				var nested = field.GetValue(value);
				if (nested is null)
					continue;

				if (nested is INotifyCollectionChanged)
					ClearEventFieldsRecursive(nested, visited);
			}

			type = type.BaseType;
		}
	}

	static void ClearEventField(object target, string eventName, Type eventHandlerType)
	{
		var type = target.GetType();
		while (type is not null)
		{
			var field = type.GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			if (field is not null && eventHandlerType.IsAssignableFrom(field.FieldType))
			{
				field.SetValue(target, null);
				return;
			}

			type = type.BaseType;
		}
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

	sealed class ReferenceEqualityComparer : IEqualityComparer<object>
	{
		public static readonly ReferenceEqualityComparer Instance = new();

		public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

		public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
	}

	public readonly record struct ScenarioResult(
		int OwnersAlive,
		int PayloadsAlive,
		int PayloadBuffersAlive,
		int RetainedMenuItemsCollections,
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
			builder.AppendLine("ShellContentMenuItemsCollectionRetentionRepro");
			builder.AppendLine($"ShellContent owners created: {Iterations}");
			builder.AppendLine($"MenuItems added then removed per owner: {MenuItemsAddedThenRemovedPerOwner}");
			builder.AppendLine($"Retained MenuItems collections per run: {Iterations}");
			builder.AppendLine($"Payload per discarded ShellContent: {PayloadBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Run: control: retained MenuItems collections after clearing nested collection event fields");
			AppendScenario(builder, Control);
			builder.AppendLine();
			builder.AppendLine("Run: current: retained MenuItems collections with MAUI collection event handlers intact");
			AppendScenario(builder, Current);
			builder.AppendLine($"  retained payload bytes: {retainedBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Leak path: app MenuItems collection cache -> MenuItemCollection._inner.CollectionChanged -> ShellContent.MenuItemsCollectionChanged -> discarded ShellContent -> BindingContext payload");
			builder.AppendLine("Distinct from ShellContent.MenuItems.Clear() reset leaks: menu items are removed individually before retaining the empty MenuItems collections.");
			builder.AppendLine($"dotnet-version: {Environment.Version}");
			return builder.ToString();
		}

		static void AppendScenario(StringBuilder builder, ScenarioResult result)
		{
			builder.AppendLine($"  MenuItems collections retained by app cache: {result.RetainedMenuItemsCollections}");
			builder.AppendLine($"  ShellContent owners alive after full GC: {result.OwnersAlive}/{Iterations}");
			builder.AppendLine($"  owner payloads alive after full GC: {result.PayloadsAlive}/{Iterations}");
			builder.AppendLine($"  owner payload buffers alive after full GC: {result.PayloadBuffersAlive}/{Iterations}");
			builder.AppendLine($"  managed heap delta: {result.HeapDelta / 1024d / 1024d:0.0} MiB");
		}
	}
}
