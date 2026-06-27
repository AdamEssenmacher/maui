#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using Foundation;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;

namespace MenuBarChildHandlerRetentionLeakRepro;

public sealed class MainPage : ContentPage
{
	const int Iterations = 80;
	const int PayloadBytes = 1024 * 1024;

	readonly string? _resultsPath;
	readonly Label _status;

	public MainPage(string? resultsPath)
	{
		_resultsPath = resultsPath;
		_status = new Label
		{
			Text = "Running MenuBar child handler retention leak repro...",
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
		StaticMenuStore.Clear();
		var staticBefore = StaticMenuStore.Count;
		var control = await RunScenarioAsync("control: disconnect removed MenuBar child handlers", disconnectRemovedHandler: true);
		var staticAfterControl = StaticMenuStore.Count;
		var current = await RunScenarioAsync("current: MenuBarItem removed children keep handlers", disconnectRemovedHandler: false);
		var staticAfterCurrent = StaticMenuStore.Count;
		StaticMenuStore.Clear();

		return new ReproResult(
			Iterations,
			PayloadBytes,
			staticBefore,
			staticAfterControl,
			staticAfterCurrent,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(string name, bool disconnectRemovedHandler)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		var retainedRemovedItems = new List<MenuFlyoutSubItem>(Iterations);
		var handlerReferences = new List<WeakReference<MenuFlyoutSubItemHandler>>(Iterations);
		var contextReferences = new List<WeakReference<MauiContext>>(Iterations);
		var payloadReferences = new List<WeakReference<Payload>>(Iterations);
		var payloadBufferReferences = new List<WeakReference<byte[]>>(Iterations);

		for (var i = 0; i < Iterations; i++)
		{
			using (new NSAutoreleasePool())
			{
				var payload = new Payload(i, PayloadBytes);
				var serviceProvider = new PayloadServiceProvider(payload);
				var mauiContext = new MauiContext(serviceProvider);
				var rootMenu = new MenuBarItem
				{
					Text = "Cached menu root " + i
				};
				var removedSubItem = new MenuFlyoutSubItem
				{
					Text = "Cached removed submenu " + i
				};
				var handler = new MenuFlyoutSubItemHandler();

				rootMenu.Add(removedSubItem);
				handler.SetMauiContext(mauiContext);
				handler.SetVirtualView(removedSubItem);
				retainedRemovedItems.Add(removedSubItem);
				rootMenu.Clear();

				if (disconnectRemovedHandler)
					removedSubItem.Handler?.DisconnectHandler();

				handlerReferences.Add(new WeakReference<MenuFlyoutSubItemHandler>(handler));
				contextReferences.Add(new WeakReference<MauiContext>(mauiContext));
				payloadReferences.Add(new WeakReference<Payload>(payload));
				payloadBufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));

				handler = null!;
				removedSubItem = null!;
				rootMenu = null!;
				mauiContext = null!;
				serviceProvider = null!;
				payload = null!;
			}

			if (i % 10 == 0)
				await Task.Yield();
		}

		await Task.Delay(250);
		ForceGc();
		await Task.Delay(250);
		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			name,
			CountAlive(handlerReferences),
			CountAlive(contextReferences),
			CountAlive(payloadReferences),
			CountAlive(payloadBufferReferences),
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedRemovedItems);
		return result;
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

	sealed class PayloadServiceProvider : IServiceProvider
	{
		readonly Payload _payload;

		public PayloadServiceProvider(Payload payload)
		{
			_payload = payload;
		}

		public object? GetService(Type serviceType)
		{
			if (serviceType == typeof(Payload))
				return _payload;

			return null;
		}
	}

	static class StaticMenuStore
	{
		static readonly FieldInfo MenusField =
			typeof(MenuFlyoutItemHandler).GetField("menus", BindingFlags.NonPublic | BindingFlags.Static)
			?? throw new InvalidOperationException("Missing MenuFlyoutItemHandler.menus.");

		public static int Count => GetDictionary().Count;

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
}

public sealed record ReproResult(
	int Iterations,
	int PayloadBytes,
	int StaticMenuCountBefore,
	int StaticMenuCountAfterControl,
	int StaticMenuCountAfterCurrent,
	ScenarioResult Control,
	ScenarioResult Current)
{
	public bool LeakProved =>
		StaticMenuCountBefore == 0 &&
		StaticMenuCountAfterControl == 0 &&
		StaticMenuCountAfterCurrent == 0 &&
		Control.AliveHandlers == 0 &&
		Control.AliveContexts == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AlivePayloadBuffers == 0 &&
		Current.AliveHandlers == Iterations &&
		Current.AliveContexts == Iterations &&
		Current.AlivePayloads == Iterations &&
		Current.AlivePayloadBuffers == Iterations;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"MenuBarChildHandlerRetentionLeakRepro",
			$"Iterations: {Iterations}",
			$"Payload per iteration: {PayloadBytes / 1024 / 1024} MiB",
			$"Leak proved: {LeakProved}",
			$"Static iOS menu dictionary count before/control/current: {StaticMenuCountBefore}/{StaticMenuCountAfterControl}/{StaticMenuCountAfterCurrent}",
			string.Empty,
			Control.ToText(Iterations, PayloadBytes),
			string.Empty,
			Current.ToText(Iterations, PayloadBytes));
	}
}

public sealed record ScenarioResult(
	string Name,
	int AliveHandlers,
	int AliveContexts,
	int AlivePayloads,
	int AlivePayloadBuffers,
	long HeapBefore,
	long HeapAfter)
{
	public string ToText(int iterations, int payloadBytes)
	{
		var retainedPayloadBytes = (long)AlivePayloadBuffers * payloadBytes;

		return string.Join(Environment.NewLine,
			$"Run: {Name}",
			$"  removed sub-item handlers alive after full GC: {AliveHandlers}/{iterations}",
			$"  MauiContexts alive after full GC: {AliveContexts}/{iterations}",
			$"  payloads alive after full GC: {AlivePayloads}/{iterations}",
			$"  payload byte arrays alive after full GC: {AlivePayloadBuffers}/{iterations}",
			$"  retained payload bytes: {FormatBytes(retainedPayloadBytes)} ({retainedPayloadBytes * 100.0 / (payloadBytes * iterations):0.0}%)",
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
