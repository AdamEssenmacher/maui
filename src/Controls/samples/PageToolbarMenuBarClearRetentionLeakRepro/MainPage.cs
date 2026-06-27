#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Foundation;
using Microsoft.Maui.Controls;

namespace PageToolbarMenuBarClearRetentionLeakRepro;

public sealed class MainPage : ContentPage
{
	const int ItemsPerKind = 80;
	const int PayloadBytes = 1024 * 1024;

	readonly string? _resultsPath;
	readonly Label _status;

	public MainPage(string? resultsPath)
	{
		_resultsPath = resultsPath;
		_status = new Label
		{
			Text = "Running page toolbar/menu bar clear retention leak repro...",
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
		var control = await RunScenarioAsync("control: remove Page toolbar/menu items individually", clearCollections: false);
		var current = await RunScenarioAsync("current: Page toolbar/menu item Clear leaves parent hooks", clearCollections: true);

		return new ReproResult(ItemsPerKind, PayloadBytes, control, current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(string name, bool clearCollections)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		var retainedPages = new List<ContentPage>(ItemsPerKind);
		var toolbarItemReferences = new List<WeakReference<ToolbarItem>>(ItemsPerKind);
		var menuBarItemReferences = new List<WeakReference<MenuBarItem>>(ItemsPerKind);
		var toolbarPayloadReferences = new List<WeakReference<Payload>>(ItemsPerKind);
		var menuBarPayloadReferences = new List<WeakReference<Payload>>(ItemsPerKind);
		var toolbarBufferReferences = new List<WeakReference<byte[]>>(ItemsPerKind);
		var menuBarBufferReferences = new List<WeakReference<byte[]>>(ItemsPerKind);

		for (var i = 0; i < ItemsPerKind; i++)
		{
			using (new NSAutoreleasePool())
			{
				var page = new ContentPage
				{
					Title = "Live page " + i,
					Content = new Label { Text = "Live page " + i }
				};
				var toolbarPayload = new Payload(i, PayloadBytes);
				var menuBarPayload = new Payload(i + ItemsPerKind, PayloadBytes);
				var toolbarItem = new ToolbarItem
				{
					Text = "Removed toolbar item " + i,
					BindingContext = toolbarPayload
				};
				var menuBarItem = new MenuBarItem
				{
					Text = "Removed menu bar item " + i,
					BindingContext = menuBarPayload
				};

				page.ToolbarItems.Add(toolbarItem);
				page.MenuBarItems.Add(menuBarItem);

				if (clearCollections)
				{
					page.ToolbarItems.Clear();
					page.MenuBarItems.Clear();
				}
				else
				{
					page.ToolbarItems.RemoveAt(0);
					page.MenuBarItems.RemoveAt(0);
				}

				retainedPages.Add(page);
				toolbarItemReferences.Add(new WeakReference<ToolbarItem>(toolbarItem));
				menuBarItemReferences.Add(new WeakReference<MenuBarItem>(menuBarItem));
				toolbarPayloadReferences.Add(new WeakReference<Payload>(toolbarPayload));
				menuBarPayloadReferences.Add(new WeakReference<Payload>(menuBarPayload));
				toolbarBufferReferences.Add(new WeakReference<byte[]>(toolbarPayload.Buffer));
				menuBarBufferReferences.Add(new WeakReference<byte[]>(menuBarPayload.Buffer));

				page = null!;
				toolbarItem = null!;
				menuBarItem = null!;
				toolbarPayload = null!;
				menuBarPayload = null!;
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
			retainedPages.Count,
			CountAlive(toolbarItemReferences),
			CountAlive(menuBarItemReferences),
			CountAlive(toolbarPayloadReferences),
			CountAlive(menuBarPayloadReferences),
			CountAlive(toolbarBufferReferences),
			CountAlive(menuBarBufferReferences),
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedPages);
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
}

public sealed record ReproResult(
	int ItemsPerKind,
	int PayloadBytes,
	ScenarioResult Control,
	ScenarioResult Current)
{
	public bool LeakProved =>
		Control.LivePages == ItemsPerKind &&
		Control.AliveToolbarItems == 0 &&
		Control.AliveMenuBarItems == 0 &&
		Control.AliveToolbarPayloads == 0 &&
		Control.AliveMenuBarPayloads == 0 &&
		Control.AliveToolbarPayloadBuffers == 0 &&
		Control.AliveMenuBarPayloadBuffers == 0 &&
		Current.LivePages == ItemsPerKind &&
		Current.AliveToolbarItems == ItemsPerKind &&
		Current.AliveMenuBarItems == ItemsPerKind &&
		Current.AliveToolbarPayloads == ItemsPerKind &&
		Current.AliveMenuBarPayloads == ItemsPerKind &&
		Current.AliveToolbarPayloadBuffers == ItemsPerKind &&
		Current.AliveMenuBarPayloadBuffers == ItemsPerKind;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"PageToolbarMenuBarClearRetentionLeakRepro",
			$"Items per kind: {ItemsPerKind}",
			$"Payload per item: {PayloadBytes / 1024 / 1024} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Control.ToText(ItemsPerKind, PayloadBytes),
			string.Empty,
			Current.ToText(ItemsPerKind, PayloadBytes));
	}
}

public sealed record ScenarioResult(
	string Name,
	int LivePages,
	int AliveToolbarItems,
	int AliveMenuBarItems,
	int AliveToolbarPayloads,
	int AliveMenuBarPayloads,
	int AliveToolbarPayloadBuffers,
	int AliveMenuBarPayloadBuffers,
	long HeapBefore,
	long HeapAfter)
{
	public string ToText(int itemsPerKind, int payloadBytes)
	{
		var retainedPayloadBytes = (long)(AliveToolbarPayloadBuffers + AliveMenuBarPayloadBuffers) * payloadBytes;
		var totalPayloadBytes = (long)itemsPerKind * 2 * payloadBytes;

		return string.Join(Environment.NewLine,
			$"Run: {Name}",
			$"  live pages intentionally retained: {LivePages}/{itemsPerKind}",
			$"  removed toolbar items alive after full GC: {AliveToolbarItems}/{itemsPerKind}",
			$"  removed menu bar items alive after full GC: {AliveMenuBarItems}/{itemsPerKind}",
			$"  toolbar payloads alive after full GC: {AliveToolbarPayloads}/{itemsPerKind}",
			$"  menu bar payloads alive after full GC: {AliveMenuBarPayloads}/{itemsPerKind}",
			$"  toolbar payload byte arrays alive after full GC: {AliveToolbarPayloadBuffers}/{itemsPerKind}",
			$"  menu bar payload byte arrays alive after full GC: {AliveMenuBarPayloadBuffers}/{itemsPerKind}",
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
