using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Hosting;

var options = ReproOptions.Parse(args);
var probe = new ImageSourceServiceProviderTypeCacheRetentionProbe(options);
var report = probe.Run();

Console.WriteLine(report);

if (!string.IsNullOrWhiteSpace(options.ResultsPath))
{
	var resultsDirectory = Path.GetDirectoryName(Path.GetFullPath(options.ResultsPath));
	if (!string.IsNullOrEmpty(resultsDirectory))
		Directory.CreateDirectory(resultsDirectory);

	File.WriteAllText(options.ResultsPath, report.ToString());
}

return report.Current.RetainedTypeCount == options.TypeCount
	&& report.Current.RetainedPayloadCount == options.TypeCount
	&& report.Control.RetainedTypeCount == 0
	&& report.Control.RetainedPayloadCount == 0
	? 0
	: 1;

sealed class ImageSourceServiceProviderTypeCacheRetentionProbe
{
	readonly ReproOptions _options;

	public ImageSourceServiceProviderTypeCacheRetentionProbe(ReproOptions options)
	{
		_options = options;
	}

	public ReproReport Run()
	{
		var control = RunScenario(clearServiceCacheBeforeCollect: true);
		var current = RunScenario(clearServiceCacheBeforeCollect: false);

		return new ReproReport(_options, control, current);
	}

	ScenarioResult RunScenario(bool clearServiceCacheBeforeCollect)
	{
		using var app = CreateApp();
		var provider = app.Services.GetRequiredService<IImageSourceServiceProvider>();
		ClearCache(provider, "_serviceCache");
		ClearCache(provider, "_imageSourceCache");

		var assemblyRefs = new List<WeakReference>(_options.TypeCount);
		var typeRefs = new List<WeakReference>(_options.TypeCount);
		var payloadRefs = new List<WeakReference>(_options.TypeCount);

		for (var i = 0; i < _options.TypeCount; i++)
			CreateDynamicImageSourceType(i, provider, assemblyRefs, typeRefs, payloadRefs);

		if (clearServiceCacheBeforeCollect)
			ClearCache(provider, "_serviceCache");

		CollectHard();

		var retainedPayloads = payloadRefs.Count(static wr => wr.IsAlive);

		return new ScenarioResult(
			ServiceCacheEntryCount: GetCacheCount(provider, "_serviceCache"),
			ImageSourceCacheEntryCount: GetCacheCount(provider, "_imageSourceCache"),
			RetainedAssemblyCount: assemblyRefs.Count(static wr => wr.IsAlive),
			RetainedTypeCount: typeRefs.Count(static wr => wr.IsAlive),
			RetainedPayloadCount: retainedPayloads,
			RetainedPayloadBytes: (long)retainedPayloads * _options.PayloadBytes);
	}

	static MauiApp CreateApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder.ConfigureImageSources(static services =>
		{
			services.AddService<ImageSource, NullImageSourceService>();
		});

		return builder.Build();
	}

	void CreateDynamicImageSourceType(
		int index,
		IImageSourceServiceProvider provider,
		List<WeakReference> assemblyRefs,
		List<WeakReference> typeRefs,
		List<WeakReference> payloadRefs)
	{
		var assemblyName = new AssemblyName($"ImageSourceServiceProviderTypeCacheRetentionRepro{index}");
		var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndCollect);
		var moduleBuilder = assemblyBuilder.DefineDynamicModule("Main");
		var typeBuilder = moduleBuilder.DefineType(
			$"DynamicTenantImageSource{index}",
			TypeAttributes.Public | TypeAttributes.Class,
			typeof(ImageSource));
		var payloadField = typeBuilder.DefineField(
			"Payload",
			typeof(byte[]),
			FieldAttributes.Public | FieldAttributes.Static);

		var type = typeBuilder.CreateType()!;
		var payload = new byte[_options.PayloadBytes];
		type.GetField(payloadField.Name)!.SetValue(null, payload);

		assemblyRefs.Add(new WeakReference(type.Assembly, trackResurrection: false));
		typeRefs.Add(new WeakReference(type, trackResurrection: false));
		payloadRefs.Add(new WeakReference(payload, trackResurrection: false));

		var service = provider.GetImageSourceService(type);
		if (service is not NullImageSourceService)
			throw new InvalidOperationException($"Expected {typeof(NullImageSourceService)}, got {service?.GetType()}.");
	}

	static void ClearCache(IImageSourceServiceProvider provider, string fieldName)
	{
		var field = provider.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(provider.GetType().FullName, fieldName);

		if (field.GetValue(provider) is IDictionary dictionary)
			dictionary.Clear();
	}

	static int GetCacheCount(IImageSourceServiceProvider provider, string fieldName)
	{
		var field = provider.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(provider.GetType().FullName, fieldName);
		var cache = field.GetValue(provider)
			?? throw new InvalidOperationException($"{fieldName} was null.");

		return cache.GetType().GetProperty("Count")?.GetValue(cache) is int count
			? count
			: -1;
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
}

sealed class NullImageSourceService : IImageSourceService<ImageSource>
{
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
	int ServiceCacheEntryCount,
	int ImageSourceCacheEntryCount,
	int RetainedAssemblyCount,
	int RetainedTypeCount,
	int RetainedPayloadCount,
	long RetainedPayloadBytes);

sealed record ReproReport(
	ReproOptions Options,
	ScenarioResult Control,
	ScenarioResult Current)
{
	public override string ToString()
	{
		return $"""
			ImageSourceServiceProvider concrete Type cache retention repro

			Dynamic image-source types: {Options.TypeCount}
			Payload per type: {Options.PayloadBytes / 1024 / 1024} MiB

			Control: explicit _serviceCache.Clear()
			  Service cache entries: {Control.ServiceCacheEntryCount}
			  Image-source cache entries: {Control.ImageSourceCacheEntryCount}
			  Retained assemblies: {Control.RetainedAssemblyCount}/{Options.TypeCount}
			  Retained types: {Control.RetainedTypeCount}/{Options.TypeCount}
			  Retained payloads: {Control.RetainedPayloadCount}/{Options.TypeCount}
			  Retained payload bytes: {Control.RetainedPayloadBytes:N0}

			Current MAUI: _serviceCache left intact
			  Service cache entries: {Current.ServiceCacheEntryCount}
			  Image-source cache entries: {Current.ImageSourceCacheEntryCount}
			  Retained assemblies: {Current.RetainedAssemblyCount}/{Options.TypeCount}
			  Retained types: {Current.RetainedTypeCount}/{Options.TypeCount}
			  Retained payloads: {Current.RetainedPayloadCount}/{Options.TypeCount}
			  Retained payload bytes: {Current.RetainedPayloadBytes:N0}
			""";
	}
}
