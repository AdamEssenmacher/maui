#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using Google.Android.Material.AppBar;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using ControlsToolbar = Microsoft.Maui.Controls.Toolbar;

namespace AndroidToolbarMenuItemIconRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 96;
	const int IconWidth = 512;
	const int IconHeight = 512;
	const int BytesPerPixel = 4;
	const int PayloadBytesPerIcon = IconWidth * IconHeight * BytesPerPixel;

	static readonly List<MaterialToolbar> RetainedNativeToolbars = new();

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			"control: clear native menu-item icon before disconnect",
			context,
			clearNativeIcon: true);

		var current = await RunScenarioAsync(
			"current: MAUI disconnect leaves native menu-item icon assigned",
			context,
			clearNativeIcon: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeToolbars);

		return new ReproReport(
			Cycles,
			IconWidth,
			IconHeight,
			PayloadBytesPerIcon,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearNativeIcon)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateCycle(context, i, tracked, clearNativeIcon);

			if (i % 16 == 0)
				await Task.Yield();
		}

		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	static void CreateCycle(
		IMauiContext context,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeIcon)
	{
		var toolbarItem = new ToolbarItem
		{
			Text = $"Icon {cycle:D4}",
			AutomationId = $"icon-{cycle:D4}",
			IconImageSource = new PayloadImageSource(cycle),
			Order = ToolbarItemOrder.Primary
		};

		var page = new ContentPage
		{
			Title = $"Toolbar page {cycle:D4}"
		};

		var toolbar = new ControlsToolbar(page)
		{
			Title = $"Retained toolbar {cycle:D4}",
			BackButtonVisible = false,
			IsVisible = true,
			ToolbarItems = new[] { toolbarItem }
		};

		var handler = new ToolbarHandler();
		handler.SetMauiContext(context);
		handler.SetVirtualView(toolbar);

		ControlsToolbar.MapToolbarItems((IToolbarHandler)handler, toolbar);

		var platformToolbar = handler.PlatformView;
		WaitForMenuIcon(platformToolbar);
		ClearNativeClickListeners(platformToolbar);

		if (clearNativeIcon)
			ClearNativeIcon(platformToolbar);

		((IElementHandler)handler).DisconnectHandler();

		RetainedNativeToolbars.Add(platformToolbar);
		tracked.Add(TrackedCycle.Create(cycle, platformToolbar, toolbar, toolbarItem, handler));
	}

	static void WaitForMenuIcon(MaterialToolbar platformToolbar)
	{
		for (var i = 0; i < 20; i++)
		{
			if (GetFirstMenuItem(platformToolbar)?.Icon is not null)
				return;

			Thread.Sleep(25);
		}
	}

	static void ClearNativeClickListeners(MaterialToolbar platformToolbar)
	{
		var menu = platformToolbar.Menu;
		if (menu is null)
			return;

		for (var i = 0; i < menu.Size(); i++)
			menu.GetItem(i)?.SetOnMenuItemClickListener(null);
	}

	static void ClearNativeIcon(MaterialToolbar platformToolbar)
	{
		var menu = platformToolbar.Menu;
		if (menu is null)
			return;

		for (var i = 0; i < menu.Size(); i++)
			menu.GetItem(i)?.SetIcon(null);
	}

	static IMenuItem? GetFirstMenuItem(MaterialToolbar platformToolbar)
	{
		var menu = platformToolbar.Menu;
		if (menu is null || menu.Size() == 0)
			return null;

		return menu.GetItem(0);
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Java.Lang.JavaSystem.Gc();
			Thread.Sleep(100);
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<MaterialToolbar> NativeToolbar,
		WeakReference<object> VirtualToolbar,
		WeakReference<ToolbarItem> ToolbarItem,
		WeakReference<ToolbarHandler> Handler)
	{
		public static TrackedCycle Create(
			int cycle,
			MaterialToolbar nativeToolbar,
			object virtualToolbar,
			ToolbarItem toolbarItem,
			ToolbarHandler handler)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<MaterialToolbar>(nativeToolbar),
				new WeakReference<object>(virtualToolbar),
				new WeakReference<ToolbarItem>(toolbarItem),
				new WeakReference<ToolbarHandler>(handler));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeToolbars,
		int AliveVirtualToolbars,
		int AliveToolbarItems,
		int AliveHandlers,
		int AliveMenuItems,
		int AssignedIconSlots,
		int PayloadSizedIconSlots,
		long RetainedNativeIconBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativeToolbars = 0;
			var aliveVirtualToolbars = 0;
			var aliveToolbarItems = 0;
			var aliveHandlers = 0;
			var aliveMenuItems = 0;
			var assignedIconSlots = 0;
			var payloadSizedIconSlots = 0;
			long retainedNativeIconBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativeToolbar.TryGetTarget(out var nativeToolbar))
				{
					aliveNativeToolbars++;
					var item = GetFirstMenuItem(nativeToolbar);
					if (item is not null)
					{
						aliveMenuItems++;
						var iconBytes = GetIconByteCount(item.Icon);

						if (iconBytes > 0)
							assignedIconSlots++;
						if (iconBytes >= PayloadBytesPerIcon)
							payloadSizedIconSlots++;

						retainedNativeIconBytes += iconBytes;
					}
				}

				if (cycle.VirtualToolbar.TryGetTarget(out _))
					aliveVirtualToolbars++;

				if (cycle.ToolbarItem.TryGetTarget(out _))
					aliveToolbarItems++;

				if (cycle.Handler.TryGetTarget(out _))
					aliveHandlers++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeToolbars,
				aliveVirtualToolbars,
				aliveToolbarItems,
				aliveHandlers,
				aliveMenuItems,
				assignedIconSlots,
				payloadSizedIconSlots,
				retainedNativeIconBytes);
		}

		static long GetIconByteCount(Drawable? icon)
		{
			if (icon is BitmapDrawable bitmapDrawable && bitmapDrawable.Bitmap is Bitmap bitmap)
				return bitmap.AllocationByteCount;

			return icon is null ? 0 : PayloadBytesPerIcon;
		}
	}
}

internal sealed class PayloadImageSource : ImageSource
{
	public PayloadImageSource(int cycle)
	{
		Cycle = cycle;
	}

	public int Cycle { get; }
}

internal sealed class PayloadImageSourceService : ImageSourceService, IImageSourceService<PayloadImageSource>
{
	public override Task<IImageSourceServiceResult<Drawable>?> GetDrawableAsync(
		IImageSource imageSource,
		Context context,
		CancellationToken cancellationToken = default)
	{
		if (imageSource is not PayloadImageSource payloadSource)
			return Task.FromResult<IImageSourceServiceResult<Drawable>?>(null);

		var bitmap = Bitmap.CreateBitmap(ReproSessionReport.IconWidth, ReproSessionReport.IconHeight, Bitmap.Config.Argb8888!);
		var color = Color.Argb(
			255,
			(payloadSource.Cycle * 37) % 255,
			(payloadSource.Cycle * 67) % 255,
			(payloadSource.Cycle * 97) % 255);
		bitmap.EraseColor(color);

		Drawable drawable = new BitmapDrawable(context.Resources, bitmap);
		return Task.FromResult<IImageSourceServiceResult<Drawable>?>(new ImageSourceServiceResult(drawable));
	}
}

static class ReproSessionReport
{
	public const int IconWidth = 512;
	public const int IconHeight = 512;
}

internal sealed record ReproReport(
	int Cycles,
	int IconWidth,
	int IconHeight,
	int PayloadBytesPerIcon,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.AliveNativeToolbars == Cycles &&
		Current.AliveNativeToolbars == Cycles &&
		Control.PayloadSizedIconSlots == 0 &&
		Current.PayloadSizedIconSlots == Cycles &&
		Current.AliveVirtualToolbars == 0 &&
		Current.AliveToolbarItems == 0 &&
		Current.AliveHandlers == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidToolbarMenuItemIconRetentionRepro",
			$"Cycles: {Cycles}",
			$"Icon size: {IconWidth}x{IconHeight}",
			$"Payload bytes per native icon: {PayloadBytesPerIcon:N0}",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained native icon payload: {FormatBytes(Control.RetainedNativeIconBytes)}",
			$"Current retained native icon payload: {FormatBytes(Current.RetainedNativeIconBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  alive native toolbars: {result.AliveNativeToolbars}/{result.TrackedCycles}",
			$"  alive virtual toolbars: {result.AliveVirtualToolbars}/{result.TrackedCycles}",
			$"  alive toolbar items: {result.AliveToolbarItems}/{result.TrackedCycles}",
			$"  alive toolbar handlers: {result.AliveHandlers}/{result.TrackedCycles}",
			$"  alive native menu items: {result.AliveMenuItems}/{result.TrackedCycles}",
			$"  assigned native icon slots: {result.AssignedIconSlots}/{result.TrackedCycles}",
			$"  payload-sized native icon slots: {result.PayloadSizedIconSlots}/{result.TrackedCycles}",
			$"  retained native icon bytes: {result.RetainedNativeIconBytes:N0}");
	}

	static string FormatBytes(long bytes)
	{
		if (bytes >= 1024 * 1024)
			return $"{bytes / 1024d / 1024d:N1} MiB";
		if (bytes >= 1024)
			return $"{bytes / 1024d:N1} KiB";
		return $"{bytes:N0} B";
	}
}
