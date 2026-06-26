#nullable enable

using System.Collections;
using System.Reflection;
using System.Text;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Storage;

namespace MenuFlyoutStaticMenuLeakRepro;

public sealed class MainPage : ContentPage
{
	const int Iterations = 80;
	const int PayloadBytes = 1024 * 1024;

	readonly Label _status;

	public MainPage()
	{
		_status = new Label
		{
			Text = "Running iOS menu flyout static menu leak repro...",
			Margin = 24
		};

		Content = _status;
		Loaded += OnLoaded;
	}

	async void OnLoaded(object? sender, EventArgs e)
	{
		Loaded -= OnLoaded;

		ReproResult result;

		try
		{
			result = await RunScenariosAsync();
		}
		catch (Exception ex)
		{
			var failure = "RESULT: ERROR" + Environment.NewLine + ex;
			_status.Text = failure;
			await WriteResultsAsync(failure);
			await Task.Delay(250);
			Environment.Exit(3);
			return;
		}

		var text = result.ToString();
		_status.Text = text;
		await WriteResultsAsync(text);
		await Task.Delay(250);
		Environment.Exit(result.IsProven ? 0 : 2);
	}

	async Task<ReproResult> RunScenariosAsync()
	{
		if (Handler?.MauiContext is not IMauiContext mauiContext)
			throw new InvalidOperationException("The page does not have a MauiContext.");

		StaticMenuStore.Clear();
		var before = StaticMenuStore.Count;
		var control = await RunUnconvertedControlAsync();
		var afterControl = StaticMenuStore.Count;
		var leak = await RunConvertedMenuItemScenarioAsync(mauiContext);
		var afterLeak = StaticMenuStore.Count;
		StaticMenuStore.Clear();
		var afterReset = StaticMenuStore.Count;
		await WaitAndCollectAsync();

		return new ReproResult(before, control, afterControl, leak, afterLeak, afterReset);
	}

	static async Task<ScenarioResult> RunUnconvertedControlAsync()
	{
		var payloadRefs = new List<WeakReference>();
		var itemRefs = new List<WeakReference>();

		await MainThread.InvokeOnMainThreadAsync(() =>
		{
			for (var i = 0; i < Iterations; i++)
			{
				CreateUnconvertedMenuItem(payloadRefs, itemRefs, i);
			}
		});

		await WaitAndCollectAsync();

		return new ScenarioResult("unconverted-control", CountAlive(payloadRefs), CountAlive(itemRefs), Iterations);
	}

	static async Task<ScenarioResult> RunConvertedMenuItemScenarioAsync(IMauiContext mauiContext)
	{
		var payloadRefs = new List<WeakReference>();
		var itemRefs = new List<WeakReference>();

		await MainThread.InvokeOnMainThreadAsync(() =>
		{
			for (var i = 0; i < Iterations; i++)
			{
				CreateConvertedMenuItem(mauiContext, payloadRefs, itemRefs, i);
			}
		});

		await WaitAndCollectAsync();

		return new ScenarioResult("converted-menu-command", CountAlive(payloadRefs), CountAlive(itemRefs), Iterations);
	}

	static void CreateUnconvertedMenuItem(List<WeakReference> payloadRefs, List<WeakReference> itemRefs, int index)
	{
		var payload = new Payload(PayloadBytes);
		var item = CreateItem(payload, index);

		payloadRefs.Add(new WeakReference(payload));
		itemRefs.Add(new WeakReference(item));
	}

	static void CreateConvertedMenuItem(
		IMauiContext mauiContext,
		List<WeakReference> payloadRefs,
		List<WeakReference> itemRefs,
		int index)
	{
		var payload = new Payload(PayloadBytes);
		var item = CreateItem(payload, index);
		var handler = new MenuFlyoutItemHandler();

		handler.SetMauiContext(mauiContext);
		handler.SetVirtualView(item);
		((IElementHandler)handler).DisconnectHandler();

		payloadRefs.Add(new WeakReference(payload));
		itemRefs.Add(new WeakReference(item));
	}

	static MenuFlyoutItem CreateItem(Payload payload, int index)
	{
		var item = new MenuFlyoutItem
		{
			Text = "Leaky menu item " + index,
			CommandParameter = payload,
			BindingContext = payload
		};

		item.Clicked += (_, _) => payload.Touch();

		return item;
	}

	static async Task WaitAndCollectAsync()
	{
		await Task.Delay(250);
		await Task.Run(ForceGc);
		await Task.Delay(100);
		await Task.Run(ForceGc);
	}

	static void ForceGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(100);
		}
	}

	static int CountAlive(List<WeakReference> refs)
	{
		var count = 0;

		foreach (var reference in refs)
		{
			if (reference.IsAlive)
				count++;
		}

		return count;
	}

	static async Task WriteResultsAsync(string text)
	{
		var paths = new[]
		{
			Path.Combine(FileSystem.AppDataDirectory, "autorun-results.txt"),
			Path.Combine(Path.GetTempPath(), "menuflyoutstaticmenuleakrepro-results.txt")
		};

		foreach (var path in paths)
		{
			try
			{
				var directory = Path.GetDirectoryName(path);
				if (!string.IsNullOrWhiteSpace(directory))
					Directory.CreateDirectory(directory);

				await File.WriteAllTextAsync(path, text);
			}
			catch (Exception ex)
			{
				Console.WriteLine(ex);
			}
		}

		Console.WriteLine(text);
	}

	sealed class Payload
	{
		readonly byte[] _data;
		int _ticks;

		public Payload(int bytes)
		{
			_data = new byte[bytes];
			_data[0] = 123;
		}

		public void Touch()
		{
			_ticks++;
			if (_ticks == int.MaxValue)
				_ticks = _data[0];
		}
	}

	static class StaticMenuStore
	{
		static readonly FieldInfo MenusField =
			typeof(MenuFlyoutItemHandler).GetField("menus", BindingFlags.NonPublic | BindingFlags.Static)
			?? throw new InvalidOperationException("Missing MenuFlyoutItemHandler.menus.");

		public static int Count
		{
			get
			{
				var dictionary = GetDictionary();
				return dictionary.Count;
			}
		}

		public static void Clear()
		{
			GetDictionary().Clear();
		}

		static IDictionary GetDictionary()
		{
			return MenusField.GetValue(null) as IDictionary
				?? throw new InvalidOperationException("MenuFlyoutItemHandler.menus was null.");
		}
	}

	readonly record struct ScenarioResult(string Name, int PayloadsAlive, int ItemsAlive, int Total)
	{
		public override string ToString()
		{
			var builder = new StringBuilder();
			builder.Append(Name);
			builder.Append(": payloads=");
			builder.Append(PayloadsAlive);
			builder.Append('/');
			builder.Append(Total);
			builder.Append(", items=");
			builder.Append(ItemsAlive);
			builder.Append('/');
			builder.Append(Total);
			return builder.ToString();
		}
	}

	readonly record struct ReproResult(
		int Before,
		ScenarioResult Control,
		int AfterControl,
		ScenarioResult Leak,
		int AfterLeak,
		int AfterReset)
	{
		static int LeakThreshold => Iterations / 2;

		int StaticDelta => AfterLeak - AfterControl;

		public bool IsProven =>
			Before == 0 &&
			Control.PayloadsAlive == 0 &&
			Control.ItemsAlive == 0 &&
			AfterControl == 0 &&
			Leak.PayloadsAlive >= LeakThreshold &&
			Leak.ItemsAlive >= LeakThreshold &&
			StaticDelta >= LeakThreshold &&
			AfterReset == 0;

		public override string ToString()
		{
			var builder = new StringBuilder();
			builder.AppendLine(IsProven ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			builder.Append("before-static-menu-count=");
			builder.Append(Before);
			builder.AppendLine();
			builder.AppendLine(Control.ToString());
			builder.Append("after-control-static-menu-count=");
			builder.Append(AfterControl);
			builder.AppendLine();
			builder.AppendLine(Leak.ToString());
			builder.Append("after-leak-static-menu-count=");
			builder.Append(AfterLeak);
			builder.AppendLine();
			builder.Append("static-menu-delta=");
			builder.Append(StaticDelta);
			builder.AppendLine();
			builder.Append("after-reset-static-menu-count=");
			builder.Append(AfterReset);
			builder.AppendLine();
			builder.Append("payload-bytes-per-leak-scenario=");
			builder.Append(Iterations * PayloadBytes);
			builder.AppendLine();
			builder.Append("app-data-directory=");
			builder.Append(FileSystem.AppDataDirectory);
			builder.AppendLine();
			builder.Append("dotnet-version=");
			builder.Append(Environment.Version);
			return builder.ToString();
		}
	}
}
