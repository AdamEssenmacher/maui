using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Maui.Controls;

namespace TemplateBindingParentSetPendingRetentionRepro;

public class App : Application
{
	const int BindingCount = 120;
	const int PayloadBytes = 1024 * 1024;
	const string ResultPath = "/tmp/templatebinding-parentset-pending-retention-results.txt";

	static readonly FieldInfo? ParentSetField = typeof(Element).GetField("ParentSet", BindingFlags.Instance | BindingFlags.NonPublic);

	public App()
	{
		Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(250), RunAndQuit);
	}

	protected override Window CreateWindow(IActivationState? activationState) =>
		new(new ContentPage
		{
			Content = new Label
			{
				Text = "Running TemplateBinding ParentSet pending retention repro...",
				Margin = new Thickness(24)
			}
		});

	void RunAndQuit()
	{
		try
		{
			File.WriteAllText(ResultPath, RunProof());
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

		var control = CreateRun("control: parented label completes templated-parent waits", keepTargetUnparented: false);
		var afterControlCreate = ForceFullGC();
		control.Measure();

		var current = CreateRun("current: retained off-tree label with removed TemplateBindings", keepTargetUnparented: true);
		var afterCurrentCreate = ForceFullGC();
		current.Measure();

		var proven =
			control.TargetAlive == 1 &&
			current.TargetAlive == 1 &&
			control.BindingsAlive == 0 &&
			control.ConvertersAlive == 0 &&
			control.PayloadsAlive == 0 &&
			control.PayloadBuffersAlive == 0 &&
			control.PendingParentSetHandlers == 0 &&
			current.BindingsAlive == BindingCount &&
			current.ConvertersAlive == BindingCount &&
			current.PayloadsAlive == BindingCount &&
			current.PayloadBuffersAlive == BindingCount &&
			current.PendingParentSetHandlers >= BindingCount;

		var builder = new StringBuilder();
		builder.AppendLine(proven ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
		builder.AppendLine("TemplateBindingParentSetPendingRetentionRepro");
		builder.AppendLine("Retained target Label instances in both scenarios: 1");
		builder.AppendLine($"TemplateBinding apply/remove cycles before parenting: {BindingCount}");
		builder.AppendLine($"Payload per removed binding converter: {PayloadBytes / 1024 / 1024:0.0} MiB");
		builder.AppendLine();
		AppendRun(builder, control, before, afterControlCreate);
		builder.AppendLine();
		AppendRun(builder, current, afterControlCreate, afterCurrentCreate);
		builder.AppendLine();
		builder.AppendLine("Leak path: retained off-tree target Label -> ParentSet event invocation list -> TemplateUtilities.GetRealParentAsync handler -> TaskCompletionSource continuations -> async TemplateBinding.Apply state machine -> removed TemplateBinding -> converter -> payload.");
		builder.AppendLine("The control parents the target under a templated host, so FindTemplatedParentAsync completes before each binding is removed.");
		builder.AppendLine("The current path keeps the target live but off-tree, repeatedly applies and removes TemplateBindings, and leaves each removed binding captured by an incomplete templated-parent wait.");
		builder.AppendLine($"dotnet-version: {Environment.Version}");

		GC.KeepAlive(control.RetainedTarget);
		GC.KeepAlive(control.RetainedHost);
		GC.KeepAlive(current.RetainedTarget);

		return builder.ToString();
	}

	static RunResult CreateRun(string name, bool keepTargetUnparented)
	{
		var result = new RunResult(name);
		var target = new Label();
		result.RetainedTarget = target;
		result.TargetRef = new WeakReference<Label>(target);

		if (!keepTargetUnparented)
		{
			var host = new TemplatedView
			{
				ControlTemplate = new ControlTemplate(() => new ContentView())
			};
			target.Parent = host;
			host.Parent = Current;
			result.RetainedHost = host;
		}

		for (var i = 0; i < BindingCount; i++)
		{
			var payload = new PayloadCarrier(i, PayloadBytes);
			var converter = new PayloadConverter(payload);
			var binding = new TemplateBinding("BindingContext", converter: converter);

			target.SetBinding(Label.TextProperty, binding);
			target.RemoveBinding(Label.TextProperty);

			result.BindingRefs.Add(new WeakReference<TemplateBinding>(binding));
			result.ConverterRefs.Add(new WeakReference<PayloadConverter>(converter));
			result.PayloadRefs.Add(new WeakReference<PayloadCarrier>(payload));
			result.PayloadBufferRefs.Add(new WeakReference<byte[]>(payload.Buffer));
		}

		return result;
	}

	static void AppendRun(StringBuilder builder, RunResult result, long beforeBytes, long afterBytes)
	{
		builder.AppendLine($"Run: {result.Name}");
		builder.AppendLine("  retained target labels: 1");
		builder.AppendLine($"  target alive after full GC: {result.TargetAlive}/1");
		builder.AppendLine($"  pending ParentSet handlers on target: {result.PendingParentSetHandlers}");
		builder.AppendLine($"  removed TemplateBindings alive after full GC: {result.BindingsAlive}/{BindingCount}");
		builder.AppendLine($"  removed converters alive after full GC: {result.ConvertersAlive}/{BindingCount}");
		builder.AppendLine($"  payload objects alive after full GC: {result.PayloadsAlive}/{BindingCount}");
		builder.AppendLine($"  payload buffers alive after full GC: {result.PayloadBuffersAlive}/{BindingCount}");
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

	static int CountPendingParentSetHandlers(Element element)
	{
		if (ParentSetField?.GetValue(element) is not MulticastDelegate parentSet)
			return 0;

		return parentSet.GetInvocationList().Length;
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
		public TemplatedView? RetainedHost { get; set; }
		public WeakReference<Label>? TargetRef { get; set; }
		public List<WeakReference<TemplateBinding>> BindingRefs { get; } = new();
		public List<WeakReference<PayloadConverter>> ConverterRefs { get; } = new();
		public List<WeakReference<PayloadCarrier>> PayloadRefs { get; } = new();
		public List<WeakReference<byte[]>> PayloadBufferRefs { get; } = new();
		public int TargetAlive { get; private set; }
		public int BindingsAlive { get; private set; }
		public int ConvertersAlive { get; private set; }
		public int PayloadsAlive { get; private set; }
		public int PayloadBuffersAlive { get; private set; }
		public int PendingParentSetHandlers { get; private set; }
		public long RetainedPayloadBytes => PayloadBuffersAlive * (long)PayloadBytes;

		public void Measure()
		{
			TargetAlive = TargetRef?.TryGetTarget(out _) == true ? 1 : 0;
			BindingsAlive = CountAlive(BindingRefs);
			ConvertersAlive = CountAlive(ConverterRefs);
			PayloadsAlive = CountAlive(PayloadRefs);
			PayloadBuffersAlive = CountAlive(PayloadBufferRefs);
			PendingParentSetHandlers = RetainedTarget is not null ? CountPendingParentSetHandlers(RetainedTarget) : 0;
		}
	}

	sealed class PayloadConverter : IValueConverter
	{
		readonly PayloadCarrier _payload;

		public PayloadConverter(PayloadCarrier payload)
		{
			_payload = payload;
		}

		public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
		{
			GC.KeepAlive(_payload);
			return string.Empty;
		}

		public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
			throw new NotSupportedException();
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
}
