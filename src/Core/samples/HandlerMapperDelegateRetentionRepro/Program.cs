using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Maui;
using Microsoft.Maui.Handlers;

var options = ReproOptions.Parse(args);
var probe = new HandlerMapperDelegateRetentionProbe(options);
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

sealed class HandlerMapperDelegateRetentionProbe
{
	const string PropertyKeyPrefix = "__HandlerMapperDelegateRetentionRepro_Property_";
	const string CommandKeyPrefix = "__HandlerMapperDelegateRetentionRepro_Command_";

	static readonly FieldInfo PropertyMapperDictionaryField =
		typeof(PropertyMapper).GetField("_mapper", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(typeof(PropertyMapper).FullName, "_mapper");

	static readonly FieldInfo CommandMapperDictionaryField =
		typeof(CommandMapper).GetField("_mapper", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(typeof(CommandMapper).FullName, "_mapper");

	readonly ReproOptions _options;

	public HandlerMapperDelegateRetentionProbe(ReproOptions options)
	{
		_options = options;
	}

	public ReproReport Run()
	{
		ClearReproMapperEntries();

		var control = RunScenario(clearMappingsBeforeCollect: true);
		var current = RunScenario(clearMappingsBeforeCollect: false);

		ClearReproMapperEntries();
		return new ReproReport(_options, control, current);
	}

	ScenarioResult RunScenario(bool clearMappingsBeforeCollect)
	{
		ClearReproMapperEntries();

		var assemblyRefs = new List<WeakReference<Assembly>>(_options.DynamicMappingsPerMapper * 2);
		var typeRefs = new List<WeakReference<Type>>(_options.DynamicMappingsPerMapper * 2);
		var payloadRefs = new List<WeakReference<byte[]>>(_options.DynamicMappingsPerMapper * 2);

		CollectHard();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		RegisterDynamicMappings(assemblyRefs, typeRefs, payloadRefs);

		var propertyEntriesBeforeCollect = CountReproPropertyEntries();
		var commandEntriesBeforeCollect = CountReproCommandEntries();

		if (clearMappingsBeforeCollect)
			ClearReproMapperEntries();

		CollectHard();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var retainedPayloads = CountAlive(payloadRefs);
		return new ScenarioResult(
			PropertyEntriesBeforeCollect: propertyEntriesBeforeCollect,
			CommandEntriesBeforeCollect: commandEntriesBeforeCollect,
			PropertyEntriesAfterCollect: CountReproPropertyEntries(),
			CommandEntriesAfterCollect: CountReproCommandEntries(),
			RetainedAssemblyCount: CountAlive(assemblyRefs),
			RetainedTypeCount: CountAlive(typeRefs),
			RetainedPayloadCount: retainedPayloads,
			RetainedPayloadBytes: (long)retainedPayloads * _options.PayloadBytes,
			HeapBeforeBytes: heapBefore,
			HeapAfterBytes: heapAfter);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	void RegisterDynamicMappings(
		List<WeakReference<Assembly>> assemblyRefs,
		List<WeakReference<Type>> typeRefs,
		List<WeakReference<byte[]>> payloadRefs)
	{
		for (var i = 0; i < _options.DynamicMappingsPerMapper; i++)
		{
			CreateDynamicMapperType(
				$"Property{i}",
				"MapProperty",
				new[] { typeof(IViewHandler), typeof(IView) },
				out var mapperType,
				out var payload);

			assemblyRefs.Add(new WeakReference<Assembly>(mapperType.Assembly, trackResurrection: false));
			typeRefs.Add(new WeakReference<Type>(mapperType, trackResurrection: false));
			payloadRefs.Add(new WeakReference<byte[]>(payload, trackResurrection: false));

			var method = mapperType.GetMethod("MapProperty", BindingFlags.Public | BindingFlags.Static)
				?? throw new MissingMethodException(mapperType.FullName, "MapProperty");
			var action = (Action<IViewHandler, IView>)Delegate.CreateDelegate(
				typeof(Action<IViewHandler, IView>),
				method);

			ViewHandler.ViewMapper.Add($"{PropertyKeyPrefix}{i}", action);
		}

		for (var i = 0; i < _options.DynamicMappingsPerMapper; i++)
		{
			CreateDynamicMapperType(
				$"Command{i}",
				"MapCommand",
				new[] { typeof(IViewHandler), typeof(IView), typeof(object) },
				out var mapperType,
				out var payload);

			assemblyRefs.Add(new WeakReference<Assembly>(mapperType.Assembly, trackResurrection: false));
			typeRefs.Add(new WeakReference<Type>(mapperType, trackResurrection: false));
			payloadRefs.Add(new WeakReference<byte[]>(payload, trackResurrection: false));

			var method = mapperType.GetMethod("MapCommand", BindingFlags.Public | BindingFlags.Static)
				?? throw new MissingMethodException(mapperType.FullName, "MapCommand");
			var action = (Action<IViewHandler, IView, object?>)Delegate.CreateDelegate(
				typeof(Action<IViewHandler, IView, object?>),
				method);

			ViewHandler.ViewCommandMapper.Add($"{CommandKeyPrefix}{i}", action);
		}
	}

	void CreateDynamicMapperType(
		string suffix,
		string methodName,
		Type[] parameterTypes,
		out Type mapperType,
		out byte[] payload)
	{
		var assemblyName = new AssemblyName($"MauiHandlerMapperDelegateRetentionRepro{suffix}");
		var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndCollect);
		var moduleBuilder = assemblyBuilder.DefineDynamicModule("Main");

		var typeBuilder = moduleBuilder.DefineType(
			$"Plugin{suffix}Mapper",
			TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed);
		var payloadField = typeBuilder.DefineField(
			"Payload",
			typeof(byte[]),
			FieldAttributes.Public | FieldAttributes.Static);
		var methodBuilder = typeBuilder.DefineMethod(
			methodName,
			MethodAttributes.Public | MethodAttributes.Static,
			typeof(void),
			parameterTypes);
		var il = methodBuilder.GetILGenerator();
		il.Emit(OpCodes.Ldsfld, payloadField);
		il.Emit(OpCodes.Pop);
		il.Emit(OpCodes.Ret);

		mapperType = typeBuilder.CreateType()!;
		payload = new byte[_options.PayloadBytes];
		for (var offset = 0; offset < payload.Length; offset += 4096)
			payload[offset] = (byte)(suffix.Length % 251);

		mapperType.GetField(payloadField.Name)!.SetValue(null, payload);
	}

	static int CountReproPropertyEntries()
	{
		return CountPrefixedEntries(GetPropertyMapperDictionary(), PropertyKeyPrefix);
	}

	static int CountReproCommandEntries()
	{
		return CountPrefixedEntries(GetCommandMapperDictionary(), CommandKeyPrefix);
	}

	static void ClearReproMapperEntries()
	{
		RemovePrefixedEntries(GetPropertyMapperDictionary(), PropertyKeyPrefix);
		RemovePrefixedEntries(GetCommandMapperDictionary(), CommandKeyPrefix);
	}

	static IDictionary GetPropertyMapperDictionary()
	{
		if (PropertyMapperDictionaryField.GetValue(ViewHandler.ViewMapper) is not IDictionary mapper)
			throw new InvalidOperationException("PropertyMapper._mapper did not implement IDictionary.");

		return mapper;
	}

	static IDictionary GetCommandMapperDictionary()
	{
		if (CommandMapperDictionaryField.GetValue(ViewHandler.ViewCommandMapper) is not IDictionary mapper)
			throw new InvalidOperationException("CommandMapper._mapper did not implement IDictionary.");

		return mapper;
	}

	static int CountPrefixedEntries(IDictionary mapper, string prefix)
	{
		var count = 0;
		foreach (var key in mapper.Keys)
		{
			if (key is string stringKey && stringKey.StartsWith(prefix, StringComparison.Ordinal))
				count++;
		}

		return count;
	}

	static void RemovePrefixedEntries(IDictionary mapper, string prefix)
	{
		var keys = new List<object>();
		foreach (var key in mapper.Keys)
		{
			if (key is string stringKey && stringKey.StartsWith(prefix, StringComparison.Ordinal))
				keys.Add(key);
		}

		foreach (var key in keys)
			mapper.Remove(key);
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

sealed record ReproOptions(int DynamicMappingsPerMapper, int PayloadBytes, string? ResultsPath)
{
	public int TotalDynamicMappings => DynamicMappingsPerMapper * 2;

	public static ReproOptions Parse(string[] args)
	{
		var dynamicMappingsPerMapper = 80;
		var payloadMiB = 1;
		string? resultsPath = null;

		foreach (var arg in args)
		{
			if (arg.StartsWith("--count-per-mapper=", StringComparison.Ordinal))
			{
				dynamicMappingsPerMapper = int.Parse(arg["--count-per-mapper=".Length..]);
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

		if (dynamicMappingsPerMapper <= 0)
			throw new ArgumentOutOfRangeException(nameof(dynamicMappingsPerMapper));
		if (payloadMiB <= 0)
			throw new ArgumentOutOfRangeException(nameof(payloadMiB));

		return new ReproOptions(dynamicMappingsPerMapper, payloadMiB * 1024 * 1024, resultsPath);
	}
}

sealed record ScenarioResult(
	int PropertyEntriesBeforeCollect,
	int CommandEntriesBeforeCollect,
	int PropertyEntriesAfterCollect,
	int CommandEntriesAfterCollect,
	int RetainedAssemblyCount,
	int RetainedTypeCount,
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
		Control.PropertyEntriesAfterCollect == 0 &&
		Control.CommandEntriesAfterCollect == 0 &&
		Control.RetainedAssemblyCount == 0 &&
		Control.RetainedTypeCount == 0 &&
		Control.RetainedPayloadCount == 0 &&
		Current.PropertyEntriesAfterCollect == Options.DynamicMappingsPerMapper &&
		Current.CommandEntriesAfterCollect == Options.DynamicMappingsPerMapper &&
		Current.RetainedAssemblyCount == Options.TotalDynamicMappings &&
		Current.RetainedTypeCount == Options.TotalDynamicMappings &&
		Current.RetainedPayloadCount == Options.TotalDynamicMappings;

	public override string ToString()
	{
		var writer = new StringWriter();
		writer.WriteLine("HandlerMapper delegate retention repro");
		writer.WriteLine($"Result: {(Proven ? "PROVEN" : "NOT PROVEN")}");
		writer.WriteLine();
		writer.WriteLine("Trigger:");
		writer.WriteLine("  MAUI exposes process-static handler PropertyMapper and CommandMapper instances.");
		writer.WriteLine("  Public Add/AppendToMapping/PrependToMapping/ModifyMapping APIs store delegates in those mapper dictionaries.");
		writer.WriteLine("  The mapper dictionaries have no public remove or scoped registration API.");
		writer.WriteLine("  Plugin/module mapper delegates can therefore stay rooted after the plugin should unload.");
		writer.WriteLine();
		writer.WriteLine($"Dynamic mappings per mapper: {Options.DynamicMappingsPerMapper}");
		writer.WriteLine($"Total dynamic mapper delegates: {Options.TotalDynamicMappings}");
		writer.WriteLine($"Payload per dynamic mapper type: {Options.PayloadBytes / 1024 / 1024} MiB");
		writer.WriteLine();
		WriteScenario(writer, "Control: repro mapper entries removed before forced GC", Control);
		writer.WriteLine();
		WriteScenario(writer, "Current MAUI: repro mapper entries left in static mappers", Current);
		return writer.ToString();
	}

	static void WriteScenario(StringWriter writer, string title, ScenarioResult result)
	{
		writer.WriteLine(title);
		writer.WriteLine($"  Property mapper entries before collect: {result.PropertyEntriesBeforeCollect}");
		writer.WriteLine($"  Property mapper entries after collect: {result.PropertyEntriesAfterCollect}");
		writer.WriteLine($"  Command mapper entries before collect: {result.CommandEntriesBeforeCollect}");
		writer.WriteLine($"  Command mapper entries after collect: {result.CommandEntriesAfterCollect}");
		writer.WriteLine($"  Retained assemblies: {result.RetainedAssemblyCount}");
		writer.WriteLine($"  Retained mapper types: {result.RetainedTypeCount}");
		writer.WriteLine($"  Retained payloads: {result.RetainedPayloadCount}");
		writer.WriteLine($"  Retained payload bytes: {result.RetainedPayloadBytes:N0}");
		writer.WriteLine($"  Managed heap delta: {result.HeapDeltaBytes:N0} bytes");
	}
}
