using System.Collections;
using System.Reflection;
using System.Text;
using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace StyleBaseResourceKeyRetentionRepro;

public class App : Application
{
	const int StyleCount = 120;
	const int PayloadBytes = 1024 * 1024;
	const string ResultPath = "/tmp/style-baseresourcekey-retention-results.txt";

	static readonly FieldInfo? PropertiesField = typeof(BindableObject).GetField("_properties", BindingFlags.Instance | BindingFlags.NonPublic);

	public App()
	{
		Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(250), RunAndQuit);
	}

	protected override Window CreateWindow(IActivationState? activationState) =>
		new(new ContentPage
		{
			Content = new Label
			{
				Text = "Running Style BaseResourceKey retention repro...",
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

		var control = CreateRun("control: remove stale hidden BasedOnResource property contexts", removeStaleBasedOnContexts: true);
		var afterControlCreate = ForceFullGC();
		control.Measure();

		var current = CreateRun("current: apply/remove BaseResourceKey styles on a retained target", removeStaleBasedOnContexts: false);
		var afterCurrentCreate = ForceFullGC();
		current.Measure();

		var proven =
			control.TargetAlive == 1 &&
			current.TargetAlive == 1 &&
			control.PayloadsAlive == 0 &&
			control.PayloadBuffersAlive == 0 &&
			control.BaseStylesAlive == 0 &&
			control.BasedOnResourceContexts == 0 &&
			control.BasedOnResourceStyleValueContexts == 0 &&
			current.PayloadsAlive == StyleCount &&
			current.PayloadBuffersAlive == StyleCount &&
			current.BaseStylesAlive == StyleCount &&
			current.BasedOnResourceContexts >= StyleCount &&
			current.BasedOnResourceStyleValueContexts == StyleCount;

		var builder = new StringBuilder();
		builder.AppendLine(proven ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
		builder.AppendLine("StyleBaseResourceKeyRetentionRepro");
		builder.AppendLine("Retained target labels in both scenarios: 1");
		builder.AppendLine($"Dynamic styles applied and removed: {StyleCount}");
		builder.AppendLine($"Payload per base style setter: {PayloadBytes / 1024 / 1024:0.0} MiB");
		builder.AppendLine();
		AppendRun(builder, control, before, afterControlCreate);
		builder.AppendLine();
		AppendRun(builder, current, afterControlCreate, afterCurrentCreate);
		builder.AppendLine();
		builder.AppendLine("Leak path: retained Label -> BindableObject._properties -> stale hidden BasedOnResource BindablePropertyContext -> removed base Style resource -> Style.Setters -> payload setter value.");
		builder.AppendLine("The target label remains alive in both scenarios; the signal is whether removed base styles and setter payload buffers collect after full GC.");
		builder.AppendLine("The control removes stale hidden BasedOnResource contexts after each derived style is unapplied.");
		builder.AppendLine($"dotnet-version: {Environment.Version}");

		GC.KeepAlive(control.RetainedTarget);
		GC.KeepAlive(current.RetainedTarget);

		return builder.ToString();
	}

	static RunResult CreateRun(string name, bool removeStaleBasedOnContexts)
	{
		var result = new RunResult(name);
		var target = new Label();
		result.RetainedTarget = target;
		result.TargetRef = new WeakReference<Label>(target);

		for (var i = 0; i < StyleCount; i++)
		{
			var resourceKey = "baseresourcekey-retention-" + i;
			var payload = new PayloadCarrier(i, PayloadBytes);
			var baseStyle = new Style(typeof(Label))
			{
				Setters =
				{
					new Setter
					{
						Property = PayloadProbe.PayloadProperty,
						Value = payload
					}
				}
			};
			var derivedStyle = new Style(typeof(Label))
			{
				BaseResourceKey = resourceKey
			};

			target.Resources[resourceKey] = baseStyle;
			target.Style = derivedStyle;
			target.Style = null;
			target.Resources.Remove(resourceKey);
			target.ClearValue(PayloadProbe.PayloadProperty);

			result.PayloadRefs.Add(new WeakReference<PayloadCarrier>(payload));
			result.PayloadBufferRefs.Add(new WeakReference<byte[]>(payload.Buffer));
			result.BaseStyleRefs.Add(new WeakReference<Style>(baseStyle));

			if (removeStaleBasedOnContexts)
				RemoveStaleBasedOnResourceContexts(target);
		}

		return result;
	}

	static void AppendRun(StringBuilder builder, RunResult result, long beforeBytes, long afterBytes)
	{
		builder.AppendLine($"Run: {result.Name}");
		builder.AppendLine("  retained target labels: 1");
		builder.AppendLine($"  target label alive after full GC: {result.TargetAlive}/1");
		builder.AppendLine($"  hidden BasedOnResource property contexts on target: {result.BasedOnResourceContexts}");
		builder.AppendLine($"  hidden BasedOnResource contexts carrying Style values: {result.BasedOnResourceStyleValueContexts}");
		builder.AppendLine($"  removed base styles alive after full GC: {result.BaseStylesAlive}/{StyleCount}");
		builder.AppendLine($"  payload objects alive after full GC: {result.PayloadsAlive}/{StyleCount}");
		builder.AppendLine($"  payload buffers alive after full GC: {result.PayloadBuffersAlive}/{StyleCount}");
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

	static void RemoveStaleBasedOnResourceContexts(Label target)
	{
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

			if (key is not null && property?.PropertyName == "BasedOnResource")
				keysToRemove.Add(key);
		}

		var removeMethod = dictionary.GetType().GetMethod("Remove", new[] { typeof(int) });
		foreach (var key in keysToRemove)
			removeMethod?.Invoke(dictionary, new[] { key });
	}

	static int CountBasedOnResourceContexts(Label target)
	{
		var dictionary = PropertiesField?.GetValue(target);
		if (dictionary is null)
			return 0;

		var count = 0;
		foreach (var entry in (IEnumerable)dictionary)
		{
			var context = entry.GetType().GetProperty("Value")?.GetValue(entry);
			var property = GetContextProperty(context);
			if (property?.PropertyName == "BasedOnResource")
				count++;
		}

		return count;
	}

	static int CountBasedOnResourceStyleValueContexts(Label target)
	{
		var dictionary = PropertiesField?.GetValue(target);
		if (dictionary is null)
			return 0;

		var count = 0;
		foreach (var entry in (IEnumerable)dictionary)
		{
			var context = entry.GetType().GetProperty("Value")?.GetValue(entry);
			var property = GetContextProperty(context);
			if (property?.PropertyName == "BasedOnResource" && GetContextValue(context) is Style)
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

	static object? GetContextValue(object? context)
	{
		var values = context?.GetType().GetField("Values")?.GetValue(context);
		return values?.GetType().GetMethod("GetValue")?.Invoke(values, null);
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
		public List<WeakReference<Style>> BaseStyleRefs { get; } = new();
		public int TargetAlive { get; private set; }
		public int PayloadsAlive { get; private set; }
		public int PayloadBuffersAlive { get; private set; }
		public int BaseStylesAlive { get; private set; }
		public int BasedOnResourceContexts { get; private set; }
		public int BasedOnResourceStyleValueContexts { get; private set; }
		public long RetainedPayloadBytes => PayloadBuffersAlive * (long)PayloadBytes;

		public void Measure()
		{
			TargetAlive = TargetRef?.TryGetTarget(out _) == true ? 1 : 0;
			PayloadsAlive = CountAlive(PayloadRefs);
			PayloadBuffersAlive = CountAlive(PayloadBufferRefs);
			BaseStylesAlive = CountAlive(BaseStyleRefs);
			BasedOnResourceContexts = RetainedTarget is not null ? CountBasedOnResourceContexts(RetainedTarget) : 0;
			BasedOnResourceStyleValueContexts = RetainedTarget is not null ? CountBasedOnResourceStyleValueContexts(RetainedTarget) : 0;
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
