using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Controls;

var options = ReproOptions.Parse(args);
var probe = new DependencyServiceRegistrationTypeRetentionProbe(options);
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

sealed class DependencyServiceRegistrationTypeRetentionProbe
{
	static readonly FieldInfo DependencyTypesField =
		typeof(DependencyService).GetField("DependencyTypes", BindingFlags.Static | BindingFlags.NonPublic)
			?? throw new MissingFieldException(typeof(DependencyService).FullName, "DependencyTypes");

	static readonly FieldInfo DependencyImplementationsField =
		typeof(DependencyService).GetField("DependencyImplementations", BindingFlags.Static | BindingFlags.NonPublic)
			?? throw new MissingFieldException(typeof(DependencyService).FullName, "DependencyImplementations");

	static readonly MethodInfo RegisterMappingMethod =
		typeof(DependencyService).GetMethods(BindingFlags.Public | BindingFlags.Static)
			.Single(static method =>
				method.Name == nameof(DependencyService.Register)
				&& method.GetGenericArguments().Length == 2
				&& method.GetParameters().Length == 0);

	readonly ReproOptions _options;

	public DependencyServiceRegistrationTypeRetentionProbe(ReproOptions options)
	{
		_options = options;
	}

	public ReproReport Run()
	{
		var control = RunScenario(clearDependencyServiceBeforeCollect: true);
		var current = RunScenario(clearDependencyServiceBeforeCollect: false);

		ClearDependencyServiceTables();
		return new ReproReport(_options, control, current);
	}

	ScenarioResult RunScenario(bool clearDependencyServiceBeforeCollect)
	{
		ClearDependencyServiceTables();

		var assemblyRefs = new List<WeakReference<Assembly>>(_options.TypePairCount);
		var serviceTypeRefs = new List<WeakReference<Type>>(_options.TypePairCount);
		var implementorTypeRefs = new List<WeakReference<Type>>(_options.TypePairCount);
		var payloadRefs = new List<WeakReference<byte[]>>(_options.TypePairCount);

		CollectHard();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		for (var i = 0; i < _options.TypePairCount; i++)
			CreateDynamicDependencyPairAndRegister(i, assemblyRefs, serviceTypeRefs, implementorTypeRefs, payloadRefs);

		var dependencyTypesBeforeCollect = GetListCount(DependencyTypesField);
		var implementationsBeforeCollect = GetDictionaryCount(DependencyImplementationsField);

		if (clearDependencyServiceBeforeCollect)
			ClearDependencyServiceTables();

		CollectHard();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var retainedPayloads = CountAlive(payloadRefs);
		return new ScenarioResult(
			DependencyTypeCount: GetListCount(DependencyTypesField),
			DependencyImplementationCount: GetDictionaryCount(DependencyImplementationsField),
			DependencyTypesBeforeCollect: dependencyTypesBeforeCollect,
			DependencyImplementationsBeforeCollect: implementationsBeforeCollect,
			RetainedAssemblyCount: CountAlive(assemblyRefs),
			RetainedServiceTypeCount: CountAlive(serviceTypeRefs),
			RetainedImplementorTypeCount: CountAlive(implementorTypeRefs),
			RetainedPayloadCount: retainedPayloads,
			RetainedPayloadBytes: (long)retainedPayloads * _options.PayloadBytes,
			HeapBeforeBytes: heapBefore,
			HeapAfterBytes: heapAfter);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	void CreateDynamicDependencyPairAndRegister(
		int index,
		List<WeakReference<Assembly>> assemblyRefs,
		List<WeakReference<Type>> serviceTypeRefs,
		List<WeakReference<Type>> implementorTypeRefs,
		List<WeakReference<byte[]>> payloadRefs)
	{
		var assemblyName = new AssemblyName($"MauiDependencyServiceRegistrationRetentionRepro{index}");
		var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndCollect);
		var moduleBuilder = assemblyBuilder.DefineDynamicModule("Main");

		var serviceTypeBuilder = moduleBuilder.DefineType(
			$"ITenantDependencyService{index}",
			TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
		var serviceType = serviceTypeBuilder.CreateType()!;

		var implementorTypeBuilder = moduleBuilder.DefineType(
			$"TenantDependencyServiceImpl{index}",
			TypeAttributes.Public | TypeAttributes.Class);
		implementorTypeBuilder.AddInterfaceImplementation(serviceType);
		implementorTypeBuilder.DefineDefaultConstructor(MethodAttributes.Public);
		var payloadField = implementorTypeBuilder.DefineField(
			"Payload",
			typeof(byte[]),
			FieldAttributes.Public | FieldAttributes.Static);

		var implementorType = implementorTypeBuilder.CreateType()!;
		var payload = new byte[_options.PayloadBytes];
		for (var offset = 0; offset < payload.Length; offset += 4096)
			payload[offset] = (byte)(index % 251);

		implementorType.GetField(payloadField.Name)!.SetValue(null, payload);

		assemblyRefs.Add(new WeakReference<Assembly>(implementorType.Assembly, trackResurrection: false));
		serviceTypeRefs.Add(new WeakReference<Type>(serviceType, trackResurrection: false));
		implementorTypeRefs.Add(new WeakReference<Type>(implementorType, trackResurrection: false));
		payloadRefs.Add(new WeakReference<byte[]>(payload, trackResurrection: false));

		RegisterMappingMethod.MakeGenericMethod(serviceType, implementorType).Invoke(null, null);
	}

	static void ClearDependencyServiceTables()
	{
		if (DependencyTypesField.GetValue(null) is not IList dependencyTypes)
			throw new InvalidOperationException("DependencyTypes did not implement IList.");
		if (DependencyImplementationsField.GetValue(null) is not IDictionary dependencyImplementations)
			throw new InvalidOperationException("DependencyImplementations did not implement IDictionary.");

		dependencyTypes.Clear();
		dependencyImplementations.Clear();
	}

	static int GetListCount(FieldInfo field)
	{
		if (field.GetValue(null) is not IList list)
			throw new InvalidOperationException($"{field.Name} did not implement IList.");

		return list.Count;
	}

	static int GetDictionaryCount(FieldInfo field)
	{
		if (field.GetValue(null) is not IDictionary dictionary)
			throw new InvalidOperationException($"{field.Name} did not implement IDictionary.");

		return dictionary.Count;
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

sealed record ReproOptions(int TypePairCount, int PayloadBytes, string? ResultsPath)
{
	public static ReproOptions Parse(string[] args)
	{
		var typePairCount = 80;
		var payloadMiB = 1;
		string? resultsPath = null;

		foreach (var arg in args)
		{
			if (arg.StartsWith("--count=", StringComparison.Ordinal))
			{
				typePairCount = int.Parse(arg["--count=".Length..]);
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

		if (typePairCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(typePairCount));
		if (payloadMiB <= 0)
			throw new ArgumentOutOfRangeException(nameof(payloadMiB));

		return new ReproOptions(typePairCount, payloadMiB * 1024 * 1024, resultsPath);
	}
}

sealed record ScenarioResult(
	int DependencyTypeCount,
	int DependencyImplementationCount,
	int DependencyTypesBeforeCollect,
	int DependencyImplementationsBeforeCollect,
	int RetainedAssemblyCount,
	int RetainedServiceTypeCount,
	int RetainedImplementorTypeCount,
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
		Control.RetainedAssemblyCount == 0
		&& Control.RetainedServiceTypeCount == 0
		&& Control.RetainedImplementorTypeCount == 0
		&& Control.RetainedPayloadCount == 0
		&& Current.RetainedAssemblyCount == Options.TypePairCount
		&& Current.RetainedServiceTypeCount == Options.TypePairCount
		&& Current.RetainedImplementorTypeCount == Options.TypePairCount
		&& Current.RetainedPayloadCount == Options.TypePairCount;

	public override string ToString()
	{
		return $"""
			DependencyService registration Type retention repro
			Result: {(Proven ? "PROVEN" : "NOT PROVEN")}

			Trigger:
			  A process-static DependencyService registers many dynamic service/implementation type pairs.
			  DependencyService.Register<T,TImpl>() stores T in DependencyTypes and TImpl in DependencyImplementations.
			  There is no public unregister or eviction path for plugin/module unload.

			Dynamic service pairs: {Options.TypePairCount}
			Payload per implementor type: {Options.PayloadBytes / 1024 / 1024} MiB

			Control: explicit DependencyService table clear before forced GC
			  DependencyTypes before collect: {Control.DependencyTypesBeforeCollect}
			  DependencyImplementations before collect: {Control.DependencyImplementationsBeforeCollect}
			  DependencyTypes after collect: {Control.DependencyTypeCount}
			  DependencyImplementations after collect: {Control.DependencyImplementationCount}
			  Retained assemblies: {Control.RetainedAssemblyCount}/{Options.TypePairCount}
			  Retained service types: {Control.RetainedServiceTypeCount}/{Options.TypePairCount}
			  Retained implementor types: {Control.RetainedImplementorTypeCount}/{Options.TypePairCount}
			  Retained payloads: {Control.RetainedPayloadCount}/{Options.TypePairCount}
			  Retained payload bytes: {Control.RetainedPayloadBytes:N0}
			  Managed heap delta: {Control.HeapDeltaBytes:N0} bytes

			Current MAUI: DependencyService tables left intact
			  DependencyTypes before collect: {Current.DependencyTypesBeforeCollect}
			  DependencyImplementations before collect: {Current.DependencyImplementationsBeforeCollect}
			  DependencyTypes after collect: {Current.DependencyTypeCount}
			  DependencyImplementations after collect: {Current.DependencyImplementationCount}
			  Retained assemblies: {Current.RetainedAssemblyCount}/{Options.TypePairCount}
			  Retained service types: {Current.RetainedServiceTypeCount}/{Options.TypePairCount}
			  Retained implementor types: {Current.RetainedImplementorTypeCount}/{Options.TypePairCount}
			  Retained payloads: {Current.RetainedPayloadCount}/{Options.TypePairCount}
			  Retained payload bytes: {Current.RetainedPayloadBytes:N0}
			  Managed heap delta: {Current.HeapDeltaBytes:N0} bytes
			""";
	}
}
