using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Maui.Controls.Xaml;

var options = ReproOptions.Parse(args);
var probe = new XamlLoaderAssemblyCacheProbe(options);
var report = probe.Run();

Console.WriteLine(report);

if (!string.IsNullOrWhiteSpace(options.ResultsPath))
{
	var resultsDirectory = Path.GetDirectoryName(Path.GetFullPath(options.ResultsPath));
	if (!string.IsNullOrEmpty(resultsDirectory))
		Directory.CreateDirectory(resultsDirectory);

	File.WriteAllText(options.ResultsPath, report.ToString());
}

return report.Current.RetainedPayloadCount == options.AssemblyCount
	&& report.Control.RetainedPayloadCount == 0
	? 0
	: 1;

sealed class XamlLoaderAssemblyCacheProbe
{
	const string Xaml = "<ContentView xmlns=\"http://schemas.microsoft.com/dotnet/2021/maui\" />";

	readonly ReproOptions _options;
	readonly MethodInfo _loadMethod;
	readonly FieldInfo _allowImplicitXmlnsField;

	public XamlLoaderAssemblyCacheProbe(ReproOptions options)
	{
		_options = options;

		var xamlAssembly = typeof(ReferenceExtension).Assembly;
		var xamlLoader = xamlAssembly.GetType("Microsoft.Maui.Controls.Xaml.XamlLoader", throwOnError: true)!;
		var xamlParser = xamlAssembly.GetType("Microsoft.Maui.Controls.Xaml.XamlParser", throwOnError: true)!;

		_loadMethod = xamlLoader.GetMethod(
			"Load",
			BindingFlags.Public | BindingFlags.Static,
			binder: null,
			[typeof(object), typeof(string), typeof(Assembly), typeof(bool)],
			modifiers: null)!;

		_allowImplicitXmlnsField = xamlParser.GetField(
			"s_allowImplicitXmlns",
			BindingFlags.NonPublic | BindingFlags.Static)!;
	}

	public ReproReport Run()
	{
		var control = RunScenario(clearStaticCacheBeforeCollect: true);
		var current = RunScenario(clearStaticCacheBeforeCollect: false);

		return new ReproReport(_options, control, current);
	}

	ScenarioResult RunScenario(bool clearStaticCacheBeforeCollect)
	{
		ClearCache();

		var assemblyRefs = new List<WeakReference>(_options.AssemblyCount);
		var payloadRefs = new List<WeakReference>(_options.AssemblyCount);

		for (var i = 0; i < _options.AssemblyCount; i++)
			CreateAssemblyAndTouchXamlLoader(i, assemblyRefs, payloadRefs);

		if (clearStaticCacheBeforeCollect)
			ClearCache();

		CollectHard();

		var cacheEntries = GetCacheCount();
		var retainedAssemblies = assemblyRefs.Count(static wr => wr.IsAlive);
		var retainedPayloads = payloadRefs.Count(static wr => wr.IsAlive);

		return new ScenarioResult(
			CacheEntryCount: cacheEntries,
			RetainedAssemblyCount: retainedAssemblies,
			RetainedPayloadCount: retainedPayloads,
			RetainedPayloadBytes: (long)retainedPayloads * _options.PayloadBytes);
	}

	void CreateAssemblyAndTouchXamlLoader(
		int index,
		List<WeakReference> assemblyRefs,
		List<WeakReference> payloadRefs)
	{
		var assemblyName = new AssemblyName($"MauiXamlLoaderAssemblyCacheRetentionRepro{index}");
		var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndCollect);
		var moduleBuilder = assemblyBuilder.DefineDynamicModule("Main");
		var typeBuilder = moduleBuilder.DefineType($"PayloadHolder{index}", TypeAttributes.Public | TypeAttributes.Class);
		var fieldBuilder = typeBuilder.DefineField("Payload", typeof(byte[]), FieldAttributes.Public | FieldAttributes.Static);
		var type = typeBuilder.CreateType()!;
		var payload = new byte[_options.PayloadBytes];

		type.GetField(fieldBuilder.Name)!.SetValue(null, payload);

		assemblyRefs.Add(new WeakReference(assemblyBuilder, trackResurrection: false));
		payloadRefs.Add(new WeakReference(payload, trackResurrection: false));

		try
		{
			_loadMethod.Invoke(null, [new object(), Xaml, assemblyBuilder, false]);
		}
		catch (TargetInvocationException)
		{
			// The leak is isolated before XAML hydration: XamlLoader caches rootAssembly
			// before parsing or applying the XAML to the supplied root object.
		}
	}

	void ClearCache()
	{
		if (_allowImplicitXmlnsField.GetValue(null) is System.Collections.IDictionary cache)
			cache.Clear();
	}

	int GetCacheCount()
		=> _allowImplicitXmlnsField.GetValue(null) is System.Collections.IDictionary cache
			? cache.Count
			: -1;

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

sealed record ReproOptions(int AssemblyCount, int PayloadBytes, string? ResultsPath)
{
	public static ReproOptions Parse(string[] args)
	{
		var assemblyCount = 80;
		var payloadMiB = 1;
		string? resultsPath = null;

		foreach (var arg in args)
		{
			if (arg.StartsWith("--count=", StringComparison.Ordinal))
			{
				assemblyCount = int.Parse(arg["--count=".Length..]);
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

		if (assemblyCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(assemblyCount));
		if (payloadMiB <= 0)
			throw new ArgumentOutOfRangeException(nameof(payloadMiB));

		return new ReproOptions(assemblyCount, payloadMiB * 1024 * 1024, resultsPath);
	}
}

sealed record ScenarioResult(
	int CacheEntryCount,
	int RetainedAssemblyCount,
	int RetainedPayloadCount,
	long RetainedPayloadBytes);

sealed record ReproReport(ReproOptions Options, ScenarioResult Control, ScenarioResult Current)
{
	public override string ToString()
	{
		var payloadMiB = Options.PayloadBytes / 1024.0 / 1024.0;
		return string.Join(
			Environment.NewLine,
			"XamlLoader assembly cache retention repro",
			$"Assemblies: {Options.AssemblyCount}",
			$"Payload per assembly: {payloadMiB:F1} MiB",
			$"Control cleared cache: cache={Control.CacheEntryCount}, assemblies={Control.RetainedAssemblyCount}/{Options.AssemblyCount}, payloads={Control.RetainedPayloadCount}/{Options.AssemblyCount}, retainedPayload={ToMiB(Control.RetainedPayloadBytes):F1} MiB",
			$"Current MAUI: cache={Current.CacheEntryCount}, assemblies={Current.RetainedAssemblyCount}/{Options.AssemblyCount}, payloads={Current.RetainedPayloadCount}/{Options.AssemblyCount}, retainedPayload={ToMiB(Current.RetainedPayloadBytes):F1} MiB");
	}

	static double ToMiB(long bytes) => bytes / 1024.0 / 1024.0;
}
