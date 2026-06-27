#nullable enable

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Android.Graphics;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;

namespace AndroidScreenshotResultBitmapRetentionLeakRepro;

public sealed class MainPage : ContentPage
{
	readonly Label _status;

	public MainPage()
	{
		_status = new Label
		{
			Text = "Running Android ScreenshotResult bitmap retention probe...",
			Margin = 24
		};

		Content = _status;
		Loaded += OnLoaded;
	}

	async void OnLoaded(object? sender, EventArgs e)
	{
		Loaded -= OnLoaded;

		var result = await Task.Run(AndroidScreenshotResultBitmapRetentionProbe.Run);
		var text = result.ToReport();
		_status.Text = text;

		var resultsPath = System.IO.Path.Combine(FileSystem.AppDataDirectory, "autorun-results.txt");
		File.WriteAllText(resultsPath, text);
		Android.Util.Log.Info("AndroidScreenshotResultBitmapRetentionLeakRepro", text);

		await Task.Delay(250);
		Android.OS.Process.KillProcess(Android.OS.Process.MyPid());
	}
}

static class AndroidScreenshotResultBitmapRetentionProbe
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
			types: new[] { typeof(Bitmap) },
			modifiers: null)
		?? throw new InvalidOperationException("Could not find ScreenshotResult(Bitmap) constructor.");

	static readonly FieldInfo CurrentBitmapField =
		CurrentResultType.GetField("bmp", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new InvalidOperationException("Could not find ScreenshotResult.bmp.");

	public static ProbeResult Run()
	{
		var bitmapPayloads = new ConditionalWeakTable<Bitmap, Payload>();
		var controlResults = new List<IScreenshotResult>(Iterations);
		var currentResults = new List<IScreenshotResult>(Iterations);
		var controlRefs = new List<ScenarioRefs>(Iterations);
		var currentRefs = new List<ScenarioRefs>(Iterations);

		for (var i = 0; i < Iterations; i++)
			controlRefs.Add(CreateControlScenario(controlResults, bitmapPayloads, i));

		for (var i = 0; i < Iterations; i++)
			currentRefs.Add(CreateCurrentScenario(currentResults, bitmapPayloads, i));

		ForceCollect();

		return new ProbeResult(
			Iterations,
			PayloadBytes,
			ImageWidth,
			ImageHeight,
			BytesPerPixel,
			controlResults.Count,
			currentResults.Count,
			CountAlive(controlRefs, static r => r.Bitmap),
			CountAlive(controlRefs, static r => r.BitmapPayload),
			CountAlive(currentRefs, static r => r.Bitmap),
			CountAlive(currentRefs, static r => r.BitmapPayload),
			GC.GetTotalMemory(forceFullCollection: true));
	}

	static ScenarioRefs CreateControlScenario(
		List<IScreenshotResult> retainedResults,
		ConditionalWeakTable<Bitmap, Payload> bitmapPayloads,
		int index)
	{
		var bitmap = CreateBitmap(index);
		var bitmapPayload = new Payload(index, PayloadBytes);
		bitmapPayloads.Add(bitmap, bitmapPayload);

		using var encoded = new MemoryStream();
		bitmap.Compress(Bitmap.CompressFormat.Jpeg!, 85, encoded);
		retainedResults.Add(new ByteArrayScreenshotResult(ImageWidth, ImageHeight, encoded.ToArray()));

		var refs = new ScenarioRefs(
			new WeakReference<Bitmap>(bitmap),
			new WeakReference<Payload>(bitmapPayload));

		bitmap.Recycle();
		bitmap.Dispose();
		return refs;
	}

	static ScenarioRefs CreateCurrentScenario(
		List<IScreenshotResult> retainedResults,
		ConditionalWeakTable<Bitmap, Payload> bitmapPayloads,
		int index)
	{
		var bitmap = CreateBitmap(index);
		var bitmapPayload = new Payload(index, PayloadBytes);
		bitmapPayloads.Add(bitmap, bitmapPayload);

		var result = (IScreenshotResult)CurrentResultConstructor.Invoke(new object[] { bitmap });
		retainedResults.Add(result);

		using (result.OpenReadAsync(ScreenshotFormat.Jpeg, quality: 85).GetAwaiter().GetResult())
		{
		}

		var retainedBitmap = (Bitmap?)CurrentBitmapField.GetValue(result)
			?? throw new InvalidOperationException("Current ScreenshotResult did not retain the Bitmap.");

		return new ScenarioRefs(
			new WeakReference<Bitmap>(retainedBitmap),
			new WeakReference<Payload>(bitmapPayload));
	}

	static Bitmap CreateBitmap(int index)
	{
		var bitmap = Bitmap.CreateBitmap(ImageWidth, ImageHeight, Bitmap.Config.Argb8888!)
			?? throw new InvalidOperationException("Failed to create Bitmap.");

		using var canvas = new Canvas(bitmap);
		using var paint = new Android.Graphics.Paint(PaintFlags.AntiAlias);
		paint.SetARGB(255, (index * 47) % 255, (index * 83) % 255, (index * 113) % 255);
		canvas.DrawRect(0, 0, ImageWidth, ImageHeight, paint);

		paint.SetARGB(92, 255, 255, 255);
		for (var stripe = 0; stripe < 16; stripe++)
		{
			var offset = (index * 19 + stripe * 71) % ImageWidth;
			canvas.DrawRect(offset, 0, offset + 18, ImageHeight, paint);
			canvas.DrawRect(0, offset, ImageWidth, offset + 14, paint);
		}

		return bitmap;
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
			Java.Lang.JavaSystem.Gc();
			Thread.Sleep(150);
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
		WeakReference<Bitmap> Bitmap,
		WeakReference<Payload> BitmapPayload);
}

sealed record ProbeResult(
	int Iterations,
	int PayloadBytes,
	int ImageWidth,
	int ImageHeight,
	int BytesPerPixel,
	int RetainedControlResults,
	int RetainedCurrentResults,
	int ControlBitmapsRetained,
	int ControlBitmapPayloadsRetained,
	int CurrentBitmapsRetained,
	int CurrentBitmapPayloadsRetained,
	long ManagedHeapBytes)
{
	public bool ProvedLeak =>
		RetainedControlResults == Iterations &&
		RetainedCurrentResults == Iterations &&
		ControlBitmapPayloadsRetained == 0 &&
		CurrentBitmapsRetained == Iterations &&
		CurrentBitmapPayloadsRetained == Iterations;

	public string ToReport()
	{
		var builder = new StringBuilder();
		var retainedPayloadMiB = CurrentBitmapPayloadsRetained * PayloadBytes / 1024.0 / 1024.0;
		var retainedNativeMiB = CurrentBitmapsRetained * ImageWidth * ImageHeight * BytesPerPixel / 1024.0 / 1024.0;
		var heapMiB = ManagedHeapBytes / 1024.0 / 1024.0;

		builder.AppendLine("AndroidScreenshotResultBitmapRetentionLeakRepro");
		builder.AppendLine(ProvedLeak ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
		builder.AppendLine($"Iterations: {Iterations}");
		builder.AppendLine($"Screenshot size: {ImageWidth}x{ImageHeight} at {BytesPerPixel} bytes/pixel");
		builder.AppendLine($"Payload bytes per native Bitmap: {PayloadBytes}");
		builder.AppendLine($"Control retained screenshot results: {RetainedControlResults}/{Iterations}");
		builder.AppendLine($"Current retained screenshot results: {RetainedCurrentResults}/{Iterations}");
		builder.AppendLine($"Control retained Bitmaps: {ControlBitmapsRetained}/{Iterations}");
		builder.AppendLine($"Control retained Bitmap payloads: {ControlBitmapPayloadsRetained}/{Iterations}");
		builder.AppendLine($"Current retained Bitmaps: {CurrentBitmapsRetained}/{Iterations}");
		builder.AppendLine($"Current retained Bitmap payloads: {CurrentBitmapPayloadsRetained}/{Iterations}");
		builder.AppendLine($"Retained payload estimate: {retainedPayloadMiB:F1} MiB");
		builder.AppendLine($"Estimated native Bitmap pixel memory: {retainedNativeMiB:F1} MiB");
		builder.AppendLine($"Managed heap after proof: {heapMiB:F1} MiB");
		builder.Append("Proved leak: ");
		builder.Append(ProvedLeak);
		return builder.ToString();
	}
}
