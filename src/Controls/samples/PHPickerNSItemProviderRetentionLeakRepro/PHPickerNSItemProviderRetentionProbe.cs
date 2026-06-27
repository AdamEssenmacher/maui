using System.Reflection;
using System.Runtime.CompilerServices;
using Foundation;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;

namespace PHPickerNSItemProviderRetentionLeakRepro;

static class PHPickerNSItemProviderRetentionProbe
{
	const int Iterations = 80;
	const int PayloadBytes = 1024 * 1024;
	const int ProviderDataBytes = 1024 * 1024;
	const string JpegTypeIdentifier = "public.jpeg";

	static readonly Type CurrentResultType =
		typeof(MediaPicker).Assembly.GetType("Microsoft.Maui.Media.PHPickerFileResult")
		?? throw new InvalidOperationException("Could not find PHPickerFileResult.");

	static readonly ConstructorInfo CurrentResultConstructor =
		CurrentResultType.GetConstructor(
			BindingFlags.Instance | BindingFlags.NonPublic,
			binder: null,
			types: new[] { typeof(NSItemProvider) },
			modifiers: null)
		?? throw new InvalidOperationException("Could not find PHPickerFileResult(NSItemProvider) constructor.");

	static readonly FieldInfo CurrentProviderField =
		CurrentResultType.GetField("_provider", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new InvalidOperationException("Could not find PHPickerFileResult._provider.");

	public static async Task<ProbeResult> RunAsync()
	{
		var providerPayloads = new ConditionalWeakTable<NSItemProvider, Payload>();
		var dataPayloads = new ConditionalWeakTable<NSData, Payload>();
		var controlResults = new List<FileResult>(Iterations);
		var currentResults = new List<FileResult>(Iterations);
		var controlRefs = new List<ScenarioRefs>(Iterations);
		var currentRefs = new List<ScenarioRefs>(Iterations);

		for (var i = 0; i < Iterations; i++)
			controlRefs.Add(await CreateControlScenarioAsync(controlResults, providerPayloads, dataPayloads, i));

		for (var i = 0; i < Iterations; i++)
			currentRefs.Add(await CreateCurrentScenarioAsync(currentResults, providerPayloads, dataPayloads, i));

		ForceCollect();

		return new ProbeResult(
			Iterations,
			PayloadBytes,
			ProviderDataBytes,
			controlResults.Count,
			currentResults.Count,
			CountAlive(controlRefs, static r => r.Provider),
			CountAlive(controlRefs, static r => r.ProviderPayload),
			CountAlive(controlRefs, static r => r.Data),
			CountAlive(controlRefs, static r => r.DataPayload),
			CountAlive(currentRefs, static r => r.Provider),
			CountAlive(currentRefs, static r => r.ProviderPayload),
			CountAlive(currentRefs, static r => r.Data),
			CountAlive(currentRefs, static r => r.DataPayload),
			GC.GetTotalMemory(forceFullCollection: true));
	}

	static async Task<ScenarioRefs> CreateControlScenarioAsync(
		List<FileResult> retainedResults,
		ConditionalWeakTable<NSItemProvider, Payload> providerPayloads,
		ConditionalWeakTable<NSData, Payload> dataPayloads,
		int index)
	{
		var data = CreateProviderData(index);
		var provider = CreateProvider(data, index);
		var providerPayload = new Payload(index, PayloadBytes);
		var dataPayload = new Payload(index + Iterations, PayloadBytes);
		providerPayloads.Add(provider, providerPayload);
		dataPayloads.Add(data, dataPayload);

		var tempPath = Path.Combine(Path.GetTempPath(), $"maui-phpicker-provider-control-{Guid.NewGuid():N}.jpg");
		File.WriteAllBytes(tempPath, data.ToArray());
		retainedResults.Add(new FileResult(tempPath));

		var refs = new ScenarioRefs(
			new WeakReference<NSItemProvider>(provider),
			new WeakReference<Payload>(providerPayload),
			new WeakReference<NSData>(data),
			new WeakReference<Payload>(dataPayload));

		provider.Dispose();
		data.Dispose();
		await Task.Yield();
		return refs;
	}

	static async Task<ScenarioRefs> CreateCurrentScenarioAsync(
		List<FileResult> retainedResults,
		ConditionalWeakTable<NSItemProvider, Payload> providerPayloads,
		ConditionalWeakTable<NSData, Payload> dataPayloads,
		int index)
	{
		var data = CreateProviderData(index);
		var provider = CreateProvider(data, index);
		var providerPayload = new Payload(index, PayloadBytes);
		var dataPayload = new Payload(index + Iterations, PayloadBytes);
		providerPayloads.Add(provider, providerPayload);
		dataPayloads.Add(data, dataPayload);

		var result = (FileResult)CurrentResultConstructor.Invoke(new object?[] { provider });
		retainedResults.Add(result);

		var retainedProvider = (NSItemProvider?)CurrentProviderField.GetValue(result)
			?? throw new InvalidOperationException("Current result did not retain the NSItemProvider.");

		var refs = new ScenarioRefs(
			new WeakReference<NSItemProvider>(retainedProvider),
			new WeakReference<Payload>(providerPayload),
			new WeakReference<NSData>(data),
			new WeakReference<Payload>(dataPayload));

		await Task.Yield();
		return refs;
	}

	static NSData CreateProviderData(int index)
	{
		var bytes = new byte[ProviderDataBytes];
		for (var i = 0; i < bytes.Length; i++)
			bytes[i] = (byte)((i + index * 31) % 251);

		return NSData.FromArray(bytes);
	}

	static NSItemProvider CreateProvider(NSData data, int index)
	{
		var provider = new NSItemProvider(data, JpegTypeIdentifier)
		{
			SuggestedName = $"picked-photo-{index:D3}"
		};

		if (!provider.RegisteredTypeIdentifiers.Contains(JpegTypeIdentifier))
			throw new InvalidOperationException("Synthetic provider did not register JPEG data.");

		return provider;
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
		WeakReference<NSItemProvider> Provider,
		WeakReference<Payload> ProviderPayload,
		WeakReference<NSData> Data,
		WeakReference<Payload> DataPayload);
}

sealed record ProbeResult(
	int Iterations,
	int PayloadBytes,
	int ProviderDataBytes,
	int RetainedControlFileResults,
	int RetainedCurrentFileResults,
	int ControlProvidersRetained,
	int ControlProviderPayloadsRetained,
	int ControlDataRetained,
	int ControlDataPayloadsRetained,
	int CurrentProvidersRetained,
	int CurrentProviderPayloadsRetained,
	int CurrentDataRetained,
	int CurrentDataPayloadsRetained,
	long ManagedHeapBytes)
{
	public bool ProvedLeak =>
		RetainedControlFileResults == Iterations &&
		RetainedCurrentFileResults == Iterations &&
		ControlProviderPayloadsRetained == 0 &&
		CurrentProvidersRetained == Iterations &&
		CurrentProviderPayloadsRetained == Iterations;

	public string ToReport()
	{
		var retainedPayloadMiB = CurrentProviderPayloadsRetained * PayloadBytes / 1024.0 / 1024.0;
		var retainedProviderDataMiB = CurrentDataRetained * ProviderDataBytes / 1024.0 / 1024.0;
		var heapMiB = ManagedHeapBytes / 1024.0 / 1024.0;

		return string.Join(Environment.NewLine, new[]
		{
			"PHPickerNSItemProviderRetentionLeakRepro",
			$"Iterations: {Iterations}",
			$"Provider data bytes: {ProviderDataBytes}",
			$"Payload bytes per provider: {PayloadBytes}",
			$"Control retained file results: {RetainedControlFileResults}/{Iterations}",
			$"Current retained file results: {RetainedCurrentFileResults}/{Iterations}",
			$"Control retained NSItemProviders: {ControlProvidersRetained}/{Iterations}",
			$"Control retained provider payloads: {ControlProviderPayloadsRetained}/{Iterations}",
			$"Control retained NSDatas: {ControlDataRetained}/{Iterations}",
			$"Control retained NSData payloads: {ControlDataPayloadsRetained}/{Iterations}",
			$"Current retained NSItemProviders: {CurrentProvidersRetained}/{Iterations}",
			$"Current retained provider payloads: {CurrentProviderPayloadsRetained}/{Iterations}",
			$"Current retained NSDatas: {CurrentDataRetained}/{Iterations}",
			$"Current retained NSData payloads: {CurrentDataPayloadsRetained}/{Iterations}",
			$"Retained provider payload estimate: {retainedPayloadMiB:F1} MiB",
			$"Retained provider data object estimate: {retainedProviderDataMiB:F1} MiB",
			$"Managed heap after proof: {heapMiB:F1} MiB",
			$"Proved leak: {ProvedLeak}"
		});
	}
}
