using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;

var options = ReproOptions.Parse(args);
var probe = new ImageSourceServiceProviderImageSourceCacheRetentionProbe(options);
var report = probe.Run();

Console.WriteLine(report);

if (!string.IsNullOrWhiteSpace(options.ResultsPath))
{
	var resultsDirectory = Path.GetDirectoryName(Path.GetFullPath(options.ResultsPath));
	if (!string.IsNullOrEmpty(resultsDirectory))
		Directory.CreateDirectory(resultsDirectory);

	File.WriteAllText(options.ResultsPath, report.ToString());
}

return report.Proven ? 0 : 1;

sealed class ImageSourceServiceProviderImageSourceCacheRetentionProbe
{
	readonly ReproOptions _options;

	public ImageSourceServiceProviderImageSourceCacheRetentionProbe(ReproOptions options)
	{
		_options = options;
	}

	public ReproReport Run()
	{
		var control = RunScenario(clearImageSourceCacheBeforeCollect: true);
		var current = RunScenario(clearImageSourceCacheBeforeCollect: false);

		return new ReproReport(_options, control, current);
	}

	ScenarioResult RunScenario(bool clearImageSourceCacheBeforeCollect)
	{
		using var app = CreateApp();
		var provider = app.Services.GetRequiredService<IImageSourceServiceProvider>();
		ClearCache(provider, "_serviceCache");
		ClearCache(provider, "_imageSourceCache");

		var assemblyRefs = new List<WeakReference<Assembly>>(_options.TypeCount);
		var imageSourceTypeRefs = new List<WeakReference<Type>>(_options.TypeCount);
		var imageSourceInterfaceRefs = new List<WeakReference<Type>>(_options.TypeCount);
		var payloadRefs = new List<WeakReference<byte[]>>(_options.TypeCount);

		CollectHard();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		CreateDynamicImageSourceTypes(
			provider,
			assemblyRefs,
			imageSourceTypeRefs,
			imageSourceInterfaceRefs,
			payloadRefs);

		var serviceCacheEntriesBeforeCollect = GetCacheCount(provider, "_serviceCache");
		var imageSourceCacheEntriesBeforeCollect = GetCacheCount(provider, "_imageSourceCache");

		if (clearImageSourceCacheBeforeCollect)
			ClearCache(provider, "_imageSourceCache");

		CollectHard();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var retainedPayloads = CountAlive(payloadRefs);
		return new ScenarioResult(
			ServiceCacheEntriesBeforeCollect: serviceCacheEntriesBeforeCollect,
			ServiceCacheEntriesAfterCollect: GetCacheCount(provider, "_serviceCache"),
			ImageSourceCacheEntriesBeforeCollect: imageSourceCacheEntriesBeforeCollect,
			ImageSourceCacheEntriesAfterCollect: GetCacheCount(provider, "_imageSourceCache"),
			RetainedAssemblyCount: CountAlive(assemblyRefs),
			RetainedImageSourceTypeCount: CountAlive(imageSourceTypeRefs),
			RetainedImageSourceInterfaceCount: CountAlive(imageSourceInterfaceRefs),
			RetainedPayloadCount: retainedPayloads,
			RetainedPayloadBytes: (long)retainedPayloads * _options.PayloadBytes,
			HeapBeforeBytes: heapBefore,
			HeapAfterBytes: heapAfter);
	}

	static MauiApp CreateApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder.ConfigureImageSources(static _ =>
		{
		});

		return builder.Build();
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	void CreateDynamicImageSourceTypes(
		IImageSourceServiceProvider provider,
		List<WeakReference<Assembly>> assemblyRefs,
		List<WeakReference<Type>> imageSourceTypeRefs,
		List<WeakReference<Type>> imageSourceInterfaceRefs,
		List<WeakReference<byte[]>> payloadRefs)
	{
		for (var i = 0; i < _options.TypeCount; i++)
		{
			CreateDynamicImageSourceType(
				i,
				out var imageSourceType,
				out var imageSourceInterfaceType,
				out var payload);

			assemblyRefs.Add(new WeakReference<Assembly>(imageSourceType.Assembly, trackResurrection: false));
			imageSourceTypeRefs.Add(new WeakReference<Type>(imageSourceType, trackResurrection: false));
			imageSourceInterfaceRefs.Add(new WeakReference<Type>(imageSourceInterfaceType, trackResurrection: false));
			payloadRefs.Add(new WeakReference<byte[]>(payload, trackResurrection: false));

#pragma warning disable CS0618
			var resolvedImageSourceType = provider.GetImageSourceType(imageSourceType);
#pragma warning restore CS0618
			if (resolvedImageSourceType != imageSourceInterfaceType)
				throw new InvalidOperationException($"Expected {imageSourceInterfaceType}, got {resolvedImageSourceType}.");
		}
	}

	void CreateDynamicImageSourceType(
		int index,
		out Type imageSourceType,
		out Type imageSourceInterfaceType,
		out byte[] payload)
	{
		var assemblyName = new AssemblyName($"MauiImageSourceCacheRetentionRepro{index}");
		var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndCollect);
		var moduleBuilder = assemblyBuilder.DefineDynamicModule("Main");

		var interfaceBuilder = moduleBuilder.DefineType(
			$"IPluginImageSource{index}",
			TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
		interfaceBuilder.AddInterfaceImplementation(typeof(IImageSource));
		imageSourceInterfaceType = interfaceBuilder.CreateType()!;

		var typeBuilder = moduleBuilder.DefineType(
			$"PluginImageSource{index}",
			TypeAttributes.Public | TypeAttributes.Class,
			typeof(object),
			new[] { imageSourceInterfaceType });
		var payloadField = typeBuilder.DefineField(
			"Payload",
			typeof(byte[]),
			FieldAttributes.Public | FieldAttributes.Static);
		var isEmptyProperty = typeBuilder.DefineProperty(
			nameof(IImageSource.IsEmpty),
			PropertyAttributes.None,
			typeof(bool),
			Type.EmptyTypes);
		var isEmptyGetter = typeBuilder.DefineMethod(
			"get_" + nameof(IImageSource.IsEmpty),
			MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
			typeof(bool),
			Type.EmptyTypes);
		isEmptyGetter.GetILGenerator().Emit(OpCodes.Ldc_I4_0);
		isEmptyGetter.GetILGenerator().Emit(OpCodes.Ret);
		isEmptyProperty.SetGetMethod(isEmptyGetter);
		var baseGetter = typeof(IImageSource).GetProperty(nameof(IImageSource.IsEmpty))?.GetMethod
			?? throw new MissingMethodException(typeof(IImageSource).FullName, "get_" + nameof(IImageSource.IsEmpty));
		typeBuilder.DefineMethodOverride(isEmptyGetter, baseGetter);

		imageSourceType = typeBuilder.CreateType()!;
		payload = new byte[_options.PayloadBytes];
		for (var offset = 0; offset < payload.Length; offset += 4096)
			payload[offset] = (byte)(index % 251);

		imageSourceType.GetField(payloadField.Name)!.SetValue(null, payload);
	}

	static void ClearCache(IImageSourceServiceProvider provider, string fieldName)
	{
		if (GetCache(provider, fieldName) is IDictionary dictionary)
			dictionary.Clear();
	}

	static int GetCacheCount(IImageSourceServiceProvider provider, string fieldName)
	{
		var cache = GetCache(provider, fieldName);
		return cache.GetType().GetProperty("Count")?.GetValue(cache) is int count
			? count
			: -1;
	}

	static object GetCache(IImageSourceServiceProvider provider, string fieldName)
	{
		var field = provider.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(provider.GetType().FullName, fieldName);

		return field.GetValue(provider)
			?? throw new InvalidOperationException($"{fieldName} was null.");
	}

	static void CollectHard()
	{
		for (var i = 0; i < 8; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(50);
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
}

sealed record ReproOptions(int TypeCount, int PayloadBytes, string? ResultsPath)
{
	public static ReproOptions Parse(string[] args)
	{
		var typeCount = 80;
		var payloadMiB = 1;
		string? resultsPath = null;

		foreach (var arg in args)
		{
			if (arg.StartsWith("--count=", StringComparison.Ordinal))
			{
				typeCount = int.Parse(arg["--count=".Length..]);
			}
			else if (arg.StartsWith("--payload-mib=", StringComparison.Ordinal))
			{
				payloadMiB = int.Parse(arg["--payload-mib=".Length..]);
			}
			else if (arg.StartsWith("--results=", StringComparison.Ordinal))
			{
				resultsPath = arg["--results=".Length..];
			}
		}

		if (typeCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(typeCount));
		if (payloadMiB <= 0)
			throw new ArgumentOutOfRangeException(nameof(payloadMiB));

		return new ReproOptions(typeCount, payloadMiB * 1024 * 1024, resultsPath);
	}
}

sealed record ScenarioResult(
	int ServiceCacheEntriesBeforeCollect,
	int ServiceCacheEntriesAfterCollect,
	int ImageSourceCacheEntriesBeforeCollect,
	int ImageSourceCacheEntriesAfterCollect,
	int RetainedAssemblyCount,
	int RetainedImageSourceTypeCount,
	int RetainedImageSourceInterfaceCount,
	int RetainedPayloadCount,
	long RetainedPayloadBytes,
	long HeapBeforeBytes,
	long HeapAfterBytes)
{
	public long HeapDeltaBytes => HeapAfterBytes - HeapBeforeBytes;
}

sealed record ReproReport(ReproOptions Options, ScenarioResult Control, ScenarioResult Current)
{
	public bool Proven =>
		Control.ServiceCacheEntriesAfterCollect == 0 &&
		Control.ImageSourceCacheEntriesAfterCollect == 0 &&
		Control.RetainedAssemblyCount == 0 &&
		Control.RetainedImageSourceTypeCount == 0 &&
		Control.RetainedImageSourceInterfaceCount == 0 &&
		Control.RetainedPayloadCount == 0 &&
		Current.ServiceCacheEntriesAfterCollect == 0 &&
		Current.ImageSourceCacheEntriesAfterCollect == Options.TypeCount &&
		Current.RetainedAssemblyCount == Options.TypeCount &&
		Current.RetainedImageSourceTypeCount == Options.TypeCount &&
		Current.RetainedImageSourceInterfaceCount == Options.TypeCount &&
		Current.RetainedPayloadCount == Options.TypeCount;

	public override string ToString()
	{
		var writer = new StringWriter();
		writer.WriteLine("ImageSourceServiceProvider image-source Type cache retention repro");
		writer.WriteLine($"Result: {(Proven ? "PROVEN" : "NOT PROVEN")}");
		writer.WriteLine();
		writer.WriteLine("Trigger:");
		writer.WriteLine("  The obsolete public IImageSourceServiceProvider.GetImageSourceType(Type) API maps concrete image-source types to image-source interfaces.");
		writer.WriteLine("  ImageSourceServiceProvider caches each concrete runtime Type key in its private _imageSourceCache dictionary.");
		writer.WriteLine("  There is no public cache eviction path while the app-lifetime provider lives.");
		writer.WriteLine("  Plugin/module image-source types can therefore stay rooted after the plugin should unload.");
		writer.WriteLine();
		writer.WriteLine($"Dynamic image-source types: {Options.TypeCount}");
		writer.WriteLine($"Payload per dynamic image-source type: {Options.PayloadBytes / 1024 / 1024} MiB");
		writer.WriteLine();
		WriteScenario(writer, "Control: ImageSourceServiceProvider._imageSourceCache cleared before forced GC", Control);
		writer.WriteLine();
		WriteScenario(writer, "Current MAUI: ImageSourceServiceProvider._imageSourceCache left intact", Current);
		return writer.ToString();
	}

	static void WriteScenario(StringWriter writer, string title, ScenarioResult result)
	{
		writer.WriteLine(title);
		writer.WriteLine($"  Service cache entries before collect: {result.ServiceCacheEntriesBeforeCollect}");
		writer.WriteLine($"  Service cache entries after collect: {result.ServiceCacheEntriesAfterCollect}");
		writer.WriteLine($"  Image-source cache entries before collect: {result.ImageSourceCacheEntriesBeforeCollect}");
		writer.WriteLine($"  Image-source cache entries after collect: {result.ImageSourceCacheEntriesAfterCollect}");
		writer.WriteLine($"  Retained assemblies: {result.RetainedAssemblyCount}");
		writer.WriteLine($"  Retained image-source types: {result.RetainedImageSourceTypeCount}");
		writer.WriteLine($"  Retained image-source interfaces: {result.RetainedImageSourceInterfaceCount}");
		writer.WriteLine($"  Retained payloads: {result.RetainedPayloadCount}");
		writer.WriteLine($"  Retained payload bytes: {result.RetainedPayloadBytes:N0}");
		writer.WriteLine($"  Managed heap delta: {result.HeapDeltaBytes:N0} bytes");
	}
}
