#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Android.Views;
using Google.Android.Material.AppBar;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using ControlsToolbar = Microsoft.Maui.Controls.Toolbar;

namespace AndroidToolbarMenuItemListenerRetentionLeakRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveHandlers,
	int AliveVirtualToolbars,
	int AliveToolbarItems,
	int AliveCommands,
	int AlivePayloads,
	long RetainedPayloadBytes);

public sealed record ReproReport(
	int Attempts,
	int PayloadBytes,
	RunStats Control,
	RunStats Current,
	long ManagedHeapBaseline,
	long ManagedHeapFinal)
{
	public bool LeakProved =>
		Control.AliveToolbarItems == 0 &&
		Control.AliveCommands == 0 &&
		Control.AlivePayloads == 0 &&
		Current.AliveToolbarItems == Attempts &&
		Current.AliveCommands == Attempts &&
		Current.AlivePayloads == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidToolbarMenuItemListenerRetentionLeakRepro",
			$"Attempts: {Attempts}",
			$"Payload per attempt: {PayloadBytes / 1024 / 1024} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Managed heap baseline: {FormatBytes(ManagedHeapBaseline)}",
			$"Managed heap final: {FormatBytes(ManagedHeapFinal)}",
			$"Managed heap delta: {FormatBytes(ManagedHeapFinal - ManagedHeapBaseline)}");
	}

	string Format(RunStats stats)
	{
		return string.Join(Environment.NewLine,
			$"Run: {stats.Name}",
			$"  retained native toolbars: {stats.Attempts}",
			$"  virtual toolbars alive after full GC: {stats.AliveVirtualToolbars}/{stats.Attempts}",
			$"  toolbar handlers alive after full GC: {stats.AliveHandlers}/{stats.Attempts}",
			$"  toolbar items alive after full GC: {stats.AliveToolbarItems}/{stats.Attempts}",
			$"  payload commands alive after full GC: {stats.AliveCommands}/{stats.Attempts}",
			$"  payloads alive after full GC: {stats.AlivePayloads}/{stats.Attempts}",
			$"  retained payload bytes: {FormatBytes(stats.RetainedPayloadBytes)} ({stats.RetainedPayloadBytes * 100.0 / (PayloadBytes * stats.Attempts):0.0}%)");
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

internal static class ReproSession
{
	const int Attempts = 80;
	const int PayloadBytes = 1024 * 1024;

	static readonly FieldInfo CurrentMenuItemsField =
		typeof(ControlsToolbar).GetField("_currentMenuItems", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(nameof(ControlsToolbar), "_currentMenuItems");

	static readonly FieldInfo CurrentToolbarItemsField =
		typeof(ControlsToolbar).GetField("_currentToolbarItems", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(nameof(ControlsToolbar), "_currentToolbarItems");

	public static async Task<ReproReport> RunAsync(IMauiContext mauiContext)
	{
		await Task.Yield();

		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			mauiContext,
			"control: clear native menu item listeners/items before disconnect",
			cleanupNativeMenu: true);

		var current = await RunScenarioAsync(
			mauiContext,
			"current: disconnect leaves native menu item listeners on retained toolbar",
			cleanupNativeMenu: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(IMauiContext mauiContext, string name, bool cleanupNativeMenu)
	{
		var retainedNativeToolbars = new List<MaterialToolbar>(Attempts);
		var handlerRefs = new List<WeakReference<ToolbarHandler>>(Attempts);
		var toolbarRefs = new List<WeakReference<ControlsToolbar>>(Attempts);
		var toolbarItemRefs = new List<WeakReference<ToolbarItem>>(Attempts);
		var commandRefs = new List<WeakReference<PayloadCommand>>(Attempts);
		var payloadRefs = new List<WeakReference<Payload>>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDisconnectedToolbar(
				mauiContext,
				cleanupNativeMenu,
				retainedNativeToolbars,
				handlerRefs,
				toolbarRefs,
				toolbarItemRefs,
				commandRefs,
				payloadRefs,
				i);

			if (i % 10 == 0)
				await Task.Yield();
		}

		ForceFullGc();
		GC.KeepAlive(retainedNativeToolbars);

		var aliveHandlers = handlerRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveToolbars = toolbarRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveToolbarItems = toolbarItemRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveCommands = commandRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloads = payloadRefs.Count(static wr => wr.TryGetTarget(out _));

		return new RunStats(
			name,
			Attempts,
			aliveHandlers,
			aliveToolbars,
			aliveToolbarItems,
			aliveCommands,
			alivePayloads,
			(long)alivePayloads * PayloadBytes);
	}

	static void CreateDisconnectedToolbar(
		IMauiContext mauiContext,
		bool cleanupNativeMenu,
		List<MaterialToolbar> retainedNativeToolbars,
		List<WeakReference<ToolbarHandler>> handlerRefs,
		List<WeakReference<ControlsToolbar>> toolbarRefs,
		List<WeakReference<ToolbarItem>> toolbarItemRefs,
		List<WeakReference<PayloadCommand>> commandRefs,
		List<WeakReference<Payload>> payloadRefs,
		int index)
	{
		var payload = new Payload(index, PayloadBytes);
		var command = new PayloadCommand(payload);
		var toolbarItem = new ToolbarItem
		{
			Text = $"Export order batch {index}",
			Order = ToolbarItemOrder.Primary,
			Command = command
		};

		payloadRefs.Add(new WeakReference<Payload>(payload));
		commandRefs.Add(new WeakReference<PayloadCommand>(command));
		toolbarItemRefs.Add(new WeakReference<ToolbarItem>(toolbarItem));

		var page = new ContentPage
		{
			Title = $"Toolbar page {index}"
		};

		var toolbar = new ControlsToolbar(page)
		{
			Title = $"Retained toolbar {index}",
			BackButtonVisible = false,
			IsVisible = true,
			ToolbarItems = new[] { toolbarItem }
		};
		toolbarRefs.Add(new WeakReference<ControlsToolbar>(toolbar));

		var handler = new ToolbarHandler();
		handler.SetMauiContext(mauiContext);
		handler.SetVirtualView(toolbar);
		handlerRefs.Add(new WeakReference<ToolbarHandler>(handler));

		var platformToolbar = handler.PlatformView;
		retainedNativeToolbars.Add(platformToolbar);

		ControlsToolbar.MapToolbarItems((IToolbarHandler)handler, toolbar);

		if (cleanupNativeMenu)
			ClearNativeMenuAndPrivateLists(platformToolbar, toolbar);

		((IElementHandler)handler).DisconnectHandler();
	}

	static void ClearNativeMenuAndPrivateLists(MaterialToolbar platformToolbar, ControlsToolbar toolbar)
	{
		var menu = platformToolbar.Menu;
		if (menu is not null)
		{
			for (var i = 0; i < menu.Size(); i++)
			{
				var item = menu.GetItem(i);
				item?.SetOnMenuItemClickListener(null);
			}

			menu.Clear();
		}

		if (CurrentMenuItemsField.GetValue(toolbar) is IList<IMenuItem> currentMenuItems)
		{
			foreach (var item in currentMenuItems)
			{
				item.SetOnMenuItemClickListener(null);
				item.Dispose();
			}

			currentMenuItems.Clear();
		}

		if (CurrentToolbarItemsField.GetValue(toolbar) is IList<ToolbarItem> currentToolbarItems)
			currentToolbarItems.Clear();
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
		}
	}

	sealed class PayloadCommand : System.Windows.Input.ICommand
	{
		public PayloadCommand(Payload payload)
		{
			Payload = payload;
		}

		public Payload Payload { get; }

		public event EventHandler? CanExecuteChanged;

		public bool CanExecute(object? parameter) => true;

		public void Execute(object? parameter)
		{
			Payload.Touch();
			CanExecuteChanged?.Invoke(this, EventArgs.Empty);
		}
	}

	sealed class Payload
	{
		public Payload(int id, int byteCount)
		{
			Id = id;
			Bytes = new byte[byteCount];
			Bytes[0] = (byte)(id % 251);
			Bytes[^1] = (byte)((id + 1) % 251);
		}

		public int Id { get; }

		public byte[] Bytes { get; }

		public void Touch()
		{
			Bytes[0] ^= 0x5a;
		}
	}
}
