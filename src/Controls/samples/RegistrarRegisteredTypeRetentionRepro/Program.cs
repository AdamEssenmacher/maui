using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Internals;

var options = ReproOptions.Parse(args);
var probe = new RegistrarRegisteredTypeRetentionProbe(options);
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

sealed class RegistrarRegisteredTypeRetentionProbe
{
	static readonly FieldInfo HandlersField =
		typeof(Registrar<IRegisterable>).GetField("_handlers", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(typeof(Registrar<IRegisterable>).FullName, "_handlers");

	readonly ReproOptions _options;

	public RegistrarRegisteredTypeRetentionProbe(ReproOptions options)
	{
		_options = options;
	}

	public ReproReport Run()
	{
		var control = RunScenario(clearRegisteredBeforeCollect: true);
		var current = RunScenario(clearRegisteredBeforeCollect: false);

		ClearRegisteredHandlers();
		return new ReproReport(_options, control, current);
	}

	ScenarioResult RunScenario(bool clearRegisteredBeforeCollect)
	{
		ClearRegisteredHandlers();

		var assemblyRefs = new List<WeakReference<Assembly>>(_options.TypePairCount);
		var viewTypeRefs = new List<WeakReference<Type>>(_options.TypePairCount);
		var rendererTypeRefs = new List<WeakReference<Type>>(_options.TypePairCount);
		var payloadRefs = new List<WeakReference<byte[]>>(_options.TypePairCount);

		CollectHard();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		for (var i = 0; i < _options.TypePairCount; i++)
			CreateDynamicViewRendererPairAndRegister(i, assemblyRefs, viewTypeRefs, rendererTypeRefs, payloadRefs);

		var viewEntriesBeforeCollect = CountRegisteredViewEntries();
		var handlerEntriesBeforeCollect = CountRegisteredHandlerEntries();

		if (clearRegisteredBeforeCollect)
			ClearRegisteredHandlers();

		CollectHard();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var retainedPayloads = CountAlive(payloadRefs);
		return new ScenarioResult(
			RegisteredViewEntryCount: CountRegisteredViewEntries(),
			RegisteredHandlerEntryCount: CountRegisteredHandlerEntries(),
			RegisteredViewEntriesBeforeCollect: viewEntriesBeforeCollect,
			RegisteredHandlerEntriesBeforeCollect: handlerEntriesBeforeCollect,
			RetainedAssemblyCount: CountAlive(assemblyRefs),
			RetainedViewTypeCount: CountAlive(viewTypeRefs),
			RetainedRendererTypeCount: CountAlive(rendererTypeRefs),
			RetainedPayloadCount: retainedPayloads,
			RetainedPayloadBytes: (long)retainedPayloads * _options.PayloadBytes,
			HeapBeforeBytes: heapBefore,
			HeapAfterBytes: heapAfter);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	void CreateDynamicViewRendererPairAndRegister(
		int index,
		List<WeakReference<Assembly>> assemblyRefs,
		List<WeakReference<Type>> viewTypeRefs,
		List<WeakReference<Type>> rendererTypeRefs,
		List<WeakReference<byte[]>> payloadRefs)
	{
		var assemblyName = new AssemblyName($"MauiRegistrarRegisteredRetentionRepro{index}");
		var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndCollect);
		var moduleBuilder = assemblyBuilder.DefineDynamicModule("Main");

		var viewTypeBuilder = moduleBuilder.DefineType(
			$"TenantCompatibilityView{index}",
			TypeAttributes.Public | TypeAttributes.Class,
			typeof(View));
		viewTypeBuilder.DefineDefaultConstructor(MethodAttributes.Public);
		var viewType = viewTypeBuilder.CreateType()!;

		var rendererTypeBuilder = moduleBuilder.DefineType(
			$"TenantCompatibilityRenderer{index}",
			TypeAttributes.Public | TypeAttributes.Class);
		rendererTypeBuilder.AddInterfaceImplementation(typeof(IRegisterable));
		rendererTypeBuilder.DefineDefaultConstructor(MethodAttributes.Public);
		var payloadField = rendererTypeBuilder.DefineField(
			"Payload",
			typeof(byte[]),
			FieldAttributes.Public | FieldAttributes.Static);

		var rendererType = rendererTypeBuilder.CreateType()!;
		var payload = new byte[_options.PayloadBytes];
		for (var offset = 0; offset < payload.Length; offset += 4096)
			payload[offset] = (byte)(index % 251);

		rendererType.GetField(payloadField.Name)!.SetValue(null, payload);

		assemblyRefs.Add(new WeakReference<Assembly>(rendererType.Assembly, trackResurrection: false));
		viewTypeRefs.Add(new WeakReference<Type>(viewType, trackResurrection: false));
		rendererTypeRefs.Add(new WeakReference<Type>(rendererType, trackResurrection: false));
		payloadRefs.Add(new WeakReference<byte[]>(payload, trackResurrection: false));

		Registrar.Registered.Register(viewType, rendererType);
	}

	static void ClearRegisteredHandlers()
	{
		if (HandlersField.GetValue(Registrar.Registered) is not IDictionary handlers)
			throw new InvalidOperationException("Registrar.Registered._handlers did not implement IDictionary.");

		handlers.Clear();
	}

	static int CountRegisteredViewEntries()
	{
		if (HandlersField.GetValue(Registrar.Registered) is not IDictionary handlers)
			throw new InvalidOperationException("Registrar.Registered._handlers did not implement IDictionary.");

		return handlers.Count;
	}

	static int CountRegisteredHandlerEntries()
	{
		if (HandlersField.GetValue(Registrar.Registered) is not IDictionary handlers)
			throw new InvalidOperationException("Registrar.Registered._handlers did not implement IDictionary.");

		var count = 0;
		foreach (DictionaryEntry entry in handlers)
		{
			if (entry.Value is not IDictionary visualHandlers)
				throw new InvalidOperationException("Registrar.Registered._handlers contained a non-dictionary visual handler map.");

			count += visualHandlers.Count;
		}

		return count;
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
	int RegisteredViewEntryCount,
	int RegisteredHandlerEntryCount,
	int RegisteredViewEntriesBeforeCollect,
	int RegisteredHandlerEntriesBeforeCollect,
	int RetainedAssemblyCount,
	int RetainedViewTypeCount,
	int RetainedRendererTypeCount,
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
		Control.RegisteredViewEntryCount == 0
		&& Control.RegisteredHandlerEntryCount == 0
		&& Control.RetainedAssemblyCount == 0
		&& Control.RetainedViewTypeCount == 0
		&& Control.RetainedRendererTypeCount == 0
		&& Control.RetainedPayloadCount == 0
		&& Current.RegisteredViewEntryCount == Options.TypePairCount
		&& Current.RegisteredHandlerEntryCount == Options.TypePairCount
		&& Current.RetainedAssemblyCount == Options.TypePairCount
		&& Current.RetainedViewTypeCount == Options.TypePairCount
		&& Current.RetainedRendererTypeCount == Options.TypePairCount
		&& Current.RetainedPayloadCount == Options.TypePairCount;

	public override string ToString()
	{
		return $"""
			Registrar.Registered type retention repro
			Result: {(Proven ? "PROVEN" : "NOT PROVEN")}

			Trigger:
			  Registrar.Registered is a process-static compatibility registrar for renderer/handler metadata.
			  AddCompatibilityRenderer and ExportRenderer registration paths store target view Type keys and renderer Type values.
			  There is no public unregister or eviction path for plugin/module unload.

			Dynamic view/renderer pairs: {Options.TypePairCount}
			Payload per renderer type: {Options.PayloadBytes / 1024 / 1024} MiB

			Control: Registrar.Registered handler table cleared before forced GC
			  Registered view entries before collect: {Control.RegisteredViewEntriesBeforeCollect}
			  Registered handler entries before collect: {Control.RegisteredHandlerEntriesBeforeCollect}
			  Registered view entries after collect: {Control.RegisteredViewEntryCount}
			  Registered handler entries after collect: {Control.RegisteredHandlerEntryCount}
			  Retained assemblies: {Control.RetainedAssemblyCount}/{Options.TypePairCount}
			  Retained view types: {Control.RetainedViewTypeCount}/{Options.TypePairCount}
			  Retained renderer types: {Control.RetainedRendererTypeCount}/{Options.TypePairCount}
			  Retained payloads: {Control.RetainedPayloadCount}/{Options.TypePairCount}
			  Retained payload bytes: {Control.RetainedPayloadBytes:N0}
			  Managed heap delta: {Control.HeapDeltaBytes:N0} bytes

			Current MAUI: Registrar.Registered handler table left intact
			  Registered view entries before collect: {Current.RegisteredViewEntriesBeforeCollect}
			  Registered handler entries before collect: {Current.RegisteredHandlerEntriesBeforeCollect}
			  Registered view entries after collect: {Current.RegisteredViewEntryCount}
			  Registered handler entries after collect: {Current.RegisteredHandlerEntryCount}
			  Retained assemblies: {Current.RetainedAssemblyCount}/{Options.TypePairCount}
			  Retained view types: {Current.RetainedViewTypeCount}/{Options.TypePairCount}
			  Retained renderer types: {Current.RetainedRendererTypeCount}/{Options.TypePairCount}
			  Retained payloads: {Current.RetainedPayloadCount}/{Options.TypePairCount}
			  Retained payload bytes: {Current.RetainedPayloadBytes:N0}
			  Managed heap delta: {Current.HeapDeltaBytes:N0} bytes
			""";
	}
}
