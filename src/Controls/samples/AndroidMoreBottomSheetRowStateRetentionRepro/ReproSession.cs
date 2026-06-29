#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using Google.Android.Material.BottomSheet;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Platform;
using AColor = Android.Graphics.Color;
using AView = Android.Views.View;

namespace AndroidMoreBottomSheetRowStateRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 96;
	const int MaxBottomItems = 5;
	const int LeadingNonOverflowItems = MaxBottomItems - 1;
	const int OverflowRowsPerCycle = 6;
	const int IconEdge = 256;
	const int BytesPerPixel = 4;
	const int PayloadBytesPerIcon = IconEdge * IconEdge * BytesPerPixel;
	const int PayloadTitleChars = 8 * 1024;
	const string PayloadTitlePrefix = "MoreBottomSheetPayload-";

	static readonly Action<int, BottomSheetDialog> SelectNoop = static (_, _) => { };
	static readonly List<RetainedNativeImageView> RetainedNativeImages = new();
	static readonly List<RetainedNativeTextView> RetainedNativeTexts = new();

	static readonly MethodInfo CreateMoreBottomSheetMethod =
		typeof(BottomNavigationViewUtils).GetMethod(
			"CreateMoreBottomSheet",
			BindingFlags.Static | BindingFlags.NonPublic,
			binder: null,
			types:
			[
				typeof(Action<int, BottomSheetDialog>),
				typeof(IMauiContext),
				typeof(List<(string title, ImageSource icon, bool tabEnabled)>),
				typeof(int)
			],
			modifiers: null)
		?? throw new MissingMethodException(nameof(BottomNavigationViewUtils), "CreateMoreBottomSheet");

	public static async Task<ReproReport> RunAsync(AppCompatActivity activity)
	{
		RetainedNativeImages.Clear();
		RetainedNativeTexts.Clear();

		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			activity,
			"control: clear native More row drawable/title slots before dialog disposal",
			clearNativeRowState: true);

		var current = await RunScenarioAsync(
			activity,
			"current: dialog disposal leaves native More row drawable/title slots assigned",
			clearNativeRowState: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeImages);
		GC.KeepAlive(RetainedNativeTexts);

		return new ReproReport(
			Cycles,
			OverflowRowsPerCycle,
			IconEdge,
			PayloadBytesPerIcon,
			PayloadTitleChars,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		AppCompatActivity activity,
		string name,
		bool clearNativeRowState)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			await CreateCycleAsync(activity, i, tracked, clearNativeRowState);

			if (i % 12 == 0)
				await Task.Yield();
		}

		await Task.Delay(250);
		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static async Task CreateCycleAsync(
		AppCompatActivity activity,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeRowState)
	{
		var services = new ReproServiceProvider(activity);
		var mauiContext = new MauiContext(services, activity);
		var iconRefs = new List<WeakReference<PayloadImageSource>>(OverflowRowsPerCycle);
		var titleRefs = new List<WeakReference<string>>(OverflowRowsPerCycle);
		var items = CreateItems(cycle, iconRefs, titleRefs);
		var dialog = CreateMoreBottomSheet(mauiContext, items);

		dialog.Show();
		var rowViews = await WaitForRowsAsync(dialog);

		if (clearNativeRowState)
			ClearNativeRowState(rowViews.Images, rowViews.Texts);

		DetachFromParents(rowViews.Images.Cast<AView>().Concat(rowViews.Texts));

		var retainedImages = rowViews.Images
			.Select(RetainedNativeImageView.Create)
			.ToArray();
		var retainedTexts = rowViews.Texts
			.Select(RetainedNativeTextView.Create)
			.ToArray();

		foreach (var image in rowViews.Images)
			image.Dispose();
		foreach (var text in rowViews.Texts)
			text.Dispose();

		dialog.Dismiss();
		dialog.Dispose();

		RetainedNativeImages.AddRange(retainedImages);
		RetainedNativeTexts.AddRange(retainedTexts);

		tracked.Add(TrackedCycle.Create(
			retainedImages,
			retainedTexts,
			iconRefs,
			titleRefs,
			services,
			mauiContext,
			dialog));

		services = null!;
		mauiContext = null!;
		items = null!;
		dialog = null!;
	}

	static List<(string title, ImageSource icon, bool tabEnabled)> CreateItems(
		int cycle,
		List<WeakReference<PayloadImageSource>> iconRefs,
		List<WeakReference<string>> titleRefs)
	{
		var items = new List<(string title, ImageSource icon, bool tabEnabled)>(
			LeadingNonOverflowItems + OverflowRowsPerCycle);

		for (var i = 0; i < LeadingNonOverflowItems; i++)
			items.Add(($"Hidden {cycle:D4}-{i:D2}", new PayloadImageSource(cycle, -1), true));

		for (var row = 0; row < OverflowRowsPerCycle; row++)
		{
			var title = CreatePayloadTitle(cycle, row);
			var icon = new PayloadImageSource(cycle, row);
			titleRefs.Add(new WeakReference<string>(title));
			iconRefs.Add(new WeakReference<PayloadImageSource>(icon));
			items.Add((title, icon, true));
		}

		return items;
	}

	static string CreatePayloadTitle(int cycle, int row)
	{
		var prefix = $"{PayloadTitlePrefix}{cycle:D4}-{row:D2}-";
		return prefix + new string((char)('A' + row), PayloadTitleChars - prefix.Length);
	}

	static BottomSheetDialog CreateMoreBottomSheet(
		IMauiContext mauiContext,
		List<(string title, ImageSource icon, bool tabEnabled)> items)
	{
		return (BottomSheetDialog)CreateMoreBottomSheetMethod.Invoke(
			null,
			[SelectNoop, mauiContext, items, MaxBottomItems])!;
	}

	static async Task<RowViews> WaitForRowsAsync(BottomSheetDialog dialog)
	{
		for (var i = 0; i < 80; i++)
		{
			var images = FindPayloadImageViews(dialog);
			var texts = FindPayloadTextViews(dialog);

			if (images.Count == OverflowRowsPerCycle && texts.Count == OverflowRowsPerCycle)
				return new RowViews(images, texts);

			await Task.Delay(25);
		}

		throw new InvalidOperationException(
			$"Expected {OverflowRowsPerCycle} overflow row image/text peers; found {FindPayloadImageViews(dialog).Count} images and {FindPayloadTextViews(dialog).Count} text views.");
	}

	static IReadOnlyList<ImageView> FindPayloadImageViews(BottomSheetDialog dialog)
	{
		var images = new List<ImageView>(OverflowRowsPerCycle);
		foreach (var root in GetDialogRoots(dialog))
			CollectPayloadImageViews(root, images);
		return images
			.DistinctBy(view => view.Handle)
			.OrderBy(view => view.Handle.ToInt64())
			.ToArray();
	}

	static IReadOnlyList<TextView> FindPayloadTextViews(BottomSheetDialog dialog)
	{
		var texts = new List<TextView>(OverflowRowsPerCycle);
		foreach (var root in GetDialogRoots(dialog))
			CollectPayloadTextViews(root, texts);
		return texts
			.DistinctBy(view => view.Handle)
			.OrderBy(view => view.Handle.ToInt64())
			.ToArray();
	}

	static IEnumerable<AView> GetDialogRoots(BottomSheetDialog dialog)
	{
		if (dialog.Window?.DecorView is AView decorView)
			yield return decorView;

		if (dialog.FindViewById(Android.Resource.Id.Content) is AView contentView)
			yield return contentView;
	}

	static void CollectPayloadImageViews(AView view, ICollection<ImageView> images)
	{
		if (view is ImageView imageView && GetDrawableByteCount(imageView.Drawable) >= PayloadBytesPerIcon)
			images.Add(imageView);

		if (view is ViewGroup viewGroup)
		{
			for (var i = 0; i < viewGroup.ChildCount; i++)
			{
				if (viewGroup.GetChildAt(i) is AView child)
					CollectPayloadImageViews(child, images);
			}
		}
	}

	static void CollectPayloadTextViews(AView view, ICollection<TextView> texts)
	{
		if (view is TextView textView &&
			textView.Text?.StartsWith(PayloadTitlePrefix, StringComparison.Ordinal) == true)
		{
			texts.Add(textView);
		}

		if (view is ViewGroup viewGroup)
		{
			for (var i = 0; i < viewGroup.ChildCount; i++)
			{
				if (viewGroup.GetChildAt(i) is AView child)
					CollectPayloadTextViews(child, texts);
			}
		}
	}

	static void ClearNativeRowState(IEnumerable<ImageView> images, IEnumerable<TextView> texts)
	{
		foreach (var image in images)
			image.SetImageDrawable(null);

		foreach (var text in texts)
			text.Text = string.Empty;
	}

	static void DetachFromParents(IEnumerable<AView> views)
	{
		foreach (var view in views)
		{
			if (view.Parent is ViewGroup parent)
				parent.RemoveView(view);
		}
	}

	static ImageSlotSnapshot CaptureImageSlot(RetainedNativeImageView retained)
	{
		using var wrapper = retained.CreateWrapper();
		var bytes = GetDrawableByteCount(wrapper.Drawable);

		return new ImageSlotSnapshot(
			bytes > 0 ? 1 : 0,
			bytes >= PayloadBytesPerIcon ? 1 : 0,
			bytes);
	}

	static TextSlotSnapshot CaptureTextSlot(RetainedNativeTextView retained)
	{
		using var wrapper = retained.CreateWrapper();
		var text = wrapper.Text;
		var hasPayload = text?.StartsWith(PayloadTitlePrefix, StringComparison.Ordinal) == true;
		var bytes = hasPayload ? text!.Length * sizeof(char) : 0;

		return new TextSlotSnapshot(
			string.IsNullOrEmpty(text) ? 0 : 1,
			hasPayload ? 1 : 0,
			bytes);
	}

	static long GetDrawableByteCount(Drawable? drawable)
	{
		if (drawable is BitmapDrawable bitmapDrawable && bitmapDrawable.Bitmap is Bitmap bitmap)
			return bitmap.AllocationByteCount;

		return drawable is null ? 0 : PayloadBytesPerIcon;
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

	internal sealed class ReproServiceProvider : IServiceProvider
	{
		readonly AppCompatActivity _activity;
		readonly PayloadImageSourceService _payloadImageSourceService = new();
		readonly ReproImageSourceServiceProvider _imageSourceServiceProvider;

		public ReproServiceProvider(AppCompatActivity activity)
		{
			_activity = activity;
			_imageSourceServiceProvider = new ReproImageSourceServiceProvider(this, _payloadImageSourceService);
		}

		public object? GetService(Type serviceType)
		{
			if (serviceType == typeof(IImageSourceServiceProvider))
				return _imageSourceServiceProvider;
			if (serviceType == typeof(Activity))
				return _activity;
			if (serviceType == typeof(Context))
				return _activity;
			if (serviceType == typeof(LayoutInflater))
				return LayoutInflater.From(_activity);

			return null;
		}
	}

	sealed class ReproImageSourceServiceProvider : IImageSourceServiceProvider
	{
		readonly PayloadImageSourceService _payloadImageSourceService;

		public ReproImageSourceServiceProvider(IServiceProvider hostServiceProvider, PayloadImageSourceService payloadImageSourceService)
		{
			HostServiceProvider = hostServiceProvider;
			_payloadImageSourceService = payloadImageSourceService;
		}

		public IServiceProvider HostServiceProvider { get; }

		public IImageSourceService? GetImageSourceService(Type imageSource) =>
			imageSource == typeof(PayloadImageSource) ? _payloadImageSourceService : null;

		public object? GetService(Type serviceType) =>
			serviceType == typeof(IImageSourceServiceProvider)
				? this
				: HostServiceProvider.GetService(serviceType);
	}

	internal sealed class RetainedNativeImageView
	{
		readonly IntPtr _globalHandle;

		RetainedNativeImageView(IntPtr globalHandle)
		{
			_globalHandle = globalHandle;
		}

		public bool IsAlive => _globalHandle != IntPtr.Zero;

		public static RetainedNativeImageView Create(ImageView image)
		{
			var globalHandle = JNIEnv.NewGlobalRef(image.Handle);
			return new RetainedNativeImageView(globalHandle);
		}

		public ImageView CreateWrapper()
		{
			if (_globalHandle == IntPtr.Zero)
				throw new ObjectDisposedException(nameof(RetainedNativeImageView));

			return Java.Lang.Object.GetObject<ImageView>(_globalHandle, JniHandleOwnership.DoNotTransfer)
				?? throw new InvalidOperationException("Could not re-wrap retained native ImageView peer.");
		}
	}

	internal sealed class RetainedNativeTextView
	{
		readonly IntPtr _globalHandle;

		RetainedNativeTextView(IntPtr globalHandle)
		{
			_globalHandle = globalHandle;
		}

		public bool IsAlive => _globalHandle != IntPtr.Zero;

		public static RetainedNativeTextView Create(TextView text)
		{
			var globalHandle = JNIEnv.NewGlobalRef(text.Handle);
			return new RetainedNativeTextView(globalHandle);
		}

		public TextView CreateWrapper()
		{
			if (_globalHandle == IntPtr.Zero)
				throw new ObjectDisposedException(nameof(RetainedNativeTextView));

			return Java.Lang.Object.GetObject<TextView>(_globalHandle, JniHandleOwnership.DoNotTransfer)
				?? throw new InvalidOperationException("Could not re-wrap retained native TextView peer.");
		}
	}

	internal sealed record RowViews(IReadOnlyList<ImageView> Images, IReadOnlyList<TextView> Texts);

	internal sealed record ImageSlotSnapshot(int AssignedSlots, int PayloadSlots, long RetainedBytes);

	internal sealed record TextSlotSnapshot(int AssignedSlots, int PayloadSlots, long RetainedBytes);

	internal sealed record TrackedCycle(
		IReadOnlyList<RetainedNativeImageView> NativeImages,
		IReadOnlyList<RetainedNativeTextView> NativeTexts,
		IReadOnlyList<WeakReference<PayloadImageSource>> IconSources,
		IReadOnlyList<WeakReference<string>> Titles,
		WeakReference<ReproServiceProvider> Services,
		WeakReference<MauiContext> MauiContext,
		WeakReference<BottomSheetDialog> Dialog)
	{
		public static TrackedCycle Create(
			IReadOnlyList<RetainedNativeImageView> nativeImages,
			IReadOnlyList<RetainedNativeTextView> nativeTexts,
			IReadOnlyList<WeakReference<PayloadImageSource>> iconSources,
			IReadOnlyList<WeakReference<string>> titles,
			ReproServiceProvider services,
			MauiContext mauiContext,
			BottomSheetDialog dialog)
		{
			return new TrackedCycle(
				nativeImages,
				nativeTexts,
				iconSources.ToArray(),
				titles.ToArray(),
				new WeakReference<ReproServiceProvider>(services),
				new WeakReference<MauiContext>(mauiContext),
				new WeakReference<BottomSheetDialog>(dialog));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeImageViews,
		int AliveNativeTextViews,
		int AliveImageSources,
		int AliveTitleStrings,
		int AliveServiceProviders,
		int AliveMauiContexts,
		int AliveDialogs,
		int AssignedImageSlots,
		int PayloadSizedImageSlots,
		long RetainedNativeImageBytes,
		int AssignedTextSlots,
		int PayloadSizedTextSlots,
		long RetainedNativeTextBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var iconSourceRefs = new List<WeakReference<PayloadImageSource>>();
			var titleRefs = new List<WeakReference<string>>();
			var serviceRefs = new List<WeakReference<ReproServiceProvider>>();
			var contextRefs = new List<WeakReference<MauiContext>>();
			var dialogRefs = new List<WeakReference<BottomSheetDialog>>();
			var aliveNativeImages = 0;
			var aliveNativeTexts = 0;
			var assignedImageSlots = 0;
			var payloadImageSlots = 0;
			long retainedImageBytes = 0;
			var assignedTextSlots = 0;
			var payloadTextSlots = 0;
			long retainedTextBytes = 0;

			foreach (var cycle in tracked)
			{
				iconSourceRefs.AddRange(cycle.IconSources);
				titleRefs.AddRange(cycle.Titles);
				serviceRefs.Add(cycle.Services);
				contextRefs.Add(cycle.MauiContext);
				dialogRefs.Add(cycle.Dialog);

				foreach (var image in cycle.NativeImages)
				{
					if (!image.IsAlive)
						continue;

					aliveNativeImages++;
					var snapshot = CaptureImageSlot(image);
					assignedImageSlots += snapshot.AssignedSlots;
					payloadImageSlots += snapshot.PayloadSlots;
					retainedImageBytes += snapshot.RetainedBytes;
				}

				foreach (var text in cycle.NativeTexts)
				{
					if (!text.IsAlive)
						continue;

					aliveNativeTexts++;
					var snapshot = CaptureTextSlot(text);
					assignedTextSlots += snapshot.AssignedSlots;
					payloadTextSlots += snapshot.PayloadSlots;
					retainedTextBytes += snapshot.RetainedBytes;
				}
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeImages,
				aliveNativeTexts,
				CountAlive(iconSourceRefs),
				CountAlive(titleRefs),
				CountAlive(serviceRefs),
				CountAlive(contextRefs),
				CountAlive(dialogRefs),
				assignedImageSlots,
				payloadImageSlots,
				retainedImageBytes,
				assignedTextSlots,
				payloadTextSlots,
				retainedTextBytes);
		}
	}
}

internal sealed class PayloadImageSource : ImageSource
{
	public PayloadImageSource(int cycle, int row)
	{
		Cycle = cycle;
		Row = row;
	}

	public int Cycle { get; }

	public int Row { get; }
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

		var bitmap = Bitmap.CreateBitmap(ReproSessionReport.IconEdge, ReproSessionReport.IconEdge, Bitmap.Config.Argb8888!);
		var color = AColor.Argb(
			255,
			(payloadSource.Cycle * 37 + payloadSource.Row * 53) % 255,
			(payloadSource.Cycle * 67 + payloadSource.Row * 29) % 255,
			(payloadSource.Cycle * 97 + payloadSource.Row * 71) % 255);
		bitmap.EraseColor(color);

		Drawable drawable = new BitmapDrawable(context.Resources, bitmap);
		return Task.FromResult<IImageSourceServiceResult<Drawable>?>(new ImageSourceServiceResult(drawable));
	}
}

static class ReproSessionReport
{
	public const int IconEdge = 256;
}

internal sealed record ReproReport(
	int Cycles,
	int OverflowRowsPerCycle,
	int IconEdge,
	int PayloadBytesPerIcon,
	int PayloadTitleChars,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public int ExpectedRows => Cycles * OverflowRowsPerCycle;

	public bool LeakProved =>
		Control.AliveNativeImageViews == ExpectedRows &&
		Current.AliveNativeImageViews == ExpectedRows &&
		Control.AliveNativeTextViews == ExpectedRows &&
		Current.AliveNativeTextViews == ExpectedRows &&
		Control.PayloadSizedImageSlots == 0 &&
		Current.PayloadSizedImageSlots == ExpectedRows &&
		Control.PayloadSizedTextSlots == 0 &&
		Current.PayloadSizedTextSlots == ExpectedRows &&
		Current.AliveImageSources == 0 &&
		Current.AliveTitleStrings == 0 &&
		Current.AliveServiceProviders == 0 &&
		Current.AliveMauiContexts == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidMoreBottomSheetRowStateRetentionRepro",
			$"Cycles: {Cycles}",
			$"Overflow rows per cycle: {OverflowRowsPerCycle}",
			$"Icon size: {IconEdge}x{IconEdge}",
			$"Payload bytes per native icon: {PayloadBytesPerIcon:N0}",
			$"Payload title chars per row: {PayloadTitleChars:N0}",
			$"Expected row slots: {ExpectedRows}",
			$"Rows are detached before dialog disposal to isolate native ImageView/TextView slot state",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control, ExpectedRows),
			string.Empty,
			Format(Current, ExpectedRows),
			string.Empty,
			$"Control retained native row payload: {FormatBytes(Control.RetainedNativeImageBytes + Control.RetainedNativeTextBytes)}",
			$"Current retained native row payload: {FormatBytes(Current.RetainedNativeImageBytes + Current.RetainedNativeTextBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result, int expectedRows)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  alive native ImageView peers: {result.AliveNativeImageViews}/{expectedRows}",
			$"  alive native TextView peers: {result.AliveNativeTextViews}/{expectedRows}",
			$"  alive payload image sources: {result.AliveImageSources}/{expectedRows}",
			$"  alive managed title strings: {result.AliveTitleStrings}/{expectedRows}",
			$"  alive service providers: {result.AliveServiceProviders}/{result.TrackedCycles}",
			$"  alive MauiContexts: {result.AliveMauiContexts}/{result.TrackedCycles}",
			$"  alive BottomSheetDialogs: {result.AliveDialogs}/{result.TrackedCycles}",
			$"  assigned native image slots: {result.AssignedImageSlots}/{expectedRows}",
			$"  payload-sized native image slots: {result.PayloadSizedImageSlots}/{expectedRows}",
			$"  retained native image bytes: {result.RetainedNativeImageBytes:N0}",
			$"  assigned native text slots: {result.AssignedTextSlots}/{expectedRows}",
			$"  payload-sized native text slots: {result.PayloadSizedTextSlots}/{expectedRows}",
			$"  retained native text bytes: {result.RetainedNativeTextBytes:N0}");
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
