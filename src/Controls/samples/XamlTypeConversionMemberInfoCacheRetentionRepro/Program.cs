using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Xaml;

var options = ReproOptions.Parse(args);
var probe = new XamlTypeConversionMemberInfoCacheProbe(options);
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

sealed class XamlTypeConversionMemberInfoCacheProbe
{
	const string Xaml = "<Root Number=\"123\" />";

	readonly ReproOptions _options;
	readonly MethodInfo _loadMethod;
	readonly FieldInfo _allowImplicitXmlnsField;
	readonly FieldInfo _converterCacheField;

	public XamlTypeConversionMemberInfoCacheProbe(ReproOptions options)
	{
		_options = options;

		var xamlAssembly = typeof(ReferenceExtension).Assembly;
		var controlsAssembly = typeof(BindableObject).Assembly;
		var xamlLoader = xamlAssembly.GetType("Microsoft.Maui.Controls.Xaml.XamlLoader", throwOnError: true)!;
		var xamlParser = xamlAssembly.GetType("Microsoft.Maui.Controls.Xaml.XamlParser", throwOnError: true)!;
		var typeConversionExtensions = controlsAssembly.GetType("Microsoft.Maui.Controls.Xaml.TypeConversionExtensions", throwOnError: true)!;

		_loadMethod = xamlLoader.GetMethod(
			"Load",
			BindingFlags.Public | BindingFlags.Static,
			binder: null,
			[typeof(object), typeof(string), typeof(Assembly), typeof(bool)],
			modifiers: null)!;

		_allowImplicitXmlnsField = xamlParser.GetField(
			"s_allowImplicitXmlns",
			BindingFlags.NonPublic | BindingFlags.Static)!;

		_converterCacheField = typeConversionExtensions.GetField(
			"s_converterCache",
			BindingFlags.NonPublic | BindingFlags.Static)!;
	}

	public ReproReport Run()
	{
		var control = RunScenario(clearConverterCacheBeforeCollect: true);
		var current = RunScenario(clearConverterCacheBeforeCollect: false);

		return new ReproReport(_options, control, current);
	}

	ScenarioResult RunScenario(bool clearConverterCacheBeforeCollect)
	{
		ClearAllowImplicitXmlnsCache();
		ClearConverterCache();

		var assemblyRefs = new List<WeakReference>(_options.TypeCount);
		var typeRefs = new List<WeakReference>(_options.TypeCount);
		var payloadRefs = new List<WeakReference>(_options.TypeCount);

		for (var i = 0; i < _options.TypeCount; i++)
			CreateDynamicRootAndLoadXaml(i, assemblyRefs, typeRefs, payloadRefs);

		// Isolate this repro from C430 by clearing the known assembly-keyed cache.
		ClearAllowImplicitXmlnsCache();

		if (clearConverterCacheBeforeCollect)
			ClearConverterCache();

		CollectHard();

		var retainedPayloads = payloadRefs.Count(static wr => wr.IsAlive);

		return new ScenarioResult(
			ConverterCacheEntryCount: GetConverterCacheCount(),
			RetainedAssemblyCount: assemblyRefs.Count(static wr => wr.IsAlive),
			RetainedTypeCount: typeRefs.Count(static wr => wr.IsAlive),
			RetainedPayloadCount: retainedPayloads,
			RetainedPayloadBytes: (long)retainedPayloads * _options.PayloadBytes);
	}

	void CreateDynamicRootAndLoadXaml(
		int index,
		List<WeakReference> assemblyRefs,
		List<WeakReference> typeRefs,
		List<WeakReference> payloadRefs)
	{
		var assemblyName = new AssemblyName($"MauiTypeConversionMemberInfoCacheRetentionRepro{index}");
		var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndCollect);
		var moduleBuilder = assemblyBuilder.DefineDynamicModule("Main");
		var typeBuilder = moduleBuilder.DefineType($"DynamicRoot{index}", TypeAttributes.Public | TypeAttributes.Class);
		var payloadField = typeBuilder.DefineField("Payload", typeof(byte[]), FieldAttributes.Public | FieldAttributes.Static);
		var numberField = typeBuilder.DefineField("_number", typeof(int), FieldAttributes.Private);
		var property = typeBuilder.DefineProperty("Number", PropertyAttributes.None, typeof(int), Type.EmptyTypes);

		var getMethod = typeBuilder.DefineMethod(
			"get_Number",
			MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
			typeof(int),
			Type.EmptyTypes);
		var getIl = getMethod.GetILGenerator();
		getIl.Emit(OpCodes.Ldarg_0);
		getIl.Emit(OpCodes.Ldfld, numberField);
		getIl.Emit(OpCodes.Ret);

		var setMethod = typeBuilder.DefineMethod(
			"set_Number",
			MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
			null,
			[typeof(int)]);
		var setIl = setMethod.GetILGenerator();
		setIl.Emit(OpCodes.Ldarg_0);
		setIl.Emit(OpCodes.Ldarg_1);
		setIl.Emit(OpCodes.Stfld, numberField);
		setIl.Emit(OpCodes.Ret);

		property.SetGetMethod(getMethod);
		property.SetSetMethod(setMethod);

		var type = typeBuilder.CreateType()!;
		var root = Activator.CreateInstance(type)!;
		var payload = new byte[_options.PayloadBytes];
		type.GetField(payloadField.Name)!.SetValue(null, payload);

		assemblyRefs.Add(new WeakReference(assemblyBuilder, trackResurrection: false));
		typeRefs.Add(new WeakReference(type, trackResurrection: false));
		payloadRefs.Add(new WeakReference(payload, trackResurrection: false));

		_loadMethod.Invoke(null, [root, Xaml, assemblyBuilder, false]);
	}

	void ClearAllowImplicitXmlnsCache()
	{
		if (_allowImplicitXmlnsField.GetValue(null) is System.Collections.IDictionary cache)
			cache.Clear();
	}

	void ClearConverterCache()
	{
		var cache = _converterCacheField.GetValue(null);
		cache?.GetType().GetMethod("Clear")?.Invoke(cache, null);
	}

	int GetConverterCacheCount()
	{
		var cache = _converterCacheField.GetValue(null);
		return cache?.GetType().GetProperty("Count")?.GetValue(cache) is int count ? count : -1;
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
	int ConverterCacheEntryCount,
	int RetainedAssemblyCount,
	int RetainedTypeCount,
	int RetainedPayloadCount,
	long RetainedPayloadBytes);

sealed record ReproReport(ReproOptions Options, ScenarioResult Control, ScenarioResult Current)
{
	public override string ToString()
	{
		var payloadMiB = Options.PayloadBytes / 1024.0 / 1024.0;
		return string.Join(
			Environment.NewLine,
			"XAML TypeConversionExtensions MemberInfo cache retention repro",
			$"Dynamic types: {Options.TypeCount}",
			$"Payload per type: {payloadMiB:F1} MiB",
			$"Control cleared converter cache: cache={Control.ConverterCacheEntryCount}, assemblies={Control.RetainedAssemblyCount}/{Options.TypeCount}, types={Control.RetainedTypeCount}/{Options.TypeCount}, payloads={Control.RetainedPayloadCount}/{Options.TypeCount}, retainedPayload={ToMiB(Control.RetainedPayloadBytes):F1} MiB",
			$"Current MAUI: cache={Current.ConverterCacheEntryCount}, assemblies={Current.RetainedAssemblyCount}/{Options.TypeCount}, types={Current.RetainedTypeCount}/{Options.TypeCount}, payloads={Current.RetainedPayloadCount}/{Options.TypeCount}, retainedPayload={ToMiB(Current.RetainedPayloadBytes):F1} MiB");
	}

	static double ToMiB(long bytes) => bytes / 1024.0 / 1024.0;
}
