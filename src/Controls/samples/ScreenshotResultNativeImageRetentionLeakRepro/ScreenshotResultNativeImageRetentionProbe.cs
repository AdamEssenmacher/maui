using System.Reflection;
using System.Runtime.CompilerServices;
using CoreGraphics;
using Foundation;
using Microsoft.Maui.Media;
using UIKit;

namespace ScreenshotResultNativeImageRetentionLeakRepro;

static class ScreenshotResultNativeImageRetentionProbe
{
	const int Iterations = 80;
	const int PayloadBytes = 1024 * 1024;
	const int ImageWidth = 1024;
	const int ImageHeight = 1024;
	const int BytesPerPixel = 4;

	static readonly Type CurrentResultType =
		typeof(Screenshot).Assembly.GetType("Microsoft.Maui.Media.ScreenshotResult")
		?? throw new InvalidOperationException("Could not find ScreenshotResult.");

	static readonly ConstructorInfo CurrentResultConstructor =
		CurrentResultType.GetConstructor(
			BindingFlags.Instance | BindingFlags.NonPublic,
			binder: null,
			types: new[] { typeof(UIImage) },
			modifiers: null)
		?? throw new InvalidOperationException("Could not find ScreenshotResult constructor.");

	static readonly FieldInfo CurrentUIImageField =
		CurrentResultType.GetField("bmp", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new InvalidOperationException("Could not find ScreenshotResult.bmp.");

	public static async Task<ProbeResult> RunAsync()
	{
		var imagePayloads = new ConditionalWeakTable<UIImage, Payload>();
		var controlResults = new List<IScreenshotResult>(Iterations);
		var currentResults = new List<IScreenshotResult>(Iterations);
		var controlRefs = new List<ScenarioRefs>(Iterations);
		var currentRefs = new List<ScenarioRefs>(Iterations);

		for (var i = 0; i < Iterations; i++)
			controlRefs.Add(await CreateControlScenarioAsync(controlResults, imagePayloads, i));

		for (var i = 0; i < Iterations; i++)
			currentRefs.Add(await CreateCurrentScenarioAsync(currentResults, imagePayloads, i));

		ForceCollect();

		return new ProbeResult(
			Iterations,
			PayloadBytes,
			ImageWidth,
			ImageHeight,
			BytesPerPixel,
			controlResults.Count,
			currentResults.Count,
			CountAlive(controlRefs, static r => r.Image),
			CountAlive(controlRefs, static r => r.ImagePayload),
			CountAlive(currentRefs, static r => r.Image),
			CountAlive(currentRefs, static r => r.ImagePayload),
			GC.GetTotalMemory(forceFullCollection: true));
	}

	static async Task<ScenarioRefs> CreateControlScenarioAsync(
		List<IScreenshotResult> retainedResults,
		ConditionalWeakTable<UIImage, Payload> imagePayloads,
		int index)
	{
		var image = CreateImage(index);
		var imagePayload = new Payload(index, PayloadBytes);
		imagePayloads.Add(image, imagePayload);

		using var data = image.AsJPEG(0.85f) ?? throw new InvalidOperationException("Failed to encode control screenshot image.");
		retainedResults.Add(new ByteArrayScreenshotResult(ImageWidth, ImageHeight, data.ToArray()));

		var refs = new ScenarioRefs(
			new WeakReference<UIImage>(image),
			new WeakReference<Payload>(imagePayload));

		image.Dispose();
		await Task.Yield();
		return refs;
	}

	static async Task<ScenarioRefs> CreateCurrentScenarioAsync(
		List<IScreenshotResult> retainedResults,
		ConditionalWeakTable<UIImage, Payload> imagePayloads,
		int index)
	{
		var image = CreateImage(index);
		var imagePayload = new Payload(index, PayloadBytes);
		imagePayloads.Add(image, imagePayload);

		var result = (IScreenshotResult)CurrentResultConstructor.Invoke(new object[] { image });
		retainedResults.Add(result);

		using (await result.OpenReadAsync(ScreenshotFormat.Jpeg, quality: 85))
		{
		}

		var retainedImage = (UIImage?)CurrentUIImageField.GetValue(result)
			?? throw new InvalidOperationException("Current ScreenshotResult did not retain the UIImage.");

		var refs = new ScenarioRefs(
			new WeakReference<UIImage>(retainedImage),
			new WeakReference<Payload>(imagePayload));

		await Task.Yield();
		return refs;
	}

	static UIImage CreateImage(int index)
	{
		var size = new CGSize(ImageWidth, ImageHeight);
		using var renderer = new UIGraphicsImageRenderer(size, new UIGraphicsImageRendererFormat
		{
			Opaque = true,
			Scale = 1
		});

		return renderer.CreateImage((UIGraphicsImageRendererContext imageContext) =>
		{
			var context = imageContext.CGContext;
			var hue = (nfloat)((index % 41) / 41.0);
			context.SetFillColor(UIColor.FromHSBA(hue, 0.72f, 0.86f, 1).CGColor);
			context.FillRect(new CGRect(CGPoint.Empty, size));

			context.SetFillColor(UIColor.FromRGBA((nfloat)1, (nfloat)1, (nfloat)1, (nfloat)0.28).CGColor);
			for (var stripe = 0; stripe < 16; stripe++)
			{
				var offset = (index * 17 + stripe * 67) % ImageWidth;
				context.FillRect(new CGRect(offset, 0, 18, ImageHeight));
				context.FillRect(new CGRect(0, offset, ImageWidth, 14));
			}
		});
	}

	static int CountAlive<T>(List<ScenarioRefs> refs, Func<ScenarioRefs, WeakReference<T>> selector)
		where T : class
	{
		var count = 0;
		foreach (var item in refs)
		{
			if (selector(item).TryGetTarget(out _))
				count++;
		}

		return count;
	}

	static void ForceCollect()
	{
		for (var i = 0; i < 8; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(50);
		}
	}

	sealed class ByteArrayScreenshotResult : IScreenshotResult
	{
		readonly byte[] _encodedBytes;

		public ByteArrayScreenshotResult(int width, int height, byte[] encodedBytes)
		{
			Width = width;
			Height = height;
			_encodedBytes = encodedBytes;
		}

		public int Width { get; }

		public int Height { get; }

		public Task<Stream> OpenReadAsync(ScreenshotFormat format = ScreenshotFormat.Png, int quality = 100) =>
			Task.FromResult<Stream>(new MemoryStream(_encodedBytes, writable: false));

		public Task CopyToAsync(Stream destination, ScreenshotFormat format = ScreenshotFormat.Png, int quality = 100)
		{
			destination.Write(_encodedBytes, 0, _encodedBytes.Length);
			return Task.CompletedTask;
		}
	}

	sealed class Payload
	{
		readonly byte[] _bytes;

		public Payload(int id, int size)
		{
			Id = id;
			_bytes = new byte[size];
			_bytes[0] = (byte)(id % 251);
			_bytes[^1] = (byte)((id + 17) % 251);
		}

		public int Id { get; }
	}

	sealed record ScenarioRefs(
		WeakReference<UIImage> Image,
		WeakReference<Payload> ImagePayload);
}

sealed record ProbeResult(
	int Iterations,
	int PayloadBytes,
	int ImageWidth,
	int ImageHeight,
	int BytesPerPixel,
	int RetainedControlResults,
	int RetainedCurrentResults,
	int ControlImagesRetained,
	int ControlImagePayloadsRetained,
	int CurrentImagesRetained,
	int CurrentImagePayloadsRetained,
	long ManagedHeapBytes)
{
	public bool ProvedLeak =>
		RetainedControlResults == Iterations &&
		RetainedCurrentResults == Iterations &&
		ControlImagePayloadsRetained == 0 &&
		CurrentImagesRetained == Iterations &&
		CurrentImagePayloadsRetained == Iterations;

	public string ToReport()
	{
		var retainedPayloadMiB = CurrentImagePayloadsRetained * PayloadBytes / 1024.0 / 1024.0;
		var retainedNativeMiB = CurrentImagesRetained * ImageWidth * ImageHeight * BytesPerPixel / 1024.0 / 1024.0;
		var heapMiB = ManagedHeapBytes / 1024.0 / 1024.0;

		return string.Join(Environment.NewLine, new[]
		{
			"ScreenshotResultNativeImageRetentionLeakRepro",
			$"Iterations: {Iterations}",
			$"Screenshot size: {ImageWidth}x{ImageHeight} at {BytesPerPixel} bytes/pixel",
			$"Payload bytes per native UIImage: {PayloadBytes}",
			$"Control retained screenshot results: {RetainedControlResults}/{Iterations}",
			$"Current retained screenshot results: {RetainedCurrentResults}/{Iterations}",
			$"Control retained UIImages: {ControlImagesRetained}/{Iterations}",
			$"Control retained UIImage payloads: {ControlImagePayloadsRetained}/{Iterations}",
			$"Current retained UIImages: {CurrentImagesRetained}/{Iterations}",
			$"Current retained UIImage payloads: {CurrentImagePayloadsRetained}/{Iterations}",
			$"Retained payload estimate: {retainedPayloadMiB:F1} MiB",
			$"Estimated native UIImage pixel memory: {retainedNativeMiB:F1} MiB",
			$"Managed heap after proof: {heapMiB:F1} MiB",
			$"Proved leak: {ProvedLeak}"
		});
	}
}
