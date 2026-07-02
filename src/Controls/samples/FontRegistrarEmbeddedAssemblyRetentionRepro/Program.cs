using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Maui;

var options = ReproOptions.Parse(args);
var probe = new FontRegistrarEmbeddedAssemblyRetentionProbe(options);
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

sealed class FontRegistrarEmbeddedAssemblyRetentionProbe
{
	static readonly FieldInfo EmbeddedFontsField =
		typeof(FontRegistrar).GetField("_embeddedFonts", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(typeof(FontRegistrar).FullName, "_embeddedFonts");

	static readonly FieldInfo FontLookupCacheField =
		typeof(FontRegistrar).GetField("_fontLookupCache", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(typeof(FontRegistrar).FullName, "_fontLookupCache");

	readonly ReproOptions _options;

	public FontRegistrarEmbeddedAssemblyRetentionProbe(ReproOptions options)
	{
		_options = options;
	}

	public ReproReport Run()
	{
		var control = RunScenario(clearRegistrarBeforeCollect: true);
		var current = RunScenario(clearRegistrarBeforeCollect: false);

		return new ReproReport(_options, control, current);
	}

	ScenarioResult RunScenario(bool clearRegistrarBeforeCollect)
	{
		var registrar = new FontRegistrar(new NoOpEmbeddedFontLoader());
		var assemblyRefs = new List<WeakReference<Assembly>>(_options.AssemblyCount);
		var typeRefs = new List<WeakReference<Type>>(_options.AssemblyCount);
		var payloadRefs = new List<WeakReference<byte[]>>(_options.AssemblyCount);

		CollectHard();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		for (var i = 0; i < _options.AssemblyCount; i++)
			CreateDynamicAssemblyAndRegisterFont(i, registrar, assemblyRefs, typeRefs, payloadRefs);

		var entriesBeforeCollect = GetDictionaryCount(EmbeddedFontsField, registrar);

		if (clearRegistrarBeforeCollect)
			ClearRegistrarCaches(registrar);

		CollectHard();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var retainedPayloads = CountAlive(payloadRefs);
		var result = new ScenarioResult(
			RegistrarEmbeddedFontEntryCount: GetDictionaryCount(EmbeddedFontsField, registrar),
			EntriesBeforeCollect: entriesBeforeCollect,
			RetainedAssemblyCount: CountAlive(assemblyRefs),
			RetainedTypeCount: CountAlive(typeRefs),
			RetainedPayloadCount: retainedPayloads,
			RetainedPayloadBytes: (long)retainedPayloads * _options.PayloadBytes,
			HeapBeforeBytes: heapBefore,
			HeapAfterBytes: heapAfter);

		GC.KeepAlive(registrar);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	void CreateDynamicAssemblyAndRegisterFont(
		int index,
		FontRegistrar registrar,
		List<WeakReference<Assembly>> assemblyRefs,
		List<WeakReference<Type>> typeRefs,
		List<WeakReference<byte[]>> payloadRefs)
	{
		var assemblyName = new AssemblyName($"MauiFontRegistrarEmbeddedAssemblyRetentionRepro{index}");
		var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndCollect);
		var moduleBuilder = assemblyBuilder.DefineDynamicModule("Main");
		var typeBuilder = moduleBuilder.DefineType(
			$"TenantFontPluginPayload{index}",
			TypeAttributes.Public | TypeAttributes.Class);
		var payloadField = typeBuilder.DefineField(
			"Payload",
			typeof(byte[]),
			FieldAttributes.Public | FieldAttributes.Static);

		var type = typeBuilder.CreateType()!;
		var payload = new byte[_options.PayloadBytes];
		for (var offset = 0; offset < payload.Length; offset += 4096)
			payload[offset] = (byte)(index % 251);

		type.GetField(payloadField.Name)!.SetValue(null, payload);

		assemblyRefs.Add(new WeakReference<Assembly>(type.Assembly, trackResurrection: false));
		typeRefs.Add(new WeakReference<Type>(type, trackResurrection: false));
		payloadRefs.Add(new WeakReference<byte[]>(payload, trackResurrection: false));

		registrar.Register($"TenantFont{index}.ttf", $"TenantFont{index}", type.Assembly);
	}

	static void ClearRegistrarCaches(FontRegistrar registrar)
	{
		ClearDictionary(EmbeddedFontsField, registrar);
		ClearDictionary(FontLookupCacheField, registrar);
	}

	static void ClearDictionary(FieldInfo field, FontRegistrar registrar)
	{
		if (field.GetValue(registrar) is not IDictionary dictionary)
			throw new InvalidOperationException($"{field.Name} did not implement IDictionary.");

		dictionary.Clear();
	}

	static int GetDictionaryCount(FieldInfo field, FontRegistrar registrar)
	{
		if (field.GetValue(registrar) is not IDictionary dictionary)
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

sealed class NoOpEmbeddedFontLoader : IEmbeddedFontLoader
{
	public string? LoadFont(EmbeddedFont font) => font.FontName;
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
	int RegistrarEmbeddedFontEntryCount,
	int EntriesBeforeCollect,
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
		Control.RetainedAssemblyCount == 0
		&& Control.RetainedTypeCount == 0
		&& Control.RetainedPayloadCount == 0
		&& Current.RetainedAssemblyCount == Options.AssemblyCount
		&& Current.RetainedTypeCount == Options.AssemblyCount
		&& Current.RetainedPayloadCount == Options.AssemblyCount;

	public override string ToString()
	{
		return $"""
			FontRegistrar embedded assembly retention repro
			Result: {(Proven ? "PROVEN" : "NOT PROVEN")}

			Trigger:
			  A live app-level FontRegistrar registers embedded resource fonts from many collectible plugin/module assemblies.
			  FontRegistrar.Register(filename, alias, assembly) stores each Assembly strongly in _embeddedFonts.
			  There is no public unregister or eviction path for plugin/module unload.

			Dynamic assemblies: {Options.AssemblyCount}
			Payload per assembly: {Options.PayloadBytes / 1024 / 1024} MiB
			Expected _embeddedFonts entries with aliases: {Options.AssemblyCount * 2}

			Control: explicit _embeddedFonts.Clear() before forced GC
			  Entries before collect: {Control.EntriesBeforeCollect}
			  Entries after collect: {Control.RegistrarEmbeddedFontEntryCount}
			  Retained assemblies: {Control.RetainedAssemblyCount}/{Options.AssemblyCount}
			  Retained types: {Control.RetainedTypeCount}/{Options.AssemblyCount}
			  Retained payloads: {Control.RetainedPayloadCount}/{Options.AssemblyCount}
			  Retained payload bytes: {Control.RetainedPayloadBytes:N0}
			  Managed heap delta: {Control.HeapDeltaBytes:N0} bytes

			Current MAUI: _embeddedFonts left intact
			  Entries before collect: {Current.EntriesBeforeCollect}
			  Entries after collect: {Current.RegistrarEmbeddedFontEntryCount}
			  Retained assemblies: {Current.RetainedAssemblyCount}/{Options.AssemblyCount}
			  Retained types: {Current.RetainedTypeCount}/{Options.AssemblyCount}
			  Retained payloads: {Current.RetainedPayloadCount}/{Options.AssemblyCount}
			  Retained payload bytes: {Current.RetainedPayloadBytes:N0}
			  Managed heap delta: {Current.HeapDeltaBytes:N0} bytes
			""";
	}
}
