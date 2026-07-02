using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace MultiBindingProxyParentRetentionRepro;

public class App : Application
{
	const int BindingCount = 120;
	const int PayloadBytes = 1024 * 1024;
	const string ResultPath = "/tmp/multibinding-proxy-parent-retention-results.txt";

	static readonly FieldInfo? ProxyObjectField = typeof(MultiBinding).GetField("_proxyObject", BindingFlags.Instance | BindingFlags.NonPublic);
	static readonly FieldInfo? ChangeHandlersField = typeof(Element).GetField("_changeHandlers", BindingFlags.Instance | BindingFlags.NonPublic);

	public App()
	{
		Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(250), RunAndQuit);
	}

	protected override Window CreateWindow(IActivationState? activationState) =>
		new(new ContentPage
		{
			Content = new Label
			{
				Text = "Running MultiBinding proxy parent retention repro...",
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

		var control = CreateRun("control: clear hidden proxy parent before removing MultiBinding", detachProxyParent: true);
		var afterControlCreate = ForceFullGC();
		control.Measure();

		var current = CreateRun("current: remove MultiBinding from retained target labels", detachProxyParent: false);
		var afterCurrentCreate = ForceFullGC();
		current.Measure();

		var proven =
			control.TargetsAlive == BindingCount &&
			current.TargetsAlive == BindingCount &&
			control.MultiBindingsAlive == 0 &&
			control.ConvertersAlive == 0 &&
			control.PayloadsAlive == 0 &&
			control.PayloadBuffersAlive == 0 &&
			control.ProxiesAlive == 0 &&
			control.TargetResourceHandlers == 0 &&
			current.MultiBindingsAlive == BindingCount &&
			current.ConvertersAlive == BindingCount &&
			current.PayloadsAlive == BindingCount &&
			current.PayloadBuffersAlive == BindingCount &&
			current.ProxiesAlive == BindingCount &&
			current.TargetResourceHandlers >= BindingCount;

		var builder = new StringBuilder();
		builder.AppendLine(proven ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
		builder.AppendLine("MultiBindingProxyParentRetentionRepro");
		builder.AppendLine($"Retained target labels in both scenarios: {BindingCount}");
		builder.AppendLine($"Removed MultiBindings: {BindingCount}");
		builder.AppendLine($"Payload per removed MultiBinding converter: {PayloadBytes / 1024 / 1024:0.0} MiB");
		builder.AppendLine();
		AppendRun(builder, control, before, afterControlCreate);
		builder.AppendLine();
		AppendRun(builder, current, afterControlCreate, afterCurrentCreate);
		builder.AppendLine();
		builder.AppendLine("Leak path: retained target Label -> Element._changeHandlers -> hidden MultiBinding ProxyElement.OnParentResourcesChanged -> stale proxy BindablePropertyContext -> generated mb-proxy BindableProperty propertyChanged delegate -> removed MultiBinding -> converter payload buffer.");
		builder.AppendLine("The target labels remain alive in both scenarios; the signal is whether removed MultiBinding converter payloads and hidden proxy elements collect after full GC.");
		builder.AppendLine($"dotnet-version: {Environment.Version}");

		GC.KeepAlive(control.RetainedTargets);
		GC.KeepAlive(current.RetainedTargets);

		return builder.ToString();
	}

	static RunResult CreateRun(string name, bool detachProxyParent)
	{
		var result = new RunResult(name);

		for (var i = 0; i < BindingCount; i++)
		{
			var target = new Label();
			var firstSource = new SourceValue("first-" + i);
			var secondSource = new SourceValue("second-" + i);
			var payload = new PayloadCarrier(i, PayloadBytes);
			var converter = new PayloadConverter(payload);
			var binding = new MultiBinding
			{
				Converter = converter
			};

			binding.Bindings.Add(new Binding(nameof(SourceValue.Value), source: firstSource));
			binding.Bindings.Add(new Binding(nameof(SourceValue.Value), source: secondSource));

			target.SetBinding(Label.TextProperty, binding);

			var proxy = ProxyObjectField?.GetValue(binding) as Element;
			result.RetainedTargets.Add(target);
			result.TargetRefs.Add(new WeakReference<Label>(target));
			result.MultiBindingRefs.Add(new WeakReference<MultiBinding>(binding));
			result.ConverterRefs.Add(new WeakReference<PayloadConverter>(converter));
			result.PayloadRefs.Add(new WeakReference<PayloadCarrier>(payload));
			result.PayloadBufferRefs.Add(new WeakReference<byte[]>(payload.Buffer));
			if (proxy is not null)
				result.ProxyRefs.Add(new WeakReference<Element>(proxy));

			if (detachProxyParent && proxy is not null)
				proxy.Parent = null;

			target.RemoveBinding(Label.TextProperty);
		}

		return result;
	}

	static void AppendRun(StringBuilder builder, RunResult result, long beforeBytes, long afterBytes)
	{
		builder.AppendLine($"Run: {result.Name}");
		builder.AppendLine($"  retained target labels: {result.RetainedTargets.Count}");
		builder.AppendLine($"  target labels alive after full GC: {result.TargetsAlive}/{BindingCount}");
		builder.AppendLine($"  removed MultiBindings alive after full GC: {result.MultiBindingsAlive}/{BindingCount}");
		builder.AppendLine($"  converter payload owners alive after full GC: {result.ConvertersAlive}/{BindingCount}");
		builder.AppendLine($"  payload objects alive after full GC: {result.PayloadsAlive}/{BindingCount}");
		builder.AppendLine($"  payload buffers alive after full GC: {result.PayloadBuffersAlive}/{BindingCount}");
		builder.AppendLine($"  hidden proxy elements alive after full GC: {result.ProxiesAlive}/{BindingCount}");
		builder.AppendLine($"  retained target resource handlers: {result.TargetResourceHandlers}");
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

	sealed class RunResult
	{
		public RunResult(string name)
		{
			Name = name;
		}

		public string Name { get; }
		public List<Label> RetainedTargets { get; } = new();
		public List<WeakReference<Label>> TargetRefs { get; } = new();
		public List<WeakReference<MultiBinding>> MultiBindingRefs { get; } = new();
		public List<WeakReference<PayloadConverter>> ConverterRefs { get; } = new();
		public List<WeakReference<PayloadCarrier>> PayloadRefs { get; } = new();
		public List<WeakReference<byte[]>> PayloadBufferRefs { get; } = new();
		public List<WeakReference<Element>> ProxyRefs { get; } = new();
		public int TargetsAlive { get; private set; }
		public int MultiBindingsAlive { get; private set; }
		public int ConvertersAlive { get; private set; }
		public int PayloadsAlive { get; private set; }
		public int PayloadBuffersAlive { get; private set; }
		public int ProxiesAlive { get; private set; }
		public int TargetResourceHandlers { get; private set; }
		public long RetainedPayloadBytes => PayloadBuffersAlive * (long)PayloadBytes;

		public void Measure()
		{
			TargetsAlive = CountAlive(TargetRefs);
			MultiBindingsAlive = CountAlive(MultiBindingRefs);
			ConvertersAlive = CountAlive(ConverterRefs);
			PayloadsAlive = CountAlive(PayloadRefs);
			PayloadBuffersAlive = CountAlive(PayloadBufferRefs);
			ProxiesAlive = CountAlive(ProxyRefs);
			TargetResourceHandlers = RetainedTargets.Sum(CountResourceHandlers);
		}
	}

	sealed class SourceValue
	{
		public SourceValue(string value)
		{
			Value = value;
		}

		public string Value { get; }
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

	sealed class PayloadConverter : IMultiValueConverter
	{
		public PayloadConverter(PayloadCarrier payload)
		{
			Payload = payload;
		}

		public PayloadCarrier Payload { get; }

		public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
		{
			Payload.Buffer[0] = (byte)((Payload.Buffer[0] + 1) % 255);
			return string.Join(":", values.Select(value => value?.ToString())) + ":" + Payload.Id;
		}

		public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
			targetTypes.Select(_ => Binding.DoNothing).ToArray();
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

	static int CountResourceHandlers(Element element)
	{
		if (ChangeHandlersField?.GetValue(element) is ICollection handlers)
			return handlers.Count;

		return 0;
	}
}
