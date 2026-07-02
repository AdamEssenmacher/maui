using System.Text;
using Microsoft.Maui.Controls;
using Compat = Microsoft.Maui.Controls.Compatibility;

namespace RelativeLayoutConstraintRetentionRepro;

public sealed class App : Microsoft.Maui.Controls.Application
{
	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new RunnerPage());
	}
}

sealed class RunnerPage : ContentPage
{
	bool _ran;

	public RunnerPage()
	{
		Content = new Label
		{
			Text = "Running RelativeLayout constraint retention repro...",
			HorizontalOptions = LayoutOptions.Center,
			VerticalOptions = LayoutOptions.Center
		};
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		await TryRunAsync();
	}

	protected override async void OnHandlerChanged()
	{
		base.OnHandlerChanged();
		await TryRunAsync();
	}

	async Task TryRunAsync()
	{
		if (_ran || Handler?.MauiContext is null)
			return;

		_ran = true;
		await Task.Delay(250);

		try
		{
			var report = ReproSession.Run();
			var text = report.ToText();
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(report.LeakProved ? 0 : 2);
		}
		catch (Exception ex)
		{
			var text = "RelativeLayoutConstraintRetentionRepro ERROR" + Environment.NewLine + ex;
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(1);
		}
	}
}

static class ReproSession
{
	public const string ResultsPath = "/tmp/relativelayout-constraint-retention-results.txt";

	const int Iterations = 80;
	const int PayloadBytes = 1024 * 1024;

	public static ReproReport Run()
	{
		var control = RunScenario(clearRemovedChildConstraints: true);
		var current = RunScenario(clearRemovedChildConstraints: false);

		return new ReproReport(control, current);
	}

	static ScenarioResult RunScenario(bool clearRemovedChildConstraints)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		var retainedRemovedChildren = new List<View>(Iterations);
		var layoutReferences = new List<WeakReference<Compat.RelativeLayout>>(Iterations);
		var anchorReferences = new List<WeakReference<View>>(Iterations);
		var layoutPayloadReferences = new List<WeakReference<Payload>>(Iterations);
		var anchorPayloadReferences = new List<WeakReference<Payload>>(Iterations);
		var layoutPayloadBufferReferences = new List<WeakReference<byte[]>>(Iterations);
		var anchorPayloadBufferReferences = new List<WeakReference<byte[]>>(Iterations);

		for (var i = 0; i < Iterations; i++)
		{
			CreateRemovedChild(
				i,
				clearRemovedChildConstraints,
				retainedRemovedChildren,
				layoutReferences,
				anchorReferences,
				layoutPayloadReferences,
				anchorPayloadReferences,
				layoutPayloadBufferReferences,
				anchorPayloadBufferReferences);
		}

		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			CountAlive(layoutReferences),
			CountAlive(anchorReferences),
			CountAlive(layoutPayloadReferences),
			CountAlive(anchorPayloadReferences),
			CountAlive(layoutPayloadBufferReferences),
			CountAlive(anchorPayloadBufferReferences),
			retainedRemovedChildren.Count,
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedRemovedChildren);
		return result;
	}

	static void CreateRemovedChild(
		int iteration,
		bool clearRemovedChildConstraints,
		List<View> retainedRemovedChildren,
		List<WeakReference<Compat.RelativeLayout>> layoutReferences,
		List<WeakReference<View>> anchorReferences,
		List<WeakReference<Payload>> layoutPayloadReferences,
		List<WeakReference<Payload>> anchorPayloadReferences,
		List<WeakReference<byte[]>> layoutPayloadBufferReferences,
		List<WeakReference<byte[]>> anchorPayloadBufferReferences)
	{
		var layoutPayload = new Payload($"layout-{iteration}", new byte[PayloadBytes]);
		var anchorPayload = new Payload($"anchor-{iteration}", new byte[PayloadBytes]);
		layoutPayload.Buffer[0] = (byte)iteration;
		anchorPayload.Buffer[0] = (byte)(iteration + 1);

		var layout = new Compat.RelativeLayout
		{
			BindingContext = layoutPayload
		};

		var anchor = new Label
		{
			Text = $"anchor {iteration}",
			BindingContext = anchorPayload
		};

		var removedChild = new BoxView
		{
			Color = Colors.CornflowerBlue
		};

		layout.Children.Add(
			anchor,
			Compat.Constraint.Constant(0),
			Compat.Constraint.Constant(0),
			Compat.Constraint.Constant(120),
			Compat.Constraint.Constant(32));

		layout.Children.Add(
			removedChild,
			Compat.Constraint.RelativeToView(anchor, static (_, view) => view.X + view.Width + 8),
			Compat.Constraint.RelativeToView(anchor, static (_, view) => view.Y),
			Compat.Constraint.RelativeToParent(static parent => Math.Max(16, parent.Width / 2)),
			Compat.Constraint.Constant(32));

		layout.Children.Remove(removedChild);
		layout.Children.Remove(anchor);

		if (clearRemovedChildConstraints)
			ClearRelativeLayoutConstraintState(removedChild);

		retainedRemovedChildren.Add(removedChild);
		layoutReferences.Add(new WeakReference<Compat.RelativeLayout>(layout));
		anchorReferences.Add(new WeakReference<View>(anchor));
		layoutPayloadReferences.Add(new WeakReference<Payload>(layoutPayload));
		anchorPayloadReferences.Add(new WeakReference<Payload>(anchorPayload));
		layoutPayloadBufferReferences.Add(new WeakReference<byte[]>(layoutPayload.Buffer));
		anchorPayloadBufferReferences.Add(new WeakReference<byte[]>(anchorPayload.Buffer));
	}

	static void ClearRelativeLayoutConstraintState(BindableObject removedChild)
	{
		Compat.RelativeLayout.SetBoundsConstraint(removedChild, null!);
		Compat.RelativeLayout.SetXConstraint(removedChild, null!);
		Compat.RelativeLayout.SetYConstraint(removedChild, null!);
		Compat.RelativeLayout.SetWidthConstraint(removedChild, null!);
		Compat.RelativeLayout.SetHeightConstraint(removedChild, null!);
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

	static void ForceGc()
	{
		for (var i = 0; i < 6; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(75);
		}
	}

	sealed class Payload
	{
		public Payload(string name, byte[] buffer)
		{
			Name = name;
			Buffer = buffer;
		}

		public string Name { get; }

		public byte[] Buffer { get; }
	}

	public readonly record struct ScenarioResult(
		int LayoutsAlive,
		int AnchorsAlive,
		int LayoutPayloadsAlive,
		int AnchorPayloadsAlive,
		int LayoutPayloadBuffersAlive,
		int AnchorPayloadBuffersAlive,
		int RetainedRemovedChildren,
		long HeapBefore,
		long HeapAfter)
	{
		public long HeapDelta => HeapAfter - HeapBefore;
	}

	public readonly record struct ReproReport(ScenarioResult Control, ScenarioResult Current)
	{
		public bool LeakProved =>
			Control.LayoutsAlive == 0 &&
			Control.AnchorsAlive == 0 &&
			Control.LayoutPayloadsAlive == 0 &&
			Control.AnchorPayloadsAlive == 0 &&
			Control.LayoutPayloadBuffersAlive == 0 &&
			Control.AnchorPayloadBuffersAlive == 0 &&
			Current.LayoutsAlive == Iterations &&
			Current.AnchorsAlive == Iterations &&
			Current.LayoutPayloadsAlive == Iterations &&
			Current.AnchorPayloadsAlive == Iterations &&
			Current.LayoutPayloadBuffersAlive == Iterations &&
			Current.AnchorPayloadBuffersAlive == Iterations;

		public string ToText()
		{
			var retainedPayloadBytes = (Current.LayoutPayloadBuffersAlive + Current.AnchorPayloadBuffersAlive) * PayloadBytes;
			var builder = new StringBuilder();
			builder.AppendLine("RelativeLayout removed-child constraint retention repro");
			builder.AppendLine($"Iterations: {Iterations}");
			builder.AppendLine($"Retained removed child views per run: {Iterations}");
			builder.AppendLine($"Payload per discarded layout: {PayloadBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine($"Payload per discarded anchor sibling: {PayloadBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine("Root under test: removed child attached RelativeLayout constraint properties");
			builder.AppendLine("Cleanup under test: removing children from RelativeLayout without clearing attached constraints");
			builder.AppendLine($"Leak proved: {LeakProved}");
			builder.AppendLine(LeakProved ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			builder.AppendLine();
			builder.AppendLine("control: retained removed children after clearing RelativeLayout attached constraint state");
			AppendScenario(builder, Control);
			builder.AppendLine();
			builder.AppendLine("current cleanup: retained removed children after RelativeLayout.Children.Remove without clearing attached constraints");
			AppendScenario(builder, Current);
			builder.AppendLine($"  retained payload bytes: {retainedPayloadBytes:N0}");
			builder.AppendLine();
			builder.AppendLine("Leak path: app cache -> removed child -> BoundsConstraint/X/Y/Width/Height attached properties -> captured RelativeLayout and anchor sibling -> BindingContext payloads");
			builder.AppendLine("Distinct from C307: this keeps only removed child views, not public Children collection wrappers.");
			builder.AppendLine($"dotnet-version: {Environment.Version}");
			return builder.ToString();
		}

		static void AppendScenario(StringBuilder builder, ScenarioResult result)
		{
			builder.AppendLine($"  removed children retained by app cache: {result.RetainedRemovedChildren}");
			builder.AppendLine($"  layouts alive after full GC: {result.LayoutsAlive}/{Iterations}");
			builder.AppendLine($"  anchor siblings alive after full GC: {result.AnchorsAlive}/{Iterations}");
			builder.AppendLine($"  layout payloads alive after full GC: {result.LayoutPayloadsAlive}/{Iterations}");
			builder.AppendLine($"  anchor payloads alive after full GC: {result.AnchorPayloadsAlive}/{Iterations}");
			builder.AppendLine($"  layout payload buffers alive after full GC: {result.LayoutPayloadBuffersAlive}/{Iterations}");
			builder.AppendLine($"  anchor payload buffers alive after full GC: {result.AnchorPayloadBuffersAlive}/{Iterations}");
			builder.AppendLine($"  managed heap delta: {result.HeapDelta / 1024d / 1024d:0.0} MiB");
		}
	}
}
