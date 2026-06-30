using System.Collections.Specialized;
using System.Reflection;
using System.Text;
using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace KeyboardAcceleratorsCollectionRetentionRepro;

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
			Text = "Running KeyboardAccelerators collection retention repro...",
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
			var text = "KeyboardAcceleratorsCollectionRetentionRepro ERROR" + Environment.NewLine + ex;
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(1);
		}
	}
}

static class ReproSession
{
	public const string ResultsPath = "/tmp/keyboardaccelerators-collection-retention-results.txt";

	const int Iterations = 160;
	const int AcceleratorsPerMenuItem = 3;
	const int PayloadBytes = 1024 * 1024;

	public static ReproReport Run()
	{
		var control = RunScenario(clearKeyboardAcceleratorHandlers: true);
		var current = RunScenario(clearKeyboardAcceleratorHandlers: false);

		return new ReproReport(control, current);
	}

	static ScenarioResult RunScenario(bool clearKeyboardAcceleratorHandlers)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
		var retainedAcceleratorCollections = new List<IList<KeyboardAccelerator>>(Iterations);
		var menuItemReferences = new List<WeakReference<MenuFlyoutItem>>(Iterations);
		var payloadReferences = new List<WeakReference<MenuItemPayload>>(Iterations);
		var payloadBufferReferences = new List<WeakReference<byte[]>>(Iterations);

		for (var i = 0; i < Iterations; i++)
		{
			CreateRetainedAcceleratorCollection(clearKeyboardAcceleratorHandlers, i, retainedAcceleratorCollections, menuItemReferences, payloadReferences, payloadBufferReferences);
		}

		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			CountAlive(menuItemReferences),
			CountAlive(payloadReferences),
			CountAlive(payloadBufferReferences),
			retainedAcceleratorCollections.Count,
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedAcceleratorCollections);
		return result;
	}

	static void CreateRetainedAcceleratorCollection(
		bool clearKeyboardAcceleratorHandlers,
		int iteration,
		List<IList<KeyboardAccelerator>> retainedAcceleratorCollections,
		List<WeakReference<MenuFlyoutItem>> menuItemReferences,
		List<WeakReference<MenuItemPayload>> payloadReferences,
		List<WeakReference<byte[]>> payloadBufferReferences)
	{
		var payload = new MenuItemPayload($"menu-item-{iteration}", new byte[PayloadBytes]);
		payload.Buffer[0] = (byte)iteration;

		var menuItem = new MenuFlyoutItem
		{
			Text = $"Command {iteration}",
			BindingContext = payload
		};

		var keyboardAccelerators = menuItem.KeyboardAccelerators;
		for (var accelerator = 0; accelerator < AcceleratorsPerMenuItem; accelerator++)
		{
			keyboardAccelerators.Add(new KeyboardAccelerator
			{
				Key = ((char)('A' + accelerator)).ToString(),
				Modifiers = KeyboardAcceleratorModifiers.Cmd | KeyboardAcceleratorModifiers.Shift
			});
		}

		if (clearKeyboardAcceleratorHandlers)
			ClearCollectionChangedHandlers(keyboardAccelerators);

		retainedAcceleratorCollections.Add(keyboardAccelerators);
		menuItemReferences.Add(new WeakReference<MenuFlyoutItem>(menuItem));
		payloadReferences.Add(new WeakReference<MenuItemPayload>(payload));
		payloadBufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));
	}

	static void ClearCollectionChangedHandlers(IList<KeyboardAccelerator> keyboardAccelerators)
	{
		if (keyboardAccelerators is not INotifyCollectionChanged)
			throw new InvalidOperationException($"Expected {keyboardAccelerators.GetType().FullName} to implement INotifyCollectionChanged.");

		var type = keyboardAccelerators.GetType();
		while (type is not null)
		{
			var field = type.GetField("CollectionChanged", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			if (field is not null && typeof(NotifyCollectionChangedEventHandler).IsAssignableFrom(field.FieldType))
			{
				field.SetValue(keyboardAccelerators, null);
				return;
			}

			type = type.BaseType;
		}

		throw new InvalidOperationException($"Could not find the CollectionChanged backing field on {keyboardAccelerators.GetType().FullName}.");
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

	sealed class MenuItemPayload
	{
		public MenuItemPayload(string name, byte[] buffer)
		{
			Name = name;
			Buffer = buffer;
		}

		public string Name { get; }

		public byte[] Buffer { get; }
	}

	public readonly record struct ScenarioResult(
		int MenuItemsAlive,
		int PayloadsAlive,
		int PayloadBuffersAlive,
		int RetainedAcceleratorCollections,
		long HeapBefore,
		long HeapAfter)
	{
		public long HeapDelta => HeapAfter - HeapBefore;
	}

	public readonly record struct ReproReport(ScenarioResult Control, ScenarioResult Current)
	{
		public bool LeakProved =>
			Control.MenuItemsAlive == 0 &&
			Control.PayloadsAlive == 0 &&
			Control.PayloadBuffersAlive == 0 &&
			Current.MenuItemsAlive == Iterations &&
			Current.PayloadsAlive == Iterations &&
			Current.PayloadBuffersAlive == Iterations;

		public string ToText()
		{
			var retainedBytes = Current.PayloadBuffersAlive * PayloadBytes;
			var builder = new StringBuilder();
			builder.AppendLine(LeakProved ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			builder.AppendLine("KeyboardAcceleratorsCollectionRetentionRepro");
			builder.AppendLine($"Menu items: {Iterations}");
			builder.AppendLine($"Keyboard accelerators per item: {AcceleratorsPerMenuItem}");
			builder.AppendLine($"Retained accelerator collections per run: {Iterations}");
			builder.AppendLine($"Payload per discarded menu item: {PayloadBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Run: control: retained accelerator collections after clearing MAUI CollectionChanged handlers");
			AppendScenario(builder, Control);
			builder.AppendLine();
			builder.AppendLine("Run: current: retained accelerator collections with MAUI CollectionChanged handlers intact");
			AppendScenario(builder, Current);
			builder.AppendLine($"  retained payload bytes: {retainedBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Leak path: app accelerator collection cache -> MenuFlyoutItem.KeyboardAccelerators ObservableCollection -> anonymous CollectionChanged handler -> MenuFlyoutItem -> BindingContext payload");
			builder.AppendLine($"dotnet-version: {Environment.Version}");
			return builder.ToString();
		}

		static void AppendScenario(StringBuilder builder, ScenarioResult result)
		{
			builder.AppendLine($"  accelerator collections retained by app cache: {result.RetainedAcceleratorCollections}");
			builder.AppendLine($"  menu items alive after full GC: {result.MenuItemsAlive}/{Iterations}");
			builder.AppendLine($"  menu item payloads alive after full GC: {result.PayloadsAlive}/{Iterations}");
			builder.AppendLine($"  menu item payload buffers alive after full GC: {result.PayloadBuffersAlive}/{Iterations}");
			builder.AppendLine($"  managed heap delta: {result.HeapDelta / 1024d / 1024d:0.0} MiB");
		}
	}
}
