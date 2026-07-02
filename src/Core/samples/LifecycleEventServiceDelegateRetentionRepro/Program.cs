using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Maui.LifecycleEvents;

var options = ReproOptions.Parse(args);
var probe = new LifecycleEventServiceDelegateRetentionProbe(options);
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

sealed class LifecycleEventServiceDelegateRetentionProbe
{
	const string EventName = "__LifecycleEventServiceDelegateRetentionRepro_Event";

	static readonly FieldInfo MapperField =
		typeof(LifecycleEventService).GetField("_mapper", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(typeof(LifecycleEventService).FullName, "_mapper");

	readonly ReproOptions _options;

	public LifecycleEventServiceDelegateRetentionProbe(ReproOptions options)
	{
		_options = options;
	}

	public ReproReport Run()
	{
		var control = RunScenario(clearLifecycleEventsBeforeCollect: true);
		var current = RunScenario(clearLifecycleEventsBeforeCollect: false);

		ClearLifecycleEvents(current.Service);
		return new ReproReport(_options, control, current);
	}

	ScenarioResult RunScenario(bool clearLifecycleEventsBeforeCollect)
	{
		var assemblyRefs = new List<WeakReference<Assembly>>(_options.DynamicDelegateCount);
		var typeRefs = new List<WeakReference<Type>>(_options.DynamicDelegateCount);
		var payloadRefs = new List<WeakReference<byte[]>>(_options.DynamicDelegateCount);

		CollectHard();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		var service = CreateLifecycleEventServiceWithDynamicDelegates(
			assemblyRefs,
			typeRefs,
			payloadRefs);

		var eventNamesBeforeCollect = CountEventNames(service);
		var lifecycleDelegatesBeforeCollect = CountLifecycleDelegates(service);

		if (clearLifecycleEventsBeforeCollect)
			ClearLifecycleEvents(service);

		CollectHard();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var retainedPayloads = CountAlive(payloadRefs);
		return new ScenarioResult(
			Service: service,
			EventNamesBeforeCollect: eventNamesBeforeCollect,
			EventNamesAfterCollect: CountEventNames(service),
			LifecycleDelegatesBeforeCollect: lifecycleDelegatesBeforeCollect,
			LifecycleDelegatesAfterCollect: CountLifecycleDelegates(service),
			RetainedAssemblyCount: CountAlive(assemblyRefs),
			RetainedTypeCount: CountAlive(typeRefs),
			RetainedPayloadCount: retainedPayloads,
			RetainedPayloadBytes: (long)retainedPayloads * _options.PayloadBytes,
			HeapBeforeBytes: heapBefore,
			HeapAfterBytes: heapAfter);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	LifecycleEventService CreateLifecycleEventServiceWithDynamicDelegates(
		List<WeakReference<Assembly>> assemblyRefs,
		List<WeakReference<Type>> typeRefs,
		List<WeakReference<byte[]>> payloadRefs)
	{
		var registrations = new LifecycleEventRegistration[_options.DynamicDelegateCount];

		for (var i = 0; i < _options.DynamicDelegateCount; i++)
		{
			CreateDynamicLifecycleDelegate(
				i,
				out var delegateType,
				out var lifecycleAction,
				out var payload);

			assemblyRefs.Add(new WeakReference<Assembly>(delegateType.Assembly, trackResurrection: false));
			typeRefs.Add(new WeakReference<Type>(delegateType, trackResurrection: false));
			payloadRefs.Add(new WeakReference<byte[]>(payload, trackResurrection: false));

			registrations[i] = new LifecycleEventRegistration(builder =>
				builder.AddEvent(EventName, lifecycleAction));
		}

		return new LifecycleEventService(registrations);
	}

	void CreateDynamicLifecycleDelegate(
		int index,
		out Type delegateType,
		out Action lifecycleAction,
		out byte[] payload)
	{
		var assemblyName = new AssemblyName($"MauiLifecycleEventServiceDelegateRetentionRepro{index}");
		var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndCollect);
		var moduleBuilder = assemblyBuilder.DefineDynamicModule("Main");

		var typeBuilder = moduleBuilder.DefineType(
			$"PluginLifecycleDelegate{index}",
			TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed);
		var payloadField = typeBuilder.DefineField(
			"Payload",
			typeof(byte[]),
			FieldAttributes.Public | FieldAttributes.Static);
		var methodBuilder = typeBuilder.DefineMethod(
			"OnLifecycleEvent",
			MethodAttributes.Public | MethodAttributes.Static,
			typeof(void),
			Type.EmptyTypes);
		var il = methodBuilder.GetILGenerator();
		il.Emit(OpCodes.Ldsfld, payloadField);
		il.Emit(OpCodes.Pop);
		il.Emit(OpCodes.Ret);

		delegateType = typeBuilder.CreateType()!;
		payload = new byte[_options.PayloadBytes];
		for (var offset = 0; offset < payload.Length; offset += 4096)
			payload[offset] = (byte)(index % 251);

		delegateType.GetField(payloadField.Name)!.SetValue(null, payload);

		var method = delegateType.GetMethod("OnLifecycleEvent", BindingFlags.Public | BindingFlags.Static)
			?? throw new MissingMethodException(delegateType.FullName, "OnLifecycleEvent");
		lifecycleAction = (Action)Delegate.CreateDelegate(typeof(Action), method);
	}

	static int CountLifecycleDelegates(LifecycleEventService service)
	{
		return service.GetEventDelegates<Action>(EventName).Count();
	}

	static int CountEventNames(LifecycleEventService service)
	{
		return GetMapper(service).Count;
	}

	static void ClearLifecycleEvents(LifecycleEventService service)
	{
		GetMapper(service).Clear();
	}

	static IDictionary GetMapper(LifecycleEventService service)
	{
		if (MapperField.GetValue(service) is not IDictionary mapper)
			throw new InvalidOperationException("LifecycleEventService._mapper did not implement IDictionary.");

		return mapper;
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

sealed record ReproOptions(int DynamicDelegateCount, int PayloadBytes, string? ResultsPath)
{
	public static ReproOptions Parse(string[] args)
	{
		var dynamicDelegateCount = 80;
		var payloadMiB = 1;
		string? resultsPath = null;

		foreach (var arg in args)
		{
			if (arg.StartsWith("--count=", StringComparison.Ordinal))
			{
				dynamicDelegateCount = int.Parse(arg["--count=".Length..]);
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

		if (dynamicDelegateCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(dynamicDelegateCount));
		if (payloadMiB <= 0)
			throw new ArgumentOutOfRangeException(nameof(payloadMiB));

		return new ReproOptions(dynamicDelegateCount, payloadMiB * 1024 * 1024, resultsPath);
	}
}

sealed record ScenarioResult(
	LifecycleEventService Service,
	int EventNamesBeforeCollect,
	int EventNamesAfterCollect,
	int LifecycleDelegatesBeforeCollect,
	int LifecycleDelegatesAfterCollect,
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
		Control.EventNamesAfterCollect == 0 &&
		Control.LifecycleDelegatesAfterCollect == 0 &&
		Control.RetainedAssemblyCount == 0 &&
		Control.RetainedTypeCount == 0 &&
		Control.RetainedPayloadCount == 0 &&
		Current.EventNamesAfterCollect == 1 &&
		Current.LifecycleDelegatesAfterCollect == Options.DynamicDelegateCount &&
		Current.RetainedAssemblyCount == Options.DynamicDelegateCount &&
		Current.RetainedTypeCount == Options.DynamicDelegateCount &&
		Current.RetainedPayloadCount == Options.DynamicDelegateCount;

	public override string ToString()
	{
		var writer = new StringWriter();
		writer.WriteLine("LifecycleEventService delegate retention repro");
		writer.WriteLine($"Result: {(Proven ? "PROVEN" : "NOT PROVEN")}");
		writer.WriteLine();
		writer.WriteLine("Trigger:");
		writer.WriteLine("  ConfigureLifecycleEvents creates app-lifetime LifecycleEventRegistration entries.");
		writer.WriteLine("  LifecycleEventService copies registered delegates into its private _mapper dictionary.");
		writer.WriteLine("  ILifecycleEventService exposes read/invoke state but no public remove or scoped registration API.");
		writer.WriteLine("  Plugin/module lifecycle delegates can therefore stay rooted after the plugin should unload.");
		writer.WriteLine();
		writer.WriteLine($"Dynamic lifecycle delegates: {Options.DynamicDelegateCount}");
		writer.WriteLine($"Payload per dynamic delegate type: {Options.PayloadBytes / 1024 / 1024} MiB");
		writer.WriteLine();
		WriteScenario(writer, "Control: LifecycleEventService._mapper cleared before forced GC", Control);
		writer.WriteLine();
		WriteScenario(writer, "Current MAUI: LifecycleEventService._mapper left intact", Current);
		return writer.ToString();
	}

	static void WriteScenario(StringWriter writer, string title, ScenarioResult result)
	{
		writer.WriteLine(title);
		writer.WriteLine($"  Event names before collect: {result.EventNamesBeforeCollect}");
		writer.WriteLine($"  Event names after collect: {result.EventNamesAfterCollect}");
		writer.WriteLine($"  Lifecycle delegates before collect: {result.LifecycleDelegatesBeforeCollect}");
		writer.WriteLine($"  Lifecycle delegates after collect: {result.LifecycleDelegatesAfterCollect}");
		writer.WriteLine($"  Retained assemblies: {result.RetainedAssemblyCount}");
		writer.WriteLine($"  Retained delegate types: {result.RetainedTypeCount}");
		writer.WriteLine($"  Retained payloads: {result.RetainedPayloadCount}");
		writer.WriteLine($"  Retained payload bytes: {result.RetainedPayloadBytes:N0}");
		writer.WriteLine($"  Managed heap delta: {result.HeapDeltaBytes:N0} bytes");
	}
}
