using System.Collections;
using System.Reflection;
using System.Text;
using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace StyleClassGeneratedPropertyRetentionRepro;

public class App : Application
{
	const int StyleClassCount = 120;
	const int PayloadBytes = 1024 * 1024;
	const string ResultPath = "/tmp/styleclass-generated-property-retention-results.txt";
	const string FinalClassName = "styleclass-retention-final-empty";
	const string StyleClassPrefix = "Microsoft.Maui.Controls.StyleClass.";

	static readonly FieldInfo? PropertiesField = typeof(BindableObject).GetField("_properties", BindingFlags.Instance | BindingFlags.NonPublic);
	static readonly FieldInfo? MergedStyleField = typeof(StyleableElement).GetField("_mergedStyle", BindingFlags.Instance | BindingFlags.NonPublic);

	public App()
	{
		Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(250), RunAndQuit);
	}

	protected override Window CreateWindow(IActivationState? activationState) =>
		new(new ContentPage
		{
			Content = new Label
			{
				Text = "Running StyleClass generated property retention repro...",
				Margin = new Thickness(24)
			}
		});

	void RunAndQuit()
	{
		try
		{
			var report = RunProof();
			File.WriteAllText(ResultPath, report);
		}
		catch (Exception ex)
		{
			File.WriteAllText(ResultPath, "RESULT: ERROR" + Environment.NewLine + ex);
		}
		finally
		{
			Quit();
		}
	}

	static string RunProof()
	{
		var before = ForceFullGC();

		var control = CreateRun("control: remove stale generated ClassStyle property contexts", removeStaleClassStyleContexts: true);
		var afterControlCreate = ForceFullGC();
		control.Measure();

		var current = CreateRun("current: churn StyleClass on a retained target", removeStaleClassStyleContexts: false);
		var afterCurrentCreate = ForceFullGC();
		current.Measure();

		var proven =
			control.TargetAlive == 1 &&
			current.TargetAlive == 1 &&
			control.PayloadsAlive == 0 &&
			control.PayloadBuffersAlive == 0 &&
			control.StylesAlive == 0 &&
			control.StyleListsAlive == 0 &&
			control.GeneratedClassStyleContexts <= 1 &&
			current.PayloadsAlive == StyleClassCount &&
			current.PayloadBuffersAlive == StyleClassCount &&
			current.StylesAlive == StyleClassCount &&
			current.StyleListsAlive == StyleClassCount &&
			current.GeneratedClassStyleContexts >= StyleClassCount;

		var builder = new StringBuilder();
		builder.AppendLine(proven ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
		builder.AppendLine("StyleClassGeneratedPropertyRetentionRepro");
		builder.AppendLine($"Retained target labels in both scenarios: 1");
		builder.AppendLine($"StyleClass churn iterations: {StyleClassCount}");
		builder.AppendLine($"Payload per generated style setter: {PayloadBytes / 1024 / 1024:0.0} MiB");
		builder.AppendLine();
		AppendRun(builder, control, before, afterControlCreate);
		builder.AppendLine();
		AppendRun(builder, current, afterControlCreate, afterCurrentCreate);
		builder.AppendLine();
		builder.AppendLine("Leak path: retained Label -> BindableObject._properties -> stale generated ClassStyle BindablePropertyContext -> resolved IList<Style> resource value -> Style.Setters -> payload setter value.");
		builder.AppendLine("The target label remains alive in both scenarios; the signal is whether removed style resource lists, styles, and setter payload buffers collect after full GC.");
		builder.AppendLine("The control removes stale generated ClassStyle contexts while preserving the current final empty class context.");
		builder.AppendLine($"dotnet-version: {Environment.Version}");

		GC.KeepAlive(control.RetainedTarget);
		GC.KeepAlive(current.RetainedTarget);

		return builder.ToString();
	}

	static RunResult CreateRun(string name, bool removeStaleClassStyleContexts)
	{
		var result = new RunResult(name);
		var target = new Label();
		result.RetainedTarget = target;
		result.TargetRef = new WeakReference<Label>(target);

		EnsureFinalEmptyClassResource(target);

		for (var i = 0; i < StyleClassCount; i++)
		{
			var className = "styleclass-retention-" + i;
			var payload = new PayloadCarrier(i, PayloadBytes);
			var style = new Style(typeof(Label))
			{
				Class = className,
				Setters =
				{
					new Setter
					{
						Property = PayloadProbe.PayloadProperty,
						Value = payload
					}
				}
			};
			var styles = new List<Style> { style };
			var resourceKey = StyleClassPrefix + className;

			target.Resources[resourceKey] = styles;
			target.StyleClass = new List<string> { className };
			target.Resources.Remove(resourceKey);

			result.PayloadRefs.Add(new WeakReference<PayloadCarrier>(payload));
			result.PayloadBufferRefs.Add(new WeakReference<byte[]>(payload.Buffer));
			result.StyleRefs.Add(new WeakReference<Style>(style));
			result.StyleListRefs.Add(new WeakReference<List<Style>>(styles));

			if (removeStaleClassStyleContexts)
				RemoveStaleClassStyleContexts(target);
		}

		target.StyleClass = new List<string> { FinalClassName };
		if (removeStaleClassStyleContexts)
			RemoveStaleClassStyleContexts(target);

		target.ClearValue(PayloadProbe.PayloadProperty);
		return result;
	}

	static void EnsureFinalEmptyClassResource(VisualElement target)
	{
		var resourceKey = StyleClassPrefix + FinalClassName;
		if (!target.Resources.ContainsKey(resourceKey))
			target.Resources[resourceKey] = new List<Style>();
	}

	static void AppendRun(StringBuilder builder, RunResult result, long beforeBytes, long afterBytes)
	{
		builder.AppendLine($"Run: {result.Name}");
		builder.AppendLine($"  retained target labels: 1");
		builder.AppendLine($"  target label alive after full GC: {result.TargetAlive}/1");
		builder.AppendLine($"  generated ClassStyle property contexts on target: {result.GeneratedClassStyleContexts}");
		builder.AppendLine($"  removed style resource lists alive after full GC: {result.StyleListsAlive}/{StyleClassCount}");
		builder.AppendLine($"  removed styles alive after full GC: {result.StylesAlive}/{StyleClassCount}");
		builder.AppendLine($"  payload objects alive after full GC: {result.PayloadsAlive}/{StyleClassCount}");
		builder.AppendLine($"  payload buffers alive after full GC: {result.PayloadBuffersAlive}/{StyleClassCount}");
		builder.AppendLine($"  retained payload bytes: {result.RetainedPayloadBytes / 1024d / 1024d:0.0} MiB");
		builder.AppendLine($"  managed heap delta: {(afterBytes - beforeBytes) / 1024d / 1024d:0.0} MiB");
	}

	static long ForceFullGC()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
			GC.WaitForPendingFinalizers();
		}

		return GC.GetTotalMemory(forceFullCollection: true);
	}

	static void RemoveStaleClassStyleContexts(Label target)
	{
		var preservedProperties = GetCurrentClassStyleProperties(target);
		var dictionary = PropertiesField?.GetValue(target);
		if (dictionary is null)
			return;

		var keysToRemove = new List<object>();
		foreach (var entry in (IEnumerable)dictionary)
		{
			var entryType = entry.GetType();
			var key = entryType.GetProperty("Key")?.GetValue(entry);
			var context = entryType.GetProperty("Value")?.GetValue(entry);
			var property = GetContextProperty(context);

			if (key is not null &&
				property?.PropertyName == "ClassStyle" &&
				!preservedProperties.Contains(property))
			{
				keysToRemove.Add(key);
			}
		}

		var removeMethod = dictionary.GetType().GetMethod("Remove", new[] { typeof(int) });
		foreach (var key in keysToRemove)
			removeMethod?.Invoke(dictionary, new[] { key });
	}

	static HashSet<BindableProperty> GetCurrentClassStyleProperties(Label target)
	{
		var result = new HashSet<BindableProperty>();
		var mergedStyle = MergedStyleField?.GetValue(target);
		var classStylePropertiesField = mergedStyle?.GetType().GetField("_classStyleProperties", BindingFlags.Instance | BindingFlags.NonPublic);
		if (classStylePropertiesField?.GetValue(mergedStyle) is IEnumerable properties)
		{
			foreach (var property in properties)
			{
				if (property is BindableProperty bindableProperty)
					result.Add(bindableProperty);
			}
		}

		return result;
	}

	static int CountGeneratedClassStyleContexts(Label target)
	{
		var dictionary = PropertiesField?.GetValue(target);
		if (dictionary is null)
			return 0;

		var count = 0;
		foreach (var entry in (IEnumerable)dictionary)
		{
			var context = entry.GetType().GetProperty("Value")?.GetValue(entry);
			var property = GetContextProperty(context);
			if (property?.PropertyName == "ClassStyle")
				count++;
		}

		return count;
	}

	static BindableProperty? GetContextProperty(object? context)
	{
		if (context is null)
			return null;

		return context.GetType().GetField("Property")?.GetValue(context) as BindableProperty;
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

	sealed class RunResult
	{
		public RunResult(string name)
		{
			Name = name;
		}

		public string Name { get; }
		public Label? RetainedTarget { get; set; }
		public WeakReference<Label>? TargetRef { get; set; }
		public List<WeakReference<PayloadCarrier>> PayloadRefs { get; } = new();
		public List<WeakReference<byte[]>> PayloadBufferRefs { get; } = new();
		public List<WeakReference<Style>> StyleRefs { get; } = new();
		public List<WeakReference<List<Style>>> StyleListRefs { get; } = new();
		public int TargetAlive { get; private set; }
		public int PayloadsAlive { get; private set; }
		public int PayloadBuffersAlive { get; private set; }
		public int StylesAlive { get; private set; }
		public int StyleListsAlive { get; private set; }
		public int GeneratedClassStyleContexts { get; private set; }
		public long RetainedPayloadBytes => PayloadBuffersAlive * (long)PayloadBytes;

		public void Measure()
		{
			TargetAlive = TargetRef?.TryGetTarget(out _) == true ? 1 : 0;
			PayloadsAlive = CountAlive(PayloadRefs);
			PayloadBuffersAlive = CountAlive(PayloadBufferRefs);
			StylesAlive = CountAlive(StyleRefs);
			StyleListsAlive = CountAlive(StyleListRefs);
			GeneratedClassStyleContexts = RetainedTarget is not null ? CountGeneratedClassStyleContexts(RetainedTarget) : 0;
		}
	}

	sealed class PayloadCarrier
	{
		public PayloadCarrier(int id, int byteCount)
		{
			Id = id;
			Buffer = new byte[byteCount];
			Buffer[0] = (byte)(id % 255);
		}

		public int Id { get; }
		public byte[] Buffer { get; }
	}

	static class PayloadProbe
	{
		public static readonly BindableProperty PayloadProperty = BindableProperty.CreateAttached(
			"Payload",
			typeof(PayloadCarrier),
			typeof(PayloadProbe),
			default(PayloadCarrier));
	}
}
