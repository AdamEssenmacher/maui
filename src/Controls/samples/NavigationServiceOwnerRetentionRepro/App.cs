using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui.Controls;

namespace NavigationServiceOwnerRetentionRepro;

public sealed class App : Application
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
			Text = "Running Navigation service owner retention repro...",
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
			var text = "NavigationServiceOwnerRetentionRepro ERROR" + Environment.NewLine + ex;
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(1);
		}
	}
}

static class ReproSession
{
	public const string ResultsPath = "/tmp/navigation-service-owner-retention-results.txt";

	const int OwnersPerType = 40;
	const int OwnerTypes = 4;
	const int Iterations = OwnersPerType * OwnerTypes;
	const int PayloadBytes = 1024 * 1024;

	public static ReproReport Run()
	{
		var control = RunScenario(clearOwnerReferences: true);
		var current = RunScenario(clearOwnerReferences: false);

		return new ReproReport(control, current);
	}

	static ScenarioResult RunScenario(bool clearOwnerReferences)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
		var payloads = new ConditionalWeakTable<object, NavigationOwnerPayload>();
		var retainedNavigationServices = new List<INavigation>(Iterations);
		var ownerReferences = new List<WeakReference<object>>(Iterations);
		var payloadReferences = new List<WeakReference<NavigationOwnerPayload>>(Iterations);
		var payloadBufferReferences = new List<WeakReference<byte[]>>(Iterations);
		var countsByOwnerType = new Dictionary<string, int>(StringComparer.Ordinal);

		for (var i = 0; i < OwnersPerType; i++)
		{
			CreateRetainedNavigationService("NavigationPage", i, clearOwnerReferences, payloads, retainedNavigationServices, ownerReferences, payloadReferences, payloadBufferReferences, countsByOwnerType);
			CreateRetainedNavigationService("Window", i, clearOwnerReferences, payloads, retainedNavigationServices, ownerReferences, payloadReferences, payloadBufferReferences, countsByOwnerType);
			CreateRetainedNavigationService("Shell", i, clearOwnerReferences, payloads, retainedNavigationServices, ownerReferences, payloadReferences, payloadBufferReferences, countsByOwnerType);
			CreateRetainedNavigationService("ShellSection", i, clearOwnerReferences, payloads, retainedNavigationServices, ownerReferences, payloadReferences, payloadBufferReferences, countsByOwnerType);
		}

		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			CountAlive(ownerReferences),
			CountAlive(payloadReferences),
			CountAlive(payloadBufferReferences),
			retainedNavigationServices.Count,
			countsByOwnerType,
			heapBefore,
			heapAfter);

		GC.KeepAlive(payloads);
		GC.KeepAlive(retainedNavigationServices);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateRetainedNavigationService(
		string ownerType,
		int iteration,
		bool clearOwnerReferences,
		ConditionalWeakTable<object, NavigationOwnerPayload> payloads,
		List<INavigation> retainedNavigationServices,
		List<WeakReference<object>> ownerReferences,
		List<WeakReference<NavigationOwnerPayload>> payloadReferences,
		List<WeakReference<byte[]>> payloadBufferReferences,
		Dictionary<string, int> countsByOwnerType)
	{
		var owner = CreateOwner(ownerType, iteration);
		var navigation = GetNavigation(owner);
		var payload = new NavigationOwnerPayload($"{ownerType.ToLowerInvariant()}-{iteration}", new byte[PayloadBytes]);
		payload.Buffer[0] = (byte)iteration;

		payloads.Add(owner, payload);

		if (clearOwnerReferences)
			ClearOwnerReferences(navigation, owner);

		retainedNavigationServices.Add(navigation);
		ownerReferences.Add(new WeakReference<object>(owner));
		payloadReferences.Add(new WeakReference<NavigationOwnerPayload>(payload));
		payloadBufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));
		countsByOwnerType[ownerType] = countsByOwnerType.GetValueOrDefault(ownerType) + 1;

		owner = null!;
		navigation = null!;
		payload = null!;
	}

	static object CreateOwner(string ownerType, int iteration)
	{
		return ownerType switch
		{
			"NavigationPage" => new NavigationPage(new ContentPage { Title = $"Orders {iteration}" }),
			"Window" => new Window(new ContentPage { Title = $"Workspace {iteration}" }),
			"Shell" => CreateShell(iteration),
			"ShellSection" => CreateShellSection(iteration),
			_ => throw new ArgumentOutOfRangeException(nameof(ownerType), ownerType, null)
		};
	}

	static Shell CreateShell(int iteration)
	{
		var shell = new Shell { FlyoutBehavior = FlyoutBehavior.Disabled };
		shell.Items.Add(new ShellContent
		{
			Title = $"Home {iteration}",
			Content = new ContentPage { Title = $"Home {iteration}" }
		});

		return shell;
	}

	static ShellSection CreateShellSection(int iteration)
	{
		var section = new ShellSection { Title = $"Tab {iteration}" };
		section.Items.Add(new ShellContent
		{
			Title = $"Details {iteration}",
			Content = new ContentPage { Title = $"Details {iteration}" }
		});

		return section;
	}

	static INavigation GetNavigation(object owner)
	{
		return owner switch
		{
			NavigationPage navigationPage => navigationPage.Navigation,
			Window window => window.Navigation,
			Shell shell => shell.Navigation,
			ShellSection shellSection => shellSection.Navigation,
			_ => throw new ArgumentOutOfRangeException(nameof(owner), owner.GetType().FullName, null)
		};
	}

	static void ClearOwnerReferences(INavigation navigation, object owner)
	{
		var cleared = false;
		var type = navigation.GetType();

		while (type is not null)
		{
			foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
			{
				if (!ReferenceEquals(field.GetValue(navigation), owner))
					continue;

				field.SetValue(navigation, null);
				cleared = true;
			}

			type = type.BaseType;
		}

		if (!cleared)
			throw new InvalidOperationException($"Could not clear an owner field from {navigation.GetType().FullName}.");
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

	sealed class NavigationOwnerPayload
	{
		public NavigationOwnerPayload(string name, byte[] buffer)
		{
			Name = name;
			Buffer = buffer;
		}

		public string Name { get; }

		public byte[] Buffer { get; }
	}

	public readonly record struct ScenarioResult(
		int OwnersAlive,
		int PayloadsAlive,
		int PayloadBuffersAlive,
		int RetainedNavigationServices,
		IReadOnlyDictionary<string, int> CountsByOwnerType,
		long HeapBefore,
		long HeapAfter)
	{
		public long HeapDelta => HeapAfter - HeapBefore;
	}

	public readonly record struct ReproReport(ScenarioResult Control, ScenarioResult Current)
	{
		public bool LeakProved =>
			Control.OwnersAlive == 0 &&
			Control.PayloadsAlive == 0 &&
			Control.PayloadBuffersAlive == 0 &&
			Current.OwnersAlive == Iterations &&
			Current.PayloadsAlive == Iterations &&
			Current.PayloadBuffersAlive == Iterations;

		public string ToText()
		{
			var retainedBytes = Current.PayloadBuffersAlive * PayloadBytes;
			var builder = new StringBuilder();
			builder.AppendLine(LeakProved ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			builder.AppendLine("NavigationServiceOwnerRetentionRepro");
			builder.AppendLine($"Owner instances created: {Iterations}");
			builder.AppendLine($"Owner types: NavigationPage, Window, Shell, ShellSection");
			builder.AppendLine($"Owners per type: {OwnersPerType}");
			builder.AppendLine($"Retained public INavigation handles per run: {Iterations}");
			builder.AppendLine($"Payload per discarded owner: {PayloadBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Run: control: retained INavigation handles after clearing private owner fields");
			AppendScenario(builder, Control);
			builder.AppendLine();
			builder.AppendLine("Run: current: retained INavigation handles with MAUI owner references intact");
			AppendScenario(builder, Current);
			builder.AppendLine($"  retained payload bytes: {retainedBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Leak path: app INavigation cache -> owner-specific NavigationImpl owner field -> discarded owner -> ConditionalWeakTable payload");
			builder.AppendLine("Distinct from NavigationStack wrapper leaks: the retained object is the public Navigation service handle itself.");
			builder.AppendLine($"dotnet-version: {Environment.Version}");
			return builder.ToString();
		}

		static void AppendScenario(StringBuilder builder, ScenarioResult result)
		{
			builder.AppendLine($"  INavigation handles retained by app cache: {result.RetainedNavigationServices}");
			builder.AppendLine($"  owners alive after full GC: {result.OwnersAlive}/{Iterations}");
			builder.AppendLine($"  owner payloads alive after full GC: {result.PayloadsAlive}/{Iterations}");
			builder.AppendLine($"  owner payload buffers alive after full GC: {result.PayloadBuffersAlive}/{Iterations}");
			builder.AppendLine($"  owner handles created: {string.Join(", ", result.CountsByOwnerType.Select(pair => $"{pair.Key}={pair.Value}"))}");
			builder.AppendLine($"  managed heap delta: {result.HeapDelta / 1024d / 1024d:0.0} MiB");
		}
	}
}
