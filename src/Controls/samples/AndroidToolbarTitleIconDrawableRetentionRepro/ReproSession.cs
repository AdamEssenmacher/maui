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
using AView = Android.Views.View;
using ControlsToolbar = Microsoft.Maui.Controls.Toolbar;

namespace AndroidToolbarTitleIconDrawableRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 96;
	const int IconWidth = 512;
	const int IconHeight = 512;
	const int BytesPerPixel = 4;
	const int PayloadBytesPerIcon = IconWidth * IconHeight * BytesPerPixel;

	static readonly List<MaterialToolbar> RetainedNativeToolbars = new();
	static readonly List<ImageView> RetainedNativeTitleIconViews = new();

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		RetainedNativeToolbars.Clear();
		RetainedNativeTitleIconViews.Clear();

		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			"control: clear native Toolbar.TitleIcon drawable before disconnect",
			context,
			clearNativeTitleIcon: true);

		var current = await RunScenarioAsync(
			"current: MAUI disconnect leaves native Toolbar.TitleIcon drawable assigned",
			context,
			clearNativeTitleIcon: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeToolbars);
		GC.KeepAlive(RetainedNativeTitleIconViews);

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
		bool clearNativeTitleIcon)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateCycle(context, i, tracked, clearNativeTitleIcon);

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
		bool clearNativeTitleIcon)
	{
		var titleIcon = new PayloadImageSource(cycle);
		var page = new ContentPage
		{
			Title = $"Toolbar page {cycle:D4}"
		};

		var toolbar = new ControlsToolbar(page)
		{
			Title = $"Retained toolbar {cycle:D4}",
			BackButtonVisible = false,
			IsVisible = true,
			TitleIcon = titleIcon
		};

		var handler = new ToolbarHandler();
		handler.SetMauiContext(context);
		handler.SetVirtualView(toolbar);

		ControlsToolbar.MapTitleIcon((IToolbarHandler)handler, toolbar);

		var platformToolbar = handler.PlatformView;
		var titleIconView = WaitForTitleIconView(platformToolbar);

		if (clearNativeTitleIcon)
			titleIconView.SetImageDrawable(null);

		((IElementHandler)handler).DisconnectHandler();

		RetainedNativeToolbars.Add(platformToolbar);
		RetainedNativeTitleIconViews.Add(titleIconView);
		tracked.Add(TrackedCycle.Create(cycle, platformToolbar, titleIconView, toolbar, titleIcon, handler));
	}

	static ImageView WaitForTitleIconView(MaterialToolbar platformToolbar)
	{
		for (var i = 0; i < 40; i++)
		{
			if (FindPayloadImageView(platformToolbar) is { } imageView)
				return imageView;

			Thread.Sleep(25);
		}

		throw new InvalidOperationException("Could not find the Toolbar.TitleIcon ImageView with an assigned payload drawable.");
	}

	static ImageView? FindPayloadImageView(AView view)
	{
		if (view is ImageView imageView && GetDrawableByteCount(imageView.Drawable) >= PayloadBytesPerIcon)
			return imageView;

		if (view is ViewGroup viewGroup)
		{
			for (var i = 0; i < viewGroup.ChildCount; i++)
			{
				if (viewGroup.GetChildAt(i) is AView child &&
					FindPayloadImageView(child) is { } result)
				{
					return result;
				}
			}
		}

		return null;
	}

	static long GetDrawableByteCount(Drawable? drawable)
	{
		if (drawable is BitmapDrawable bitmapDrawable && bitmapDrawable.Bitmap is Bitmap bitmap)
			return bitmap.AllocationByteCount;

		return drawable is null ? 0 : PayloadBytesPerIcon;
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

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<MaterialToolbar> NativeToolbar,
		WeakReference<ImageView> NativeTitleIconView,
		WeakReference<object> VirtualToolbar,
		WeakReference<PayloadImageSource> TitleIconSource,
		WeakReference<ToolbarHandler> Handler)
	{
		public static TrackedCycle Create(
			int cycle,
			MaterialToolbar nativeToolbar,
			ImageView nativeTitleIconView,
			object virtualToolbar,
			PayloadImageSource titleIconSource,
			ToolbarHandler handler)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<MaterialToolbar>(nativeToolbar),
				new WeakReference<ImageView>(nativeTitleIconView),
				new WeakReference<object>(virtualToolbar),
				new WeakReference<PayloadImageSource>(titleIconSource),
				new WeakReference<ToolbarHandler>(handler));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeToolbars,
		int AliveNativeTitleIconViews,
		int AliveVirtualToolbars,
		int AliveTitleIconSources,
		int AliveHandlers,
		int AssignedIconSlots,
		int PayloadSizedIconSlots,
		long RetainedNativeIconBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var nativeToolbarRefs = new List<WeakReference<MaterialToolbar>>();
			var titleIconViewRefs = new List<WeakReference<ImageView>>();
			var virtualToolbarRefs = new List<WeakReference<object>>();
			var titleIconSourceRefs = new List<WeakReference<PayloadImageSource>>();
			var handlerRefs = new List<WeakReference<ToolbarHandler>>();
			var assignedIconSlots = 0;
			var payloadSizedIconSlots = 0;
			long retainedNativeIconBytes = 0;

			foreach (var cycle in tracked)
			{
				nativeToolbarRefs.Add(cycle.NativeToolbar);
				titleIconViewRefs.Add(cycle.NativeTitleIconView);
				virtualToolbarRefs.Add(cycle.VirtualToolbar);
				titleIconSourceRefs.Add(cycle.TitleIconSource);
				handlerRefs.Add(cycle.Handler);

				if (cycle.NativeTitleIconView.TryGetTarget(out var titleIconView))
				{
					var iconBytes = GetDrawableByteCount(titleIconView.Drawable);

					if (iconBytes > 0)
						assignedIconSlots++;
					if (iconBytes >= PayloadBytesPerIcon)
						payloadSizedIconSlots++;

					retainedNativeIconBytes += iconBytes;
				}
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				CountAlive(nativeToolbarRefs),
				CountAlive(titleIconViewRefs),
				CountAlive(virtualToolbarRefs),
				CountAlive(titleIconSourceRefs),
				CountAlive(handlerRefs),
				assignedIconSlots,
				payloadSizedIconSlots,
				retainedNativeIconBytes);
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
		Control.AliveNativeTitleIconViews == Cycles &&
		Current.AliveNativeTitleIconViews == Cycles &&
		Control.PayloadSizedIconSlots == 0 &&
		Current.PayloadSizedIconSlots == Cycles &&
		Current.AliveVirtualToolbars == 0 &&
		Current.AliveTitleIconSources == 0 &&
		Current.AliveHandlers == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidToolbarTitleIconDrawableRetentionRepro",
			$"Cycles: {Cycles}",
			$"Icon size: {IconWidth}x{IconHeight}",
			$"Payload bytes per native title icon: {PayloadBytesPerIcon:N0}",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained native title-icon payload: {FormatBytes(Control.RetainedNativeIconBytes)}",
			$"Current retained native title-icon payload: {FormatBytes(Current.RetainedNativeIconBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  alive native toolbars: {result.AliveNativeToolbars}/{result.TrackedCycles}",
			$"  alive native title-icon ImageViews: {result.AliveNativeTitleIconViews}/{result.TrackedCycles}",
			$"  alive virtual toolbars: {result.AliveVirtualToolbars}/{result.TrackedCycles}",
			$"  alive title-icon image sources: {result.AliveTitleIconSources}/{result.TrackedCycles}",
			$"  alive toolbar handlers: {result.AliveHandlers}/{result.TrackedCycles}",
			$"  assigned native title-icon slots: {result.AssignedIconSlots}/{result.TrackedCycles}",
			$"  payload-sized native title-icon slots: {result.PayloadSizedIconSlots}/{result.TrackedCycles}",
			$"  retained native title-icon bytes: {result.RetainedNativeIconBytes:N0}");
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
