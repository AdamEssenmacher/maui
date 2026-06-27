using System.Reflection;
using System.Runtime.CompilerServices;
using Foundation;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;

namespace PHPickerProcessedOriginalRetentionLeakRepro;

static class PHPickerProcessedOriginalRetentionProbe
{
	const int Iterations = 80;
	const int OriginalFileBytes = 1024 * 1024;
	const int ProviderPayloadBytes = 1024 * 1024;
	const string JpegTypeIdentifier = "public.jpeg";

	static readonly Type CurrentResultType =
		typeof(MediaPicker).Assembly.GetType("Microsoft.Maui.Media.PHPickerProcessedFileResult")
		?? throw new InvalidOperationException("Could not find PHPickerProcessedFileResult.");

	static readonly ConstructorInfo CurrentResultConstructor =
		CurrentResultType.GetConstructor(
			BindingFlags.Instance | BindingFlags.NonPublic,
			binder: null,
			types:
			[
				typeof(FileResult),
				typeof(int?),
				typeof(int?),
				typeof(int),
				typeof(bool),
				typeof(bool)
			],
			modifiers: null)
		?? throw new InvalidOperationException("Could not find PHPickerProcessedFileResult constructor.");

	static readonly FieldInfo CurrentOriginalResultField =
		CurrentResultType.GetField("_originalResult", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new InvalidOperationException("Could not find PHPickerProcessedFileResult._originalResult.");

	static readonly FieldInfo CurrentCachedDataField =
		CurrentResultType.GetField("_cachedData", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new InvalidOperationException("Could not find PHPickerProcessedFileResult._cachedData.");

	public static async Task<ProbeResult> RunAsync()
	{
		var providerPayloads = new ConditionalWeakTable<NSItemProvider, Payload>();
		var controlResults = new List<ControlProcessedResult>(Iterations);
		var currentResults = new List<FileResult>(Iterations);
		var controlRefs = new List<ScenarioRefs>(Iterations);
		var currentRefs = new List<ScenarioRefs>(Iterations);

		for (var i = 0; i < Iterations; i++)
			controlRefs.Add(await CreateControlScenarioAsync(controlResults, providerPayloads, i));

		for (var i = 0; i < Iterations; i++)
			currentRefs.Add(await CreateCurrentScenarioAsync(currentResults, providerPayloads, i));

		ForceCollect();

		return new ProbeResult(
			Iterations,
			OriginalFileBytes,
			ProviderPayloadBytes,
			controlResults.Count,
			currentResults.Count,
			CountAlive(controlRefs, static r => r.ProcessedCachedData),
			CountAlive(currentRefs, static r => r.ProcessedCachedData),
			CountAlive(controlRefs, static r => r.OriginalResult),
			CountAlive(controlRefs, static r => r.Provider),
			CountAlive(controlRefs, static r => r.ProviderPayload),
			CountAlive(controlRefs, static r => r.OriginalSourceBytes),
			CountAlive(currentRefs, static r => r.OriginalResult),
			CountAlive(currentRefs, static r => r.Provider),
			CountAlive(currentRefs, static r => r.ProviderPayload),
			CountAlive(currentRefs, static r => r.OriginalSourceBytes),
			GC.GetTotalMemory(forceFullCollection: true));
	}

	static async Task<ScenarioRefs> CreateControlScenarioAsync(
		List<ControlProcessedResult> retainedResults,
		ConditionalWeakTable<NSItemProvider, Payload> providerPayloads,
		int index)
	{
		var original = CreateProviderBackedOriginal(providerPayloads, index);
		var result = new ControlProcessedResult(original);
		retainedResults.Add(result);
		var provider = original.Provider;
		var providerPayload = original.ProviderPayload;
		var originalSourceBytes = original.OriginalSourceBytes;

		await OpenAndDrainAsync(result);
		var cachedData = result.CachedData ?? throw new InvalidOperationException("Control result did not cache data.");
		DeleteQuietly(original.FullPath);

		var refs = new ScenarioRefs(
			new WeakReference<FileResult>(original),
			new WeakReference<NSItemProvider>(provider),
			new WeakReference<Payload>(providerPayload),
			new WeakReference<byte[]>(originalSourceBytes),
			new WeakReference<byte[]>(cachedData));

		await Task.Yield();
		return refs;
	}

	static async Task<ScenarioRefs> CreateCurrentScenarioAsync(
		List<FileResult> retainedResults,
		ConditionalWeakTable<NSItemProvider, Payload> providerPayloads,
		int index)
	{
		var original = CreateProviderBackedOriginal(providerPayloads, index + Iterations);
		var provider = original.Provider;
		var providerPayload = original.ProviderPayload;
		var originalSourceBytes = original.OriginalSourceBytes;
		var result = (FileResult)CurrentResultConstructor.Invoke(
			new object?[]
			{
				original,
				100,
				100,
				75,
				false,
				true
			});
		retainedResults.Add(result);

		await OpenAndDrainAsync(result);
		DeleteQuietly(original.FullPath);

		var retainedOriginal = (FileResult?)CurrentOriginalResultField.GetValue(result)
			?? throw new InvalidOperationException("Current processed result did not retain the original result.");
		var cachedData = (byte[]?)CurrentCachedDataField.GetValue(result)
			?? throw new InvalidOperationException("Current processed result did not cache data.");

		var refs = new ScenarioRefs(
			new WeakReference<FileResult>(retainedOriginal),
			new WeakReference<NSItemProvider>(provider),
			new WeakReference<Payload>(providerPayload),
			new WeakReference<byte[]>(originalSourceBytes),
			new WeakReference<byte[]>(cachedData));

		await Task.Yield();
		return refs;
	}

	static ProviderBackedFileResult CreateProviderBackedOriginal(
		ConditionalWeakTable<NSItemProvider, Payload> providerPayloads,
		int index)
	{
		var bytes = CreateOriginalBytes(index);
		var tempPath = Path.Combine(Path.GetTempPath(), $"maui-phpicker-processed-original-{Guid.NewGuid():N}.jpg");
		File.WriteAllBytes(tempPath, bytes);

		NSItemProvider provider;
		using (var data = NSData.FromArray([(byte)(index % 251)]))
		{
			provider = new NSItemProvider(data, JpegTypeIdentifier)
			{
				SuggestedName = $"picked-photo-{index:D3}"
			};
		}

		var providerPayload = new Payload(index, ProviderPayloadBytes);
		providerPayloads.Add(provider, providerPayload);

		return new ProviderBackedFileResult(tempPath, provider, providerPayload, bytes)
		{
			FileName = $"picked-photo-{index:D3}.jpg",
			ContentType = "image/jpeg"
		};
	}

	static byte[] CreateOriginalBytes(int index)
	{
		var bytes = new byte[OriginalFileBytes];
		for (var i = 0; i < bytes.Length; i++)
			bytes[i] = (byte)((i + index * 17) % 251);

		return bytes;
	}

	static async Task OpenAndDrainAsync(FileResult result)
	{
		using var stream = await result.OpenReadAsync();
		await DrainAsync(stream);
	}

	static async Task OpenAndDrainAsync(ControlProcessedResult result)
	{
		using var stream = await result.OpenReadAsync();
		await DrainAsync(stream);
	}

	static async Task DrainAsync(Stream stream)
	{
		var buffer = new byte[64 * 1024];
		while (await stream.ReadAsync(buffer) > 0)
		{
		}
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

	static void DeleteQuietly(string path)
	{
		try
		{
			if (File.Exists(path))
				File.Delete(path);
		}
		catch
		{
		}
	}

	sealed class ProviderBackedFileResult : FileResult, IDisposable
	{
		NSItemProvider? _provider;
		Payload? _providerPayload;
		byte[]? _originalSourceBytes;

		public ProviderBackedFileResult(string path, NSItemProvider provider, Payload providerPayload, byte[] originalSourceBytes)
			: base(path)
		{
			_provider = provider;
			_providerPayload = providerPayload;
			_originalSourceBytes = originalSourceBytes;
		}

		public NSItemProvider Provider =>
			_provider ?? throw new ObjectDisposedException(nameof(ProviderBackedFileResult));

		public Payload ProviderPayload =>
			_providerPayload ?? throw new ObjectDisposedException(nameof(ProviderBackedFileResult));

		public byte[] OriginalSourceBytes =>
			_originalSourceBytes ?? throw new ObjectDisposedException(nameof(ProviderBackedFileResult));

		public void Dispose()
		{
			_provider?.Dispose();
			_provider = null;
			_providerPayload = null;
			_originalSourceBytes = null;
			GC.SuppressFinalize(this);
		}
	}

	sealed class ControlProcessedResult
	{
		FileResult? _originalResult;
		byte[]? _cachedData;

		public ControlProcessedResult(FileResult originalResult)
		{
			_originalResult = originalResult;
		}

		public byte[]? CachedData => _cachedData;

		public async Task<Stream> OpenReadAsync()
		{
			if (_cachedData is null)
			{
				using (var originalStream = await _originalResult!.OpenReadAsync())
				using (var buffer = new MemoryStream())
				{
					await originalStream.CopyToAsync(buffer);
					_cachedData = buffer.ToArray();
				}

				if (_originalResult is IDisposable disposable)
					disposable.Dispose();

				_originalResult = null;
			}

			return new MemoryStream(_cachedData, writable: false);
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
		WeakReference<FileResult> OriginalResult,
		WeakReference<NSItemProvider> Provider,
		WeakReference<Payload> ProviderPayload,
		WeakReference<byte[]> OriginalSourceBytes,
		WeakReference<byte[]> ProcessedCachedData);
}

sealed record ProbeResult(
	int Iterations,
	int OriginalFileBytes,
	int ProviderPayloadBytes,
	int RetainedControlProcessedResults,
	int RetainedCurrentProcessedResults,
	int ControlCachedDataRetained,
	int CurrentCachedDataRetained,
	int ControlOriginalResultsRetained,
	int ControlProvidersRetained,
	int ControlProviderPayloadsRetained,
	int ControlOriginalSourceBytesRetained,
	int CurrentOriginalResultsRetained,
	int CurrentProvidersRetained,
	int CurrentProviderPayloadsRetained,
	int CurrentOriginalSourceBytesRetained,
	long ManagedHeapBytes)
{
	public bool ProvedLeak =>
		RetainedControlProcessedResults == Iterations &&
		RetainedCurrentProcessedResults == Iterations &&
		ControlCachedDataRetained == Iterations &&
		CurrentCachedDataRetained == Iterations &&
		ControlOriginalResultsRetained == 0 &&
		ControlProvidersRetained == 0 &&
		ControlProviderPayloadsRetained == 0 &&
		ControlOriginalSourceBytesRetained == 0 &&
		CurrentOriginalResultsRetained == Iterations &&
		CurrentProvidersRetained == Iterations &&
		CurrentProviderPayloadsRetained == Iterations &&
		CurrentOriginalSourceBytesRetained == Iterations;

	public string ToReport()
	{
		var retainedProviderPayloadMiB = CurrentProviderPayloadsRetained * ProviderPayloadBytes / 1024.0 / 1024.0;
		var retainedOriginalBytesMiB = CurrentOriginalSourceBytesRetained * OriginalFileBytes / 1024.0 / 1024.0;
		var controlCachedMiB = ControlCachedDataRetained * OriginalFileBytes / 1024.0 / 1024.0;
		var currentCachedMiB = CurrentCachedDataRetained * OriginalFileBytes / 1024.0 / 1024.0;
		var heapMiB = ManagedHeapBytes / 1024.0 / 1024.0;

		return string.Join(Environment.NewLine, new[]
		{
			"PHPickerProcessedOriginalRetentionLeakRepro",
			$"Iterations: {Iterations}",
			$"Original file bytes per result: {OriginalFileBytes}",
			$"Provider payload bytes: {ProviderPayloadBytes}",
			$"Control retained processed results: {RetainedControlProcessedResults}/{Iterations}",
			$"Current retained processed results: {RetainedCurrentProcessedResults}/{Iterations}",
			$"Control retained cached byte arrays: {ControlCachedDataRetained}/{Iterations}",
			$"Current retained cached byte arrays: {CurrentCachedDataRetained}/{Iterations}",
			$"Control retained original results: {ControlOriginalResultsRetained}/{Iterations}",
			$"Control retained NSItemProviders: {ControlProvidersRetained}/{Iterations}",
			$"Control retained provider payloads: {ControlProviderPayloadsRetained}/{Iterations}",
			$"Control retained original source byte arrays: {ControlOriginalSourceBytesRetained}/{Iterations}",
			$"Current retained original results: {CurrentOriginalResultsRetained}/{Iterations}",
			$"Current retained NSItemProviders: {CurrentProvidersRetained}/{Iterations}",
			$"Current retained provider payloads: {CurrentProviderPayloadsRetained}/{Iterations}",
			$"Current retained original source byte arrays: {CurrentOriginalSourceBytesRetained}/{Iterations}",
			$"Expected cached data retained in both paths: control {controlCachedMiB:F1} MiB, current {currentCachedMiB:F1} MiB",
			$"Extra provider payload retained by current path: {retainedProviderPayloadMiB:F1} MiB",
			$"Extra original source bytes retained by current path: {retainedOriginalBytesMiB:F1} MiB",
			$"Total extra retained estimate: {retainedProviderPayloadMiB + retainedOriginalBytesMiB:F1} MiB",
			$"Managed heap after proof: {heapMiB:F1} MiB",
			$"Proved leak: {ProvedLeak}"
		});
	}
}
