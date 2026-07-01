using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Controls;

var options = ReproOptions.Parse(args);
var probe = new DataTemplateSelectorItemTypeCacheRetentionProbe(options);
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

sealed class DataTemplateSelectorItemTypeCacheRetentionProbe
{
	readonly ReproOptions _options;

	public DataTemplateSelectorItemTypeCacheRetentionProbe(ReproOptions options)
	{
		_options = options;
	}

	public ReproReport Run()
	{
		var control = RunScenario(clearTemplateCacheBeforeCollect: true);
		var current = RunScenario(clearTemplateCacheBeforeCollect: false);

		return new ReproReport(_options, control, current);
	}

	ScenarioResult RunScenario(bool clearTemplateCacheBeforeCollect)
	{
		var selector = new ProbeTemplateSelector();
		var listView = CreateRecycleElementAndDataTemplateListView();

		var assemblyRefs = new List<WeakReference>(_options.TypeCount);
		var typeRefs = new List<WeakReference>(_options.TypeCount);
		var payloadRefs = new List<WeakReference>(_options.TypeCount);

		for (var i = 0; i < _options.TypeCount; i++)
			SelectDynamicItemType(i, selector, listView, assemblyRefs, typeRefs, payloadRefs);

		if (clearTemplateCacheBeforeCollect)
			ClearTemplateCache(selector);

		CollectHard();

		var retainedPayloads = payloadRefs.Count(static wr => wr.IsAlive);

		return new ScenarioResult(
			SelectorCacheEntryCount: GetTemplateCacheCount(selector),
			RetainedAssemblyCount: assemblyRefs.Count(static wr => wr.IsAlive),
			RetainedTypeCount: typeRefs.Count(static wr => wr.IsAlive),
			RetainedPayloadCount: retainedPayloads,
			RetainedPayloadBytes: (long)retainedPayloads * _options.PayloadBytes);
	}

	void SelectDynamicItemType(
		int index,
		DataTemplateSelector selector,
		BindableObject container,
		List<WeakReference> assemblyRefs,
		List<WeakReference> typeRefs,
		List<WeakReference> payloadRefs)
	{
		var assemblyName = new AssemblyName($"DataTemplateSelectorItemTypeCacheRetentionRepro{index}");
		var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndCollect);
		var moduleBuilder = assemblyBuilder.DefineDynamicModule("Main");
		var typeBuilder = moduleBuilder.DefineType(
			$"DynamicTemplateItem{index}",
			TypeAttributes.Public | TypeAttributes.Class);
		var payloadField = typeBuilder.DefineField(
			"Payload",
			typeof(byte[]),
			FieldAttributes.Public | FieldAttributes.Static);
		typeBuilder.DefineDefaultConstructor(MethodAttributes.Public);

		var type = typeBuilder.CreateType()!;
		var payload = new byte[_options.PayloadBytes];
		type.GetField(payloadField.Name)!.SetValue(null, payload);
		var item = Activator.CreateInstance(type)!;

		assemblyRefs.Add(new WeakReference(type.Assembly, trackResurrection: false));
		typeRefs.Add(new WeakReference(type, trackResurrection: false));
		payloadRefs.Add(new WeakReference(payload, trackResurrection: false));

		selector.SelectTemplate(item, container);
	}

	static void ClearTemplateCache(DataTemplateSelector selector)
	{
		if (GetTemplateCache(selector) is IDictionary dictionary)
			dictionary.Clear();
	}

	static int GetTemplateCacheCount(DataTemplateSelector selector)
	{
		var cache = GetTemplateCache(selector);
		return cache.GetType().GetProperty("Count")?.GetValue(cache) is int count
			? count
			: -1;
	}

	static object GetTemplateCache(DataTemplateSelector selector)
	{
		var field = typeof(DataTemplateSelector).GetField("_dataTemplates", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(typeof(DataTemplateSelector).FullName, "_dataTemplates");

		return field.GetValue(selector)
			?? throw new InvalidOperationException("_dataTemplates was null.");
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

#pragma warning disable CS0618
	static ListView CreateRecycleElementAndDataTemplateListView()
	{
		// The real selector cache only requires a ListView container with this strategy.
		// Avoid the dispatcher-dependent ListView constructor so this proof runs headless.
		var listView = (ListView)RuntimeHelpers.GetUninitializedObject(typeof(ListView));
		var field = typeof(ListView).GetField("<CachingStrategy>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(typeof(ListView).FullName, "<CachingStrategy>k__BackingField");
		field.SetValue(listView, ListViewCachingStrategy.RecycleElementAndDataTemplate);
		return listView;
	}
#pragma warning restore CS0618
}

sealed class ProbeTemplateSelector : DataTemplateSelector
{
	protected override DataTemplate OnSelectTemplate(object item, BindableObject container)
		=> new(typeof(Microsoft.Maui.Controls.Label));
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
	int SelectorCacheEntryCount,
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
			DataTemplateSelector item Type cache retention repro

			Dynamic item types: {Options.TypeCount}
			Payload per type: {Options.PayloadBytes / 1024 / 1024} MiB

			Control: explicit _dataTemplates.Clear()
			  Selector cache entries: {Control.SelectorCacheEntryCount}
			  Retained assemblies: {Control.RetainedAssemblyCount}/{Options.TypeCount}
			  Retained types: {Control.RetainedTypeCount}/{Options.TypeCount}
			  Retained payloads: {Control.RetainedPayloadCount}/{Options.TypeCount}
			  Retained payload bytes: {Control.RetainedPayloadBytes:N0}

			Current MAUI: _dataTemplates left intact
			  Selector cache entries: {Current.SelectorCacheEntryCount}
			  Retained assemblies: {Current.RetainedAssemblyCount}/{Options.TypeCount}
			  Retained types: {Current.RetainedTypeCount}/{Options.TypeCount}
			  Retained payloads: {Current.RetainedPayloadCount}/{Options.TypeCount}
			  Retained payload bytes: {Current.RetainedPayloadBytes:N0}
			""";
	}
}
