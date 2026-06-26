using System.Reflection;
using System.Runtime.CompilerServices;
using CoreGraphics;
using Foundation;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;
using UIKit;

namespace MediaPickerUIImageFileResultRetentionLeakRepro;

static class MediaPickerUIImageFileResultRetentionProbe
{
	const int Iterations = 80;
	const int PayloadBytes = 1024 * 1024;

	static readonly Type CurrentResultType =
		typeof(MediaPicker).Assembly.GetType("Microsoft.Maui.Media.CompressedUIImageFileResult")
		?? throw new InvalidOperationException("Could not find CompressedUIImageFileResult.");

	static readonly ConstructorInfo CurrentResultConstructor =
		CurrentResultType.GetConstructor(
			BindingFlags.Instance | BindingFlags.NonPublic,
			binder: null,
			types: new[] { typeof(UIImage), typeof(string), typeof(int?), typeof(int?), typeof(int) },
			modifiers: null)
		?? throw new InvalidOperationException("Could not find CompressedUIImageFileResult constructor.");

	static readonly FieldInfo CurrentUIImageField =
		CurrentResultType.GetField("uiImage", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new InvalidOperationException("Could not find CompressedUIImageFileResult.uiImage.");

	static readonly FieldInfo CurrentDataField =
		CurrentResultType.GetField("data", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new InvalidOperationException("Could not find CompressedUIImageFileResult.data.");

	public static async Task<ProbeResult> RunAsync()
	{
		var imagePayloads = new ConditionalWeakTable<UIImage, Payload>();
		var dataPayloads = new ConditionalWeakTable<NSData, Payload>();
		var controlResults = new List<FileResult>(Iterations);
		var currentResults = new List<FileResult>(Iterations);
		var controlRefs = new List<ScenarioRefs>(Iterations);
		var currentRefs = new List<ScenarioRefs>(Iterations);

		for (var i = 0; i < Iterations; i++)
			controlRefs.Add(await CreateControlScenarioAsync(controlResults, imagePayloads, dataPayloads, i));

		for (var i = 0; i < Iterations; i++)
			currentRefs.Add(await CreateCurrentScenarioAsync(currentResults, imagePayloads, dataPayloads, i));

		ForceCollect();

		return new ProbeResult(
			Iterations,
			PayloadBytes,
			controlResults.Count,
			currentResults.Count,
			CountAlive(controlRefs, static r => r.Image),
			CountAlive(controlRefs, static r => r.ImagePayload),
			CountAlive(controlRefs, static r => r.Data),
			CountAlive(controlRefs, static r => r.DataPayload),
			CountAlive(currentRefs, static r => r.Image),
			CountAlive(currentRefs, static r => r.ImagePayload),
			CountAlive(currentRefs, static r => r.Data),
			CountAlive(currentRefs, static r => r.DataPayload),
			GC.GetTotalMemory(forceFullCollection: true));
	}

	static async Task<ScenarioRefs> CreateControlScenarioAsync(
		List<FileResult> retainedResults,
		ConditionalWeakTable<UIImage, Payload> imagePayloads,
		ConditionalWeakTable<NSData, Payload> dataPayloads,
		int index)
	{
		var image = CreateImage(index);
		var imagePayload = new Payload(index, PayloadBytes);
		imagePayloads.Add(image, imagePayload);

		using var data = image.AsJPEG(0.95f) ?? throw new InvalidOperationException("Failed to encode control image.");
		var dataPayload = new Payload(index + Iterations, PayloadBytes);
		dataPayloads.Add(data, dataPayload);

		var tempPath = Path.Combine(Path.GetTempPath(), $"maui-control-capture-{Guid.NewGuid():N}.jpg");
		File.WriteAllBytes(tempPath, data.ToArray());
		retainedResults.Add(new FileResult(tempPath));

		var refs = new ScenarioRefs(
			new WeakReference<UIImage>(image),
			new WeakReference<Payload>(imagePayload),
			new WeakReference<NSData>(data),
			new WeakReference<Payload>(dataPayload));

		image.Dispose();
		await Task.Yield();
		return refs;
	}

	static async Task<ScenarioRefs> CreateCurrentScenarioAsync(
		List<FileResult> retainedResults,
		ConditionalWeakTable<UIImage, Payload> imagePayloads,
		ConditionalWeakTable<NSData, Payload> dataPayloads,
		int index)
	{
		var image = CreateImage(index);
		var imagePayload = new Payload(index, PayloadBytes);
		imagePayloads.Add(image, imagePayload);

		var result = (FileResult)CurrentResultConstructor.Invoke(new object?[]
		{
			image,
			$"maui-current-capture-{index}.jpg",
			null,
			null,
			100
		});

		retainedResults.Add(result);

		using (await result.OpenReadAsync())
		{
		}

		var retainedImage = (UIImage?)CurrentUIImageField.GetValue(result)
			?? throw new InvalidOperationException("Current result did not retain the UIImage.");
		var retainedData = (NSData?)CurrentDataField.GetValue(result)
			?? throw new InvalidOperationException("Current result did not cache NSData after OpenReadAsync.");

		var dataPayload = new Payload(index + Iterations, PayloadBytes);
		dataPayloads.Add(retainedData, dataPayload);

		var refs = new ScenarioRefs(
			new WeakReference<UIImage>(retainedImage),
			new WeakReference<Payload>(imagePayload),
			new WeakReference<NSData>(retainedData),
			new WeakReference<Payload>(dataPayload));

		await Task.Yield();
		return refs;
	}

	static UIImage CreateImage(int index)
	{
		var size = new CGSize(768, 768);
		using var renderer = new UIGraphicsImageRenderer(size, new UIGraphicsImageRendererFormat
		{
			Opaque = true,
			Scale = 1
		});

		return renderer.CreateImage((UIGraphicsImageRendererContext imageContext) =>
		{
			var context = imageContext.CGContext;
			var hue = (nfloat)((index % 37) / 37.0);
			context.SetFillColor(UIColor.FromHSBA(hue, 0.75f, 0.85f, 1).CGColor);
			context.FillRect(new CGRect(CGPoint.Empty, size));

			context.SetFillColor(UIColor.FromRGBA((nfloat)1, (nfloat)1, (nfloat)1, (nfloat)0.35).CGColor);
			for (var stripe = 0; stripe < 12; stripe++)
			{
				var offset = (index * 13 + stripe * 61) % 768;
				context.FillRect(new CGRect(offset, 0, 22, 768));
				context.FillRect(new CGRect(0, offset, 768, 18));
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
		WeakReference<Payload> ImagePayload,
		WeakReference<NSData> Data,
		WeakReference<Payload> DataPayload);
}

sealed record ProbeResult(
	int Iterations,
	int PayloadBytes,
	int RetainedControlFileResults,
	int RetainedCurrentFileResults,
	int ControlImagesRetained,
	int ControlImagePayloadsRetained,
	int ControlDataRetained,
	int ControlDataPayloadsRetained,
	int CurrentImagesRetained,
	int CurrentImagePayloadsRetained,
	int CurrentDataRetained,
	int CurrentDataPayloadsRetained,
	long ManagedHeapBytes)
{
	public bool ProvedLeak =>
		RetainedControlFileResults == Iterations &&
		RetainedCurrentFileResults == Iterations &&
		ControlImagePayloadsRetained == 0 &&
		ControlDataPayloadsRetained == 0 &&
		CurrentImagesRetained == Iterations &&
		CurrentImagePayloadsRetained == Iterations &&
		CurrentDataRetained == Iterations &&
		CurrentDataPayloadsRetained == Iterations;

	public string ToReport()
	{
		var retainedPayloadMiB = (CurrentImagePayloadsRetained + CurrentDataPayloadsRetained) * PayloadBytes / 1024.0 / 1024.0;
		var heapMiB = ManagedHeapBytes / 1024.0 / 1024.0;

		return string.Join(Environment.NewLine, new[]
		{
			"MediaPickerUIImageFileResultRetentionLeakRepro",
			$"Iterations: {Iterations}",
			$"Payload bytes per native object: {PayloadBytes}",
			$"Control retained file results: {RetainedControlFileResults}/{Iterations}",
			$"Current retained file results: {RetainedCurrentFileResults}/{Iterations}",
			$"Control retained UIImages: {ControlImagesRetained}/{Iterations}",
			$"Control retained UIImage payloads: {ControlImagePayloadsRetained}/{Iterations}",
			$"Control retained NSDatas: {ControlDataRetained}/{Iterations}",
			$"Control retained NSData payloads: {ControlDataPayloadsRetained}/{Iterations}",
			$"Current retained UIImages: {CurrentImagesRetained}/{Iterations}",
			$"Current retained UIImage payloads: {CurrentImagePayloadsRetained}/{Iterations}",
			$"Current retained NSDatas: {CurrentDataRetained}/{Iterations}",
			$"Current retained NSData payloads: {CurrentDataPayloadsRetained}/{Iterations}",
			$"Retained payload estimate: {retainedPayloadMiB:F1} MiB",
			$"Managed heap after proof: {heapMiB:F1} MiB",
			$"Proved leak: {ProvedLeak}"
		});
	}
}
