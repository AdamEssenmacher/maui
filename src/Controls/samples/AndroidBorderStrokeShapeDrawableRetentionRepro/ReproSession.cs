#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Android.Graphics.Drawables;
using Android.Runtime;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;

namespace AndroidBorderStrokeShapeDrawableRetentionRepro;

internal static class ReproSession
{
	const int Iterations = 96;
	const int PayloadBytesPerShape = 512 * 1024;
	const string LogTag = "AndroidBorderStrokeShapeDrawableRetentionRepro";
	static readonly PropertyInfo ContentViewGroupClipProperty =
		typeof(ContentViewGroup).GetProperty("Clip", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new InvalidOperationException("ContentViewGroup.Clip property was not found.");

	public static async Task<string> RunAsync(IMauiContext context)
	{
		Android.Util.Log.Info(LogTag, "Starting repro session.");
		await ForceCollectionsAsync();
		var baselineBytes = GC.GetTotalMemory(true);

		Android.Util.Log.Info(LogTag, "Starting control scenario.");
		var control = await RunScenarioAsync(
			"control: clear ContentViewGroup.Background after disconnect",
			context,
			clearNativeBackground: true);
		await ForceCollectionsAsync();

		Android.Util.Log.Info(LogTag, "Starting current scenario.");
		var current = await RunScenarioAsync(
			"current: disconnect leaves MauiDrawable assigned as native background",
			context,
			clearNativeBackground: false);
		await ForceCollectionsAsync();

		Android.Util.Log.Info(LogTag, "Inspecting scenarios.");
		var controlResult = Inspect(control);
		var currentResult = Inspect(current);
		var finalBytes = GC.GetTotalMemory(true);

		var report = new ReproReport(
			Iterations,
			PayloadBytesPerShape,
			baselineBytes,
			finalBytes,
			controlResult,
			currentResult).ToText();

		control.Dispose();
		current.Dispose();

		Android.Util.Log.Info(LogTag, "Finished repro session.");
		return report;
	}

	static async Task<ScenarioSnapshot> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearNativeBackground)
	{
		var samples = new List<IterationSnapshot>(Iterations);

		for (var i = 0; i < Iterations; i++)
		{
			samples.Add(CreateIteration(i, context, clearNativeBackground));

			if ((i + 1) % 8 == 0)
				Android.Util.Log.Info(LogTag, $"{name}: created {i + 1}/{Iterations} iterations.");

			if ((i + 1) % 16 == 0)
				await ForceCollectionsAsync();
		}

		return new ScenarioSnapshot(name, samples);
	}

	static IterationSnapshot CreateIteration(
		int index,
		IMauiContext context,
		bool clearNativeBackground)
	{
		var shape = new PayloadGeometry(index, PayloadBytesPerShape);
		var border = new Border
		{
			StrokeShape = shape,
			Stroke = Brush.DarkSlateGray,
			StrokeThickness = 3,
			Background = Brush.White,
			WidthRequest = 160,
			HeightRequest = 96
		};

		var handler = new BorderHandler();
		handler.SetMauiContext(context);
		border.Handler = handler;

		var platformView = handler.PlatformView
			?? throw new InvalidOperationException("BorderHandler did not create an Android ContentViewGroup.");

		if (platformView.Background is not Microsoft.Maui.Graphics.MauiDrawable)
			throw new InvalidOperationException($"Expected MauiDrawable background, found {platformView.Background?.GetType().FullName ?? "null"}.");

		var nativeRoot = new NativePeerRoot(platformView);
		var borderWeak = new WeakReference<Border>(border);
		var handlerWeak = new WeakReference<IElementHandler>(handler);
		var shapeWeak = new WeakReference<PayloadGeometry>(shape);
		var payloadWeak = new WeakReference<byte[]>(shape.Payload);

		((IElementHandler)handler).DisconnectHandler();

		// Isolate this repro from C129: BorderHandler also stores the Border in native owner fields.
		platformView.CrossPlatformLayout = null;
		ContentViewGroupClipProperty.SetValue(platformView, null);
		platformView.RemoveAllViews();

		// Remove app-side references so only the retained native background can keep the shape graph alive.
		border.StrokeShape = null;

		if (clearNativeBackground)
		{
			var oldBackground = platformView.Background;
			platformView.Background = null;
			oldBackground?.Dispose();
		}

		handler = null!;
		border = null!;
		shape = null!;
		platformView = null!;

		return new IterationSnapshot(nativeRoot, borderWeak, handlerWeak, shapeWeak, payloadWeak);
	}

	static ScenarioResult Inspect(ScenarioSnapshot scenario)
	{
		var nativeViews = 0;
		var assignedBackgrounds = 0;
		var mauiDrawableBackgrounds = 0;
		var aliveBorders = 0;
		var aliveHandlers = 0;
		var aliveShapes = 0;
		var alivePayloads = 0;
		long retainedPayloadBytes = 0;

		foreach (var sample in scenario.Samples)
		{
			if (sample.Border.TryGetTarget(out _))
				aliveBorders++;

			if (sample.Handler.TryGetTarget(out _))
				aliveHandlers++;

			if (sample.Shape.TryGetTarget(out _))
				aliveShapes++;

			if (sample.Payload.TryGetTarget(out _))
			{
				alivePayloads++;
				retainedPayloadBytes += PayloadBytesPerShape;
			}

			var view = sample.NativeRoot.Get<ContentViewGroup>();
			if (view is null)
				continue;

			nativeViews++;

			var background = view.Background;
			if (background is null)
				continue;

			assignedBackgrounds++;

			if (background is Microsoft.Maui.Graphics.MauiDrawable)
				mauiDrawableBackgrounds++;
		}

		return new ScenarioResult(
			scenario.Name,
			scenario.Samples.Count,
			nativeViews,
			assignedBackgrounds,
			mauiDrawableBackgrounds,
			aliveBorders,
			aliveHandlers,
			aliveShapes,
			alivePayloads,
			retainedPayloadBytes);
	}

	static async Task ForceCollectionsAsync()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Java.Lang.JavaSystem.Gc();
			await Task.Delay(100);
		}
	}

	internal sealed class PayloadGeometry : Geometry
	{
		public PayloadGeometry(int index, int payloadBytes)
		{
			Index = index;
			Payload = CreatePayload(index, payloadBytes);
			Tokens = CreateTokens(index);
		}

		public int Index { get; }

		public byte[] Payload { get; }

		public IReadOnlyList<VectorToken> Tokens { get; }

		public override void AppendPath(PathF path)
		{
			path.MoveTo(0, 0);
			path.LineTo(160, 0);
			path.LineTo(160, 72);
			path.QuadTo(128, 96, 80, 92);
			path.QuadTo(24, 88, 0, 72);
			path.Close();
		}
	}

	internal sealed record VectorToken(string Key, string Layer, string Value);

	static VectorToken[] CreateTokens(int index)
	{
		var tokens = new VectorToken[16];
		for (var i = 0; i < tokens.Length; i++)
			tokens[i] = new VectorToken($"card-shape-{index:D4}-{i:D2}", "stroke-shape", $"resolved-vector-style-{index:D4}-{i:D2}");

		return tokens;
	}

	static byte[] CreatePayload(int index, int payloadBytes)
	{
		var payload = new byte[payloadBytes];
		for (var i = 0; i < payload.Length; i += 4096)
			payload[i] = (byte)(0x31 + index + i);

		return payload;
	}

	sealed record ScenarioSnapshot(string Name, List<IterationSnapshot> Samples) : IDisposable
	{
		public void Dispose()
		{
			foreach (var sample in Samples)
				sample.NativeRoot.Dispose();
		}
	}

	sealed record IterationSnapshot(
		NativePeerRoot NativeRoot,
		WeakReference<Border> Border,
		WeakReference<IElementHandler> Handler,
		WeakReference<PayloadGeometry> Shape,
		WeakReference<byte[]> Payload);

	internal sealed record ScenarioResult(
		string Name,
		int TrackedIterations,
		int NativeViewsRetained,
		int AssignedBackgrounds,
		int MauiDrawableBackgrounds,
		int AliveBorders,
		int AliveHandlers,
		int AliveShapes,
		int AlivePayloads,
		long RetainedPayloadBytes);

	sealed class NativePeerRoot : IDisposable
	{
		IntPtr _handle;

		public NativePeerRoot(Java.Lang.Object peer)
		{
			_handle = JNIEnv.NewGlobalRef(peer.Handle);
		}

		public T? Get<T>() where T : Java.Lang.Object
		{
			if (_handle == IntPtr.Zero)
				return null;

			return Java.Lang.Object.GetObject<T>(_handle, JniHandleOwnership.DoNotTransfer);
		}

		public void Dispose()
		{
			if (_handle == IntPtr.Zero)
				return;

			JNIEnv.DeleteGlobalRef(_handle);
			_handle = IntPtr.Zero;
		}
	}
}

internal sealed record ReproReport(
	int Iterations,
	int PayloadBytesPerShape,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.NativeViewsRetained == Iterations &&
		Current.NativeViewsRetained == Iterations &&
		Control.MauiDrawableBackgrounds == 0 &&
		Control.AlivePayloads == 0 &&
		Current.MauiDrawableBackgrounds == Iterations &&
		Current.AlivePayloads == Iterations &&
		Current.AliveBorders == 0 &&
		Current.AliveHandlers == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var controlMiB = Control.RetainedPayloadBytes / 1024d / 1024d;
		var currentMiB = Current.RetainedPayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidBorderStrokeShapeDrawableRetentionRepro",
			$"Iterations: {Iterations}",
			$"Payload bytes per Border.StrokeShape: {PayloadBytesPerShape:N0}",
			"Source paths mirrored: BorderHandler, ContentViewGroup.UpdateBackground(IBorderStroke), StrokeExtensions.UpdateMauiDrawable, MauiDrawable.SetBorderShape, ElementHandler disconnect",
			"Known C129 owner fields cleared in both runs: ContentViewGroup.CrossPlatformLayout and ContentViewGroup.Clip",
			"Retained peers: native Android ContentViewGroup instances rooted by JNI global refs",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained shape payload: {controlMiB:N1} MiB",
			$"Current retained shape payload: {currentMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked iterations: {result.TrackedIterations}",
			$"  alive native ContentViewGroups: {result.NativeViewsRetained}/{result.TrackedIterations}",
			$"  assigned native backgrounds: {result.AssignedBackgrounds}/{result.TrackedIterations}",
			$"  MauiDrawable backgrounds: {result.MauiDrawableBackgrounds}/{result.TrackedIterations}",
			$"  alive Borders: {result.AliveBorders}/{result.TrackedIterations}",
			$"  alive handlers: {result.AliveHandlers}/{result.TrackedIterations}",
			$"  alive StrokeShape geometries: {result.AliveShapes}/{result.TrackedIterations}",
			$"  alive StrokeShape payload byte arrays: {result.AlivePayloads}/{result.TrackedIterations}",
			$"  retained payload bytes: {result.RetainedPayloadBytes:N0}");
	}
}
