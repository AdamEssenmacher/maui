using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui.Controls;

namespace ShellFlyoutItemsStaleGroupRetentionRepro;

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
			Text = "Running Shell flyout stale group retention repro...",
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
			var text = "ShellFlyoutItemsStaleGroupRetentionRepro ERROR" + Environment.NewLine + ex;
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(1);
		}
	}
}

static partial class ReproSession
{
	public const string ResultsPath = "/tmp/shell-flyoutitems-stale-group-retention-results.txt";

	public const int ShellCount = 48;
	public const int RetiredGroupsPerShell = 8;
	const int PayloadBytes = 1024 * 1024;

	static readonly MethodInfo s_sendFlyoutItemsChanged =
		typeof(Shell).GetMethod("SendFlyoutItemsChanged", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(Shell).FullName, "SendFlyoutItemsChanged");

	static readonly FieldInfo s_flyoutManagerField =
		typeof(Shell).GetField("_flyoutManager", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(Shell).FullName, "_flyoutManager");

	public static ReproReport Run()
	{
		var control = RunScenario(removeStaleFlyoutGroups: true);
		var current = RunScenario(removeStaleFlyoutGroups: false);

		return new ReproReport(control, current);
	}

	static ScenarioResult RunScenario(bool removeStaleFlyoutGroups)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
		var retainedShells = new List<Shell>(ShellCount);
		var retainedFlyoutHandles = new List<object>(ShellCount);
		var retiredContentReferences = new List<WeakReference<ShellContent>>(ShellCount * RetiredGroupsPerShell);
		var payloadReferences = new List<WeakReference<FlyoutPayload>>(ShellCount * RetiredGroupsPerShell);
		var payloadBufferReferences = new List<WeakReference<byte[]>>(ShellCount * RetiredGroupsPerShell);
		var staleGroupCounts = new List<int>(ShellCount);
		var expectedGroupCounts = new List<int>(ShellCount);

		for (var shellIndex = 0; shellIndex < ShellCount; shellIndex++)
		{
			CreateShellAndChurnFlyoutGroups(
				removeStaleFlyoutGroups,
				shellIndex,
				retainedShells,
				retainedFlyoutHandles,
				retiredContentReferences,
				payloadReferences,
				payloadBufferReferences,
				staleGroupCounts,
				expectedGroupCounts);
		}

		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			retainedShells.Count,
			retainedFlyoutHandles.Count,
			CountAlive(retiredContentReferences),
			CountAlive(payloadReferences),
			CountAlive(payloadBufferReferences),
			Sum(staleGroupCounts),
			Sum(expectedGroupCounts),
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedShells);
		GC.KeepAlive(retainedFlyoutHandles);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateShellAndChurnFlyoutGroups(
		bool removeStaleFlyoutGroups,
		int shellIndex,
		List<Shell> retainedShells,
		List<object> retainedFlyoutHandles,
		List<WeakReference<ShellContent>> retiredContentReferences,
		List<WeakReference<FlyoutPayload>> payloadReferences,
		List<WeakReference<byte[]>> payloadBufferReferences,
		List<int> staleGroupCounts,
		List<int> expectedGroupCounts)
	{
		var shell = new Shell
		{
			Title = $"Live shell {shellIndex:000}"
		};
		var retiredItems = new List<FlyoutItem>(RetiredGroupsPerShell);

		for (var groupIndex = 0; groupIndex < RetiredGroupsPerShell; groupIndex++)
		{
			var payload = new FlyoutPayload(
				$"retired-flyout-{shellIndex:000}-{groupIndex:000}",
				$"Retired tenant workspace {shellIndex:000}/{groupIndex:000} with cached menu state and dashboard metadata",
				new byte[PayloadBytes]);
			payload.Buffer[0] = (byte)groupIndex;
			payload.Buffer[^1] = (byte)(255 - groupIndex);

			var content = new ShellContent
			{
				Title = $"Retired workspace {groupIndex:000}",
				BindingContext = payload
			};
			var item = new FlyoutItem
			{
				Title = $"Retired group {groupIndex:000}",
				FlyoutDisplayOptions = FlyoutDisplayOptions.AsMultipleItems,
				Items =
				{
					new ShellSection
					{
						Title = $"Retired section {groupIndex:000}",
						Items = { content }
					}
				}
			};

			shell.Items.Add(item);
			retiredItems.Add(item);
			retiredContentReferences.Add(new WeakReference<ShellContent>(content));
			payloadReferences.Add(new WeakReference<FlyoutPayload>(payload));
			payloadBufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));
		}

		ForceFlyoutSync(shell);
		var flyoutHandle = shell.FlyoutItems;

		shell.Items.Clear();
		ForceFlyoutSync(shell);

		var activeItem = CreateLightweightFlyoutItem(shellIndex);
		shell.Items.Add(activeItem);
		shell.CurrentItem = activeItem;
		ForceFlyoutSync(shell);

		var expectedGroupCount = GetExpectedFlyoutGroupCount(shell);
		if (removeStaleFlyoutGroups)
			RemoveStaleFlyoutGroups(flyoutHandle, expectedGroupCount);

		staleGroupCounts.Add(CountPublicFlyoutGroups(flyoutHandle));
		expectedGroupCounts.Add(expectedGroupCount);
		retainedShells.Add(shell);
		retainedFlyoutHandles.Add(flyoutHandle);

		retiredItems = null!;
		activeItem = null!;
		shell = null!;
		flyoutHandle = null!;
	}

	static FlyoutItem CreateLightweightFlyoutItem(int shellIndex)
	{
		return new FlyoutItem
		{
			Title = $"Active group {shellIndex:000}",
			FlyoutDisplayOptions = FlyoutDisplayOptions.AsMultipleItems,
			Items =
			{
				new ShellSection
				{
					Title = $"Active section {shellIndex:000}",
					Items =
					{
						new ShellContent
						{
							Title = $"Active content {shellIndex:000}",
							Content = new ContentPage
							{
								Title = $"Active page {shellIndex:000}",
								Content = new Label { Text = "Active lightweight page" }
							}
						}
					}
				}
			}
		};
	}

	static void ForceFlyoutSync(Shell shell)
	{
		s_sendFlyoutItemsChanged.Invoke(shell, null);
	}

	static int GetExpectedFlyoutGroupCount(Shell shell)
	{
		var flyoutManager = s_flyoutManagerField.GetValue(shell)
			?? throw new InvalidOperationException("Shell flyout manager was null.");
		var generateMethod = flyoutManager.GetType().GetMethod("GenerateFlyoutGrouping", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
			?? throw new MissingMethodException(flyoutManager.GetType().FullName, "GenerateFlyoutGrouping");
		var groups = (ICollection)(generateMethod.Invoke(flyoutManager, null)
			?? throw new InvalidOperationException("GenerateFlyoutGrouping returned null."));

		return groups.Count;
	}

	static int CountPublicFlyoutGroups(IEnumerable flyoutItems)
	{
		var count = 0;
		foreach (var _ in flyoutItems)
			count++;

		return count;
	}

	static void RemoveStaleFlyoutGroups(IEnumerable flyoutItems, int expectedGroupCount)
	{
		var listProperty = flyoutItems.GetType().GetProperty("List", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
			?? throw new MissingMemberException(flyoutItems.GetType().FullName, "List");
		var sourceList = (IList)(listProperty.GetValue(flyoutItems)
			?? throw new InvalidOperationException("FlyoutItems source list was null."));

		while (sourceList.Count > expectedGroupCount)
			sourceList.RemoveAt(sourceList.Count - 1);
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

	static int Sum(IEnumerable<int> values)
	{
		var result = 0;
		foreach (var value in values)
			result += value;

		return result;
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
}

sealed class FlyoutPayload
{
	public FlyoutPayload(string name, string description, byte[] buffer)
	{
		Name = name;
		Description = description;
		Buffer = buffer;
	}

	public string Name { get; }
	public string Description { get; }
	public byte[] Buffer { get; }
	public string DisplayName => $"{Name} ({Buffer.Length / (1024 * 1024)} MiB payload)";
}

readonly record struct ScenarioResult(
	int RetainedShells,
	int RetainedFlyoutHandles,
	int RetiredContentsAlive,
	int PayloadsAlive,
	int PayloadBuffersAlive,
	int PublicFlyoutGroupCount,
	int ExpectedFlyoutGroupCount,
	long HeapBefore,
	long HeapAfter)
{
	public long HeapDelta => HeapAfter - HeapBefore;
	public long RetainedPayloadBytes => (long)PayloadBuffersAlive * 1024 * 1024;
}

readonly record struct ReproReport(ScenarioResult Control, ScenarioResult Current)
{
	public int RetiredContentCount => ReproSession.ShellCount * ReproSession.RetiredGroupsPerShell;

	public bool LeakProved =>
		Control.PayloadBuffersAlive == 0 &&
		Control.PublicFlyoutGroupCount == Control.ExpectedFlyoutGroupCount &&
		Current.PayloadBuffersAlive > 0 &&
		Current.PublicFlyoutGroupCount > Current.ExpectedFlyoutGroupCount;

	public string ToText()
	{
		var builder = new StringBuilder();
		builder.AppendLine(LeakProved ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
		builder.AppendLine("ShellFlyoutItemsStaleGroupRetentionRepro");
		builder.AppendLine($"Live Shell owners retained in both scenarios: {Current.RetainedShells}");
		builder.AppendLine($"FlyoutItems handles retained in both scenarios: {Current.RetainedFlyoutHandles}");
		builder.AppendLine($"Retired payload flyout groups per shell: {ReproSession.RetiredGroupsPerShell}");
		builder.AppendLine("Payload per retired flyout content: 1.0 MiB");
		builder.AppendLine();
		AppendScenario(builder, "control: remove stale public flyout groups after shrinking to one active group", Control, RetiredContentCount);
		builder.AppendLine();
		AppendScenario(builder, "current: ShellFlyoutItemsManager forward-removes extra groups while the list shrinks", Current, RetiredContentCount);
		builder.AppendLine();
		builder.AppendLine("Leak path: live Shell -> ShellFlyoutItemsManager._flyoutItemsReadonly -> stale flyout group -> removed ShellContent -> BindingContext/Payload buffer.");
		builder.AppendLine($"dotnet-version: {Environment.Version}");
		return builder.ToString();
	}

	static void AppendScenario(StringBuilder builder, string title, ScenarioResult result, int retiredContentCount)
	{
		builder.AppendLine($"Run: {title}");
		builder.AppendLine($"  retained live Shell owners: {result.RetainedShells}");
		builder.AppendLine($"  retained FlyoutItems handles: {result.RetainedFlyoutHandles}");
		builder.AppendLine($"  public flyout group count after shrink: {result.PublicFlyoutGroupCount}");
		builder.AppendLine($"  expected flyout group count after shrink: {result.ExpectedFlyoutGroupCount}");
		builder.AppendLine($"  retired ShellContent entries alive after full GC: {result.RetiredContentsAlive}/{retiredContentCount}");
		builder.AppendLine($"  retired payloads alive after full GC: {result.PayloadsAlive}/{retiredContentCount}");
		builder.AppendLine($"  retired payload buffers alive after full GC: {result.PayloadBuffersAlive}/{retiredContentCount}");
		builder.AppendLine($"  retained retired payload bytes: {FormatBytes(result.RetainedPayloadBytes)}");
		builder.AppendLine($"  managed heap delta: {FormatBytes(result.HeapDelta)}");
	}

	static string FormatBytes(long bytes)
	{
		var mib = bytes / 1024d / 1024d;
		return $"{mib:0.0} MiB";
	}
}
