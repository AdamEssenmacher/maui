using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui.Controls;

namespace XamlTransientNameScopeDetachedChildRetentionRepro;

public sealed class MainPage : ContentPage
{
	const int PageCount = 96;
	const int PayloadBytes = 1024 * 1024;
	const int SurvivorTolerance = 2;

	readonly string? _resultsPath;
	readonly Label _status;
	bool _started;

	public MainPage(string? resultsPath)
	{
		_resultsPath = resultsPath;
		Title = "XAML Transient NameScope Detached Child Retention";

		_status = new Label
		{
			Text = "Running XAML transient NameScope detached-child retention repro...",
			HorizontalOptions = LayoutOptions.Center,
			VerticalOptions = LayoutOptions.Center
		};

		Content = new Grid
		{
			Padding = 24,
			Children = { _status }
		};

		Loaded += OnLoaded;
	}

	async void OnLoaded(object? sender, EventArgs e)
	{
		if (_started)
			return;

		_started = true;

		try
		{
			var result = await RunReproAsync();
			var report = result.ToReport();

			_status.Text = result.Proven
				? "PROVEN: detached XAML children retained discarded page roots through transient NameScope."
				: "NOT PROVEN: discarded page roots did not remain alive.";

			if (!string.IsNullOrWhiteSpace(_resultsPath))
				File.WriteAllText(_resultsPath, report);

			await Task.Delay(250);
			Environment.Exit(result.Proven ? 0 : 2);
		}
		catch (Exception ex)
		{
			var report = "XAML transient NameScope detached-child retention repro failed." + Environment.NewLine + ex;
			_status.Text = report;

			if (!string.IsNullOrWhiteSpace(_resultsPath))
				File.WriteAllText(_resultsPath, report);

			await Task.Delay(250);
			Environment.Exit(3);
		}
	}

	static async Task<ReproResult> RunReproAsync()
	{
		var control = await RunScenarioAsync(clearTransientNameScope: true);
		var current = await RunScenarioAsync(clearTransientNameScope: false);

		var controlCollected = control.PageSurvivors <= SurvivorTolerance
			&& control.PayloadSurvivors <= SurvivorTolerance
			&& control.PayloadBufferSurvivors <= SurvivorTolerance;

		var currentRetained = current.PageSurvivors >= PageCount - SurvivorTolerance
			&& current.PayloadSurvivors >= PageCount - SurvivorTolerance
			&& current.PayloadBufferSurvivors >= PageCount - SurvivorTolerance;

		return new ReproResult(control, current, controlCollected && currentRetained);
	}

	static async Task<ScenarioResult> RunScenarioAsync(bool clearTransientNameScope)
	{
		var pageRefs = new List<WeakReference<DetachedChildPage>>(PageCount);
		var payloadRefs = new List<WeakReference<Payload>>(PageCount);
		var payloadBufferRefs = new List<WeakReference<byte[]>>(PageCount);
		var detachedChildren = new List<Label>(PageCount);

		ForceFullGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		for (var i = 0; i < PageCount; i++)
		{
			CreateAndDropPage(
				i,
				clearTransientNameScope,
				detachedChildren,
				pageRefs,
				payloadRefs,
				payloadBufferRefs);

			if (i % 12 == 0)
				await Task.Yield();
		}

		await WaitForCollectionAsync();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			clearTransientNameScope ? "Control: clear detached child transientNamescope" : "Current MAUI behavior",
			clearTransientNameScope,
			detachedChildren.Count,
			CountAlive(pageRefs),
			CountAlive(payloadRefs),
			CountAlive(payloadBufferRefs),
			heapBefore,
			heapAfter);

		GC.KeepAlive(detachedChildren);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateAndDropPage(
		int index,
		bool clearTransientNameScope,
		List<Label> detachedChildren,
		List<WeakReference<DetachedChildPage>> pageRefs,
		List<WeakReference<Payload>> payloadRefs,
		List<WeakReference<byte[]>> payloadBufferRefs)
	{
		var payload = new Payload(index, PayloadBytes);
		var page = new DetachedChildPage(payload)
		{
			Title = $"Detached-child page {index}"
		};

		var child = page.DetachNamedChild();

		if (clearTransientNameScope)
			child.transientNamescope = null;

		detachedChildren.Add(child);
		pageRefs.Add(new WeakReference<DetachedChildPage>(page));
		payloadRefs.Add(new WeakReference<Payload>(payload));
		payloadBufferRefs.Add(new WeakReference<byte[]>(payload.Buffer));
	}

	static async Task WaitForCollectionAsync()
	{
		for (var i = 0; i < 6; i++)
		{
			ForceFullGc();
			await Task.Delay(50);
		}
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
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

	readonly record struct ScenarioResult(
		string Name,
		bool ClearTransientNameScope,
		int RetainedDetachedChildren,
		int PageSurvivors,
		int PayloadSurvivors,
		int PayloadBufferSurvivors,
		long HeapBeforeBytes,
		long HeapAfterBytes)
	{
		public long HeapDeltaBytes => HeapAfterBytes - HeapBeforeBytes;
		public double RetainedPayloadMiB => PayloadBufferSurvivors * PayloadBytes / 1024d / 1024d;

		public void AppendTo(StringBuilder builder)
		{
			builder.AppendLine(Name);
			builder.AppendLine($"  Retained detached children: {RetainedDetachedChildren}");
			builder.AppendLine($"  Clear transientNamescope: {ClearTransientNameScope}");
			builder.AppendLine($"  Page survivors: {PageSurvivors}/{PageCount}");
			builder.AppendLine($"  Payload survivors: {PayloadSurvivors}/{PageCount}");
			builder.AppendLine($"  Payload buffer survivors: {PayloadBufferSurvivors}/{PageCount}");
			builder.AppendLine($"  Retained payload estimate: {RetainedPayloadMiB:F1} MiB");
			builder.AppendLine($"  Managed heap before: {HeapBeforeBytes:N0} bytes");
			builder.AppendLine($"  Managed heap after: {HeapAfterBytes:N0} bytes");
			builder.AppendLine($"  Managed heap delta: {HeapDeltaBytes:N0} bytes");
		}
	}

	readonly record struct ReproResult(ScenarioResult Control, ScenarioResult Current, bool Proven)
	{
		public string ToReport()
		{
			var builder = new StringBuilder();
			builder.AppendLine("XAML transient NameScope detached-child retention repro");
			builder.AppendLine($"Result: {(Proven ? "PROVEN" : "NOT PROVEN")}");
			builder.AppendLine();
			Control.AppendTo(builder);
			builder.AppendLine();
			Current.AppendTo(builder);
			builder.AppendLine();
			builder.AppendLine("Expected proof signal:");
			builder.AppendLine($"- Control survivors <= {SurvivorTolerance} after clearing the detached child's transientNamescope.");
			builder.AppendLine($"- Current behavior survivors >= {PageCount - SurvivorTolerance} while only detached child elements remain rooted.");
			builder.AppendLine();
			builder.AppendLine("Retained graph under current behavior:");
			builder.AppendLine("Retained detached child -> Element.transientNamescope -> NameScope -> x:Name root ContentPage -> page payload");
			return builder.ToString();
		}
	}
}
