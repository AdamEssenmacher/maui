using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Hosting;

var options = ReproOptions.Parse(args);
var probe = new MauiHandlersFactoryTypeCacheRetentionProbe(options);
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

sealed class MauiHandlersFactoryTypeCacheRetentionProbe
{
	readonly ReproOptions _options;

	public MauiHandlersFactoryTypeCacheRetentionProbe(ReproOptions options)
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
		using var provider = CreateProvider();
		var factory = provider.GetRequiredService<IMauiHandlersFactory>();
		ClearFactoryServiceCache(factory);

		var assemblyRefs = new List<WeakReference>(_options.TypeCount);
		var typeRefs = new List<WeakReference>(_options.TypeCount);
		var payloadRefs = new List<WeakReference>(_options.TypeCount);

		for (var i = 0; i < _options.TypeCount; i++)
			CreateDynamicViewType(i, factory, assemblyRefs, typeRefs, payloadRefs);

		if (clearServiceCacheBeforeCollect)
			ClearFactoryServiceCache(factory);

		CollectHard();

		var retainedPayloads = payloadRefs.Count(static wr => wr.IsAlive);

		return new ScenarioResult(
			FactoryCacheEntryCount: GetFactoryServiceCacheCount(factory),
			RetainedAssemblyCount: assemblyRefs.Count(static wr => wr.IsAlive),
			RetainedTypeCount: typeRefs.Count(static wr => wr.IsAlive),
			RetainedPayloadCount: retainedPayloads,
			RetainedPayloadBytes: (long)retainedPayloads * _options.PayloadBytes);
	}

	static ServiceProvider CreateProvider()
	{
		var services = new ServiceCollection();
		services.ConfigureMauiHandlers(static handlers =>
		{
			handlers.AddHandler(typeof(BoxView), typeof(NullHandler));
		});

		return services.BuildServiceProvider();
	}

	void CreateDynamicViewType(
		int index,
		IMauiHandlersFactory factory,
		List<WeakReference> assemblyRefs,
		List<WeakReference> typeRefs,
		List<WeakReference> payloadRefs)
	{
		var assemblyName = new AssemblyName($"MauiHandlersFactoryTypeCacheRetentionRepro{index}");
		var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndCollect);
		var moduleBuilder = assemblyBuilder.DefineDynamicModule("Main");
		var typeBuilder = moduleBuilder.DefineType(
			$"DynamicTenantBoxView{index}",
			TypeAttributes.Public | TypeAttributes.Class,
			typeof(BoxView));
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

		var handlerType = factory.GetHandlerType(type);
		if (handlerType != typeof(NullHandler))
			throw new InvalidOperationException($"Expected {typeof(NullHandler)}, got {handlerType}.");
	}

	static void ClearFactoryServiceCache(IMauiHandlersFactory factory)
	{
		var field = factory.GetType().GetField("_serviceCache", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(factory.GetType().FullName, "_serviceCache");

		if (field.GetValue(factory) is IDictionary dictionary)
			dictionary.Clear();
	}

	static int GetFactoryServiceCacheCount(IMauiHandlersFactory factory)
	{
		var field = factory.GetType().GetField("_serviceCache", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(factory.GetType().FullName, "_serviceCache");
		var cache = field.GetValue(factory)
			?? throw new InvalidOperationException("_serviceCache was null.");

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

sealed class NullHandler : IElementHandler
{
	public object? PlatformView => null;
	public IElement? VirtualView { get; private set; }
	public IMauiContext? MauiContext { get; private set; }

	public void SetMauiContext(IMauiContext mauiContext) => MauiContext = mauiContext;
	public void SetVirtualView(IElement view) => VirtualView = view;
	public void UpdateValue(string property) { }
	public void Invoke(string command, object? args = null) { }
	public void DisconnectHandler() { }
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
	int FactoryCacheEntryCount,
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
			MauiHandlersFactory concrete Type cache retention repro

			Dynamic view types: {Options.TypeCount}
			Payload per type: {Options.PayloadBytes / 1024 / 1024} MiB

			Control: explicit _serviceCache.Clear()
			  Factory cache entries: {Control.FactoryCacheEntryCount}
			  Retained assemblies: {Control.RetainedAssemblyCount}/{Options.TypeCount}
			  Retained types: {Control.RetainedTypeCount}/{Options.TypeCount}
			  Retained payloads: {Control.RetainedPayloadCount}/{Options.TypeCount}
			  Retained payload bytes: {Control.RetainedPayloadBytes:N0}

			Current MAUI: _serviceCache left intact
			  Factory cache entries: {Current.FactoryCacheEntryCount}
			  Retained assemblies: {Current.RetainedAssemblyCount}/{Options.TypeCount}
			  Retained types: {Current.RetainedTypeCount}/{Options.TypeCount}
			  Retained payloads: {Current.RetainedPayloadCount}/{Options.TypeCount}
			  Retained payload bytes: {Current.RetainedPayloadBytes:N0}
			""";
	}
}
