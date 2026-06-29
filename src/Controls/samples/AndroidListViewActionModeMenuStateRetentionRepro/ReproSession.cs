#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using AndroidX.Core.View;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Handlers;
using AColor = Android.Graphics.Color;
using AMenu = Android.Views.IMenu;
using AMenuItemCompat = AndroidX.Core.View.MenuItemCompat;
using AView = Android.Views.View;
using AViewGroup = Android.Views.ViewGroup;

namespace AndroidListViewActionModeMenuStateRetentionRepro;

internal static class ReproSession
{
	public const int Cycles = 96;
	public const int ActionsPerCycle = 4;
	public const int IconWidth = 256;
	public const int IconHeight = 256;
	public const int BytesPerPixel = 4;
	public const int PayloadBytesPerIcon = IconWidth * IconHeight * BytesPerPixel;
	public const int StringPayloadChars = 8 * 1024;
	public const int ExpectedNativeMenuItems = Cycles * ActionsPerCycle;

	static readonly BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;
	static readonly PropertyInfo ActionModeContextProperty = typeof(CellAdapter).GetProperty("ActionModeContext", InstanceNonPublic)
		?? throw new MissingMemberException(nameof(CellAdapter), "ActionModeContext");
	static readonly MethodInfo CreateContextMenuMethod = typeof(CellAdapter).GetMethod("CreateContextMenu", InstanceNonPublic)
		?? throw new MissingMethodException(nameof(CellAdapter), "CreateContextMenu");
	static readonly MethodInfo OnDestroyActionModeImplMethod = typeof(CellAdapter).GetMethod("OnDestroyActionModeImpl", InstanceNonPublic)
		?? throw new MissingMethodException(nameof(CellAdapter), "OnDestroyActionModeImpl");

	static readonly List<RetainedNativeMenuItem> RetainedNativeMenuItems = new();

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		RetainedNativeMenuItems.Clear();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			context,
			"control: clear native ActionMode menu item state before teardown",
			clearNativeMenuState: true);

		var current = await RunScenarioAsync(
			context,
			"current: CellAdapter teardown leaves native ActionMode menu item state assigned",
			clearNativeMenuState: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeMenuItems);

		return new ReproReport(
			Cycles,
			ActionsPerCycle,
			IconWidth,
			IconHeight,
			PayloadBytesPerIcon,
			StringPayloadChars,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		IMauiContext mauiContext,
		string name,
		bool clearNativeMenuState)
	{
		var androidContext = mauiContext.Context
			?? Android.App.Application.Context
			?? throw new InvalidOperationException("No Android context is available.");
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			await CreateCycleAsync(mauiContext, androidContext, i, tracked, clearNativeMenuState);

			if (i % 16 == 0)
				await Task.Yield();
		}

		await Task.Delay(250);
		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static async Task CreateCycleAsync(
		IMauiContext mauiContext,
		Context androidContext,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeMenuState)
	{
		var source = new RetainedActionSource(cycle);
		var cellHandler = new FakeCellHandler();
		cellHandler.SetMauiContext(mauiContext);
		cellHandler.SetVirtualView(source.Cell);

		var adapter = new PayloadCellAdapter(androidContext, source.Cell);

		using var anchor = new AView(androidContext);
		using var popup = new PopupMenu(androidContext, anchor);
		var menu = popup.Menu ?? throw new InvalidOperationException("Popup menu was not created.");

		SetActionModeContext(adapter, source.Cell);
		CreateContextMenu(adapter, menu);
		await WaitForMenuIconsAsync(menu);

		var retainedItems = RetainMenuItems(menu);

		if (clearNativeMenuState)
			ClearNativeMenuState(menu);

		DestroyActionMode(adapter);
		source.ClearManagedPayloads();
		((IElementHandler)cellHandler).DisconnectHandler();
		adapter.Dispose();

		RetainedNativeMenuItems.AddRange(retainedItems);
		tracked.Add(TrackedCycle.Create(retainedItems, source, adapter, cellHandler));

		source = null!;
		cellHandler = null!;
		adapter = null!;
	}

	static void SetActionModeContext(CellAdapter adapter, Cell cell)
	{
		ActionModeContextProperty.SetValue(adapter, cell);
	}

	static void CreateContextMenu(CellAdapter adapter, AMenu menu)
	{
		CreateContextMenuMethod.Invoke(adapter, new object[] { menu });
	}

	static void DestroyActionMode(CellAdapter adapter)
	{
		OnDestroyActionModeImplMethod.Invoke(adapter, Array.Empty<object>());
	}

	static async Task WaitForMenuIconsAsync(AMenu menu)
	{
		for (var attempt = 0; attempt < 40; attempt++)
		{
			var ready = menu.Size() == ActionsPerCycle;

			for (var i = 0; ready && i < menu.Size(); i++)
				ready = menu.GetItem(i)?.Icon is not null;

			if (ready)
				return;

			await Task.Delay(25);
		}
	}

	static IReadOnlyList<RetainedNativeMenuItem> RetainMenuItems(AMenu menu)
	{
		var retained = new List<RetainedNativeMenuItem>(menu.Size());

		for (var i = 0; i < menu.Size(); i++)
		{
			if (menu.GetItem(i) is { } item)
				retained.Add(RetainedNativeMenuItem.Create(item));
		}

		return retained;
	}

	static void ClearNativeMenuState(AMenu menu)
	{
		for (var i = 0; i < menu.Size(); i++)
		{
			if (menu.GetItem(i) is { } item)
				ClearNativeMenuState(item);
		}
	}

	static void ClearNativeMenuState(IMenuItem item)
	{
		item.SetTitle(string.Empty);
		item.SetIcon(null);
		AMenuItemCompat.SetContentDescription(item, (string?)null);
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
		IReadOnlyList<RetainedNativeMenuItem> NativeMenuItems,
		WeakReference<Cell> Cell,
		IReadOnlyList<WeakReference<MenuItem>> MenuItems,
		IReadOnlyList<WeakReference<PayloadImageSource>> ImageSources,
		WeakReference<PayloadCellAdapter> Adapter,
		WeakReference<FakeCellHandler> CellHandler)
	{
		public static TrackedCycle Create(
			IReadOnlyList<RetainedNativeMenuItem> nativeMenuItems,
			RetainedActionSource source,
			PayloadCellAdapter adapter,
			FakeCellHandler cellHandler)
		{
			var menuItems = new List<WeakReference<MenuItem>>(source.Actions.Count);
			var imageSources = new List<WeakReference<PayloadImageSource>>(source.IconSources.Count);

			foreach (var action in source.Actions)
				menuItems.Add(new WeakReference<MenuItem>(action));

			foreach (var imageSource in source.IconSources)
				imageSources.Add(new WeakReference<PayloadImageSource>(imageSource));

			return new TrackedCycle(
				nativeMenuItems,
				new WeakReference<Cell>(source.Cell),
				menuItems,
				imageSources,
				new WeakReference<PayloadCellAdapter>(adapter),
				new WeakReference<FakeCellHandler>(cellHandler));
		}
	}

	internal sealed class RetainedNativeMenuItem
	{
		readonly IntPtr _globalHandle;

		RetainedNativeMenuItem(IntPtr globalHandle)
		{
			_globalHandle = globalHandle;
		}

		public static RetainedNativeMenuItem Create(IMenuItem item)
		{
			if (item is not IJavaObject javaObject || javaObject.Handle == IntPtr.Zero)
				throw new InvalidOperationException("Native menu item does not expose a Java handle.");

			return new RetainedNativeMenuItem(JNIEnv.NewGlobalRef(javaObject.Handle));
		}

		public IMenuItem CreateWrapper()
		{
			if (_globalHandle == IntPtr.Zero)
				throw new ObjectDisposedException(nameof(RetainedNativeMenuItem));

			return Java.Lang.Object.GetObject<IMenuItem>(_globalHandle, JniHandleOwnership.DoNotTransfer)
				?? throw new InvalidOperationException("Could not re-wrap retained native menu item peer.");
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeMenuItems,
		int AssignedTitleSlots,
		int PayloadSizedTitleSlots,
		int AssignedContentDescriptionSlots,
		int PayloadSizedContentDescriptionSlots,
		int AssignedIconSlots,
		int PayloadSizedIconSlots,
		long RetainedNativeStringBytes,
		long RetainedNativeIconBytes,
		int AliveCells,
		int AliveManagedMenuItems,
		int AliveImageSources,
		int AliveAdapters,
		int AliveCellHandlers)
	{
		public static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativeMenuItems = 0;
			var assignedTitleSlots = 0;
			var payloadSizedTitleSlots = 0;
			var assignedContentDescriptionSlots = 0;
			var payloadSizedContentDescriptionSlots = 0;
			var assignedIconSlots = 0;
			var payloadSizedIconSlots = 0;
			long retainedNativeStringBytes = 0;
			long retainedNativeIconBytes = 0;
			var aliveCells = 0;
			var aliveManagedMenuItems = 0;
			var aliveImageSources = 0;
			var aliveAdapters = 0;
			var aliveCellHandlers = 0;

			foreach (var cycle in tracked)
			{
				foreach (var retainedItem in cycle.NativeMenuItems)
				{
					var item = retainedItem.CreateWrapper();

					try
					{
						aliveNativeMenuItems++;

						var title = item.TitleFormatted?.ToString();
						var contentDescription = AMenuItemCompat.GetContentDescription(item)?.ToString();
						var iconBytes = GetDrawableByteCount(item.Icon);

						if (!string.IsNullOrEmpty(title))
							assignedTitleSlots++;
						if (title is not null && IsPayloadString(title))
						{
							payloadSizedTitleSlots++;
							retainedNativeStringBytes += EstimateNativeStringBytes(title);
						}

						if (!string.IsNullOrEmpty(contentDescription))
							assignedContentDescriptionSlots++;
						if (contentDescription is not null && IsPayloadString(contentDescription))
						{
							payloadSizedContentDescriptionSlots++;
							retainedNativeStringBytes += EstimateNativeStringBytes(contentDescription);
						}

						if (iconBytes > 0)
							assignedIconSlots++;
						if (iconBytes >= PayloadBytesPerIcon)
							payloadSizedIconSlots++;

						retainedNativeIconBytes += iconBytes;
					}
					finally
					{
						(item as IDisposable)?.Dispose();
					}
				}

				if (cycle.Cell.TryGetTarget(out _))
					aliveCells++;

				foreach (var reference in cycle.MenuItems)
				{
					if (reference.TryGetTarget(out _))
						aliveManagedMenuItems++;
				}

				foreach (var reference in cycle.ImageSources)
				{
					if (reference.TryGetTarget(out _))
						aliveImageSources++;
				}

				if (cycle.Adapter.TryGetTarget(out _))
					aliveAdapters++;

				if (cycle.CellHandler.TryGetTarget(out _))
					aliveCellHandlers++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeMenuItems,
				assignedTitleSlots,
				payloadSizedTitleSlots,
				assignedContentDescriptionSlots,
				payloadSizedContentDescriptionSlots,
				assignedIconSlots,
				payloadSizedIconSlots,
				retainedNativeStringBytes,
				retainedNativeIconBytes,
				aliveCells,
				aliveManagedMenuItems,
				aliveImageSources,
				aliveAdapters,
				aliveCellHandlers);
		}

		static bool IsPayloadString(string? value) =>
			value?.Length >= StringPayloadChars;

		static long EstimateNativeStringBytes(string value) =>
			value.Length * sizeof(char);

		static long GetDrawableByteCount(Drawable? icon)
		{
			if (icon is BitmapDrawable bitmapDrawable && bitmapDrawable.Bitmap is Bitmap bitmap)
				return bitmap.AllocationByteCount;

			return icon is null ? 0 : PayloadBytesPerIcon;
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int ActionsPerCycle,
	int IconWidth,
	int IconHeight,
	int PayloadBytesPerIcon,
	int StringPayloadChars,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	int ExpectedNativeMenuItems => Cycles * ActionsPerCycle;

	public bool LeakProved =>
		Control.AliveNativeMenuItems == ExpectedNativeMenuItems &&
		Current.AliveNativeMenuItems == ExpectedNativeMenuItems &&
		Control.PayloadSizedTitleSlots == 0 &&
		Control.PayloadSizedContentDescriptionSlots == 0 &&
		Control.PayloadSizedIconSlots == 0 &&
		Current.PayloadSizedTitleSlots == ExpectedNativeMenuItems &&
		Current.PayloadSizedContentDescriptionSlots == ExpectedNativeMenuItems &&
		Current.PayloadSizedIconSlots == ExpectedNativeMenuItems &&
		Current.AliveCells == 0 &&
		Current.AliveManagedMenuItems == 0 &&
		Current.AliveImageSources == 0 &&
		Current.AliveAdapters == 0 &&
		Current.AliveCellHandlers == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidListViewActionModeMenuStateRetentionRepro",
			$"Cycles: {Cycles}",
			$"Actions per cycle: {ActionsPerCycle}",
			$"Native menu items: {ExpectedNativeMenuItems}",
			$"Icon size: {IconWidth}x{IconHeight}",
			$"Payload bytes per native icon: {PayloadBytesPerIcon:N0}",
			$"Generated title chars: {StringPayloadChars:N0}",
			$"Generated automation chars: {StringPayloadChars:N0}",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained native menu text payload: {FormatBytes(Control.RetainedNativeStringBytes)}",
			$"Control retained native icon payload: {FormatBytes(Control.RetainedNativeIconBytes)}",
			$"Current retained native menu text payload: {FormatBytes(Current.RetainedNativeStringBytes)}",
			$"Current retained native icon payload: {FormatBytes(Current.RetainedNativeIconBytes)}",
			$"Current retained native payload total: {FormatBytes(Current.RetainedNativeStringBytes + Current.RetainedNativeIconBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var expectedMenuItems = result.TrackedCycles * ReproSession.ActionsPerCycle;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  alive native menu items: {result.AliveNativeMenuItems}/{expectedMenuItems}",
			$"  assigned native title slots: {result.AssignedTitleSlots}/{expectedMenuItems}",
			$"  payload-sized native title slots: {result.PayloadSizedTitleSlots}/{expectedMenuItems}",
			$"  assigned native content-description slots: {result.AssignedContentDescriptionSlots}/{expectedMenuItems}",
			$"  payload-sized native content-description slots: {result.PayloadSizedContentDescriptionSlots}/{expectedMenuItems}",
			$"  assigned native icon slots: {result.AssignedIconSlots}/{expectedMenuItems}",
			$"  payload-sized native icon slots: {result.PayloadSizedIconSlots}/{expectedMenuItems}",
			$"  retained native string bytes: {result.RetainedNativeStringBytes:N0}",
			$"  retained native icon bytes: {result.RetainedNativeIconBytes:N0}",
			$"  alive cells: {result.AliveCells}/{result.TrackedCycles}",
			$"  alive managed menu items: {result.AliveManagedMenuItems}/{expectedMenuItems}",
			$"  alive image sources: {result.AliveImageSources}/{expectedMenuItems}",
			$"  alive adapters: {result.AliveAdapters}/{result.TrackedCycles}",
			$"  alive cell handlers: {result.AliveCellHandlers}/{result.TrackedCycles}");
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

internal sealed class RetainedActionSource
{
	readonly List<MenuItem> _actions = new(ReproSession.ActionsPerCycle);
	readonly List<PayloadImageSource> _iconSources = new(ReproSession.ActionsPerCycle);

	public RetainedActionSource(int cycle)
	{
#pragma warning disable CS0618
		Cell = new TextCell
#pragma warning restore CS0618
		{
			Text = $"Customer {cycle:D4}",
			Detail = "Long-press actions for a retained legacy ListView row"
		};

		for (var i = 0; i < ReproSession.ActionsPerCycle; i++)
		{
			var iconSource = new PayloadImageSource(cycle, i);
			var action = new MenuItem
			{
				Text = CreatePayloadString("Action", cycle, i),
				AutomationId = CreatePayloadString("Automation", cycle, i),
				IconImageSource = iconSource
			};

			_iconSources.Add(iconSource);
			_actions.Add(action);
			Cell.ContextActions.Add(action);
		}
	}

#pragma warning disable CS0618
	public Cell Cell { get; }
#pragma warning restore CS0618

	public IReadOnlyList<MenuItem> Actions => _actions;

	public IReadOnlyList<PayloadImageSource> IconSources => _iconSources;

	public void ClearManagedPayloads()
	{
		foreach (var action in _actions)
		{
			action.Text = "cleared";
			action.IconImageSource = null;
			action.Command = null;
		}

		Cell.ContextActions.Clear();

	}

	static string CreatePayloadString(string label, int cycle, int action)
	{
		var prefix = $"{label}-{cycle:D4}-{action:D2}-";
		return prefix + new string((char)('A' + action), ReproSession.StringPayloadChars);
	}
}

internal sealed class PayloadCellAdapter : CellAdapter
{
	readonly Context _context;
#pragma warning disable CS0618
	readonly Cell _cell;
#pragma warning restore CS0618

#pragma warning disable CS0618
	public PayloadCellAdapter(Context context, Cell cell)
#pragma warning restore CS0618
		: base(context)
	{
		_context = context;
		_cell = cell;
	}

	public override int Count => 1;

	public override object this[int position] => _cell.BindingContext ?? _cell;

	public override long GetItemId(int position) => position;

	public override AView GetView(int position, AView? convertView, AViewGroup? parent)
	{
		return convertView ?? new AView(_context);
	}

#pragma warning disable CS0618
	protected override Cell GetCellForPosition(int position)
#pragma warning restore CS0618
	{
		return _cell;
	}
}

internal sealed class FakeCellHandler : ElementHandler<Cell, object>
{
	public FakeCellHandler()
		: base(ElementMapper)
	{
	}

	protected override object CreatePlatformElement() =>
		new();
}

internal sealed class PayloadImageSource : ImageSource
{
	public PayloadImageSource(int cycle, int action)
	{
		Cycle = cycle;
		Action = action;
	}

	public int Cycle { get; }

	public int Action { get; }
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

		var bitmap = Bitmap.CreateBitmap(ReproSession.IconWidth, ReproSession.IconHeight, Bitmap.Config.Argb8888!);
		var color = AColor.Argb(
			255,
			(payloadSource.Cycle * 37 + payloadSource.Action * 19) % 255,
			(payloadSource.Cycle * 67 + payloadSource.Action * 29) % 255,
			(payloadSource.Cycle * 97 + payloadSource.Action * 31) % 255);
		bitmap.EraseColor(color);

		Drawable drawable = new BitmapDrawable(context.Resources, bitmap);
		return Task.FromResult<IImageSourceServiceResult<Drawable>?>(new ImageSourceServiceResult(drawable));
	}
}
