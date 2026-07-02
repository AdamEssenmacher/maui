using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui.Controls;

namespace ShellMenuItemsFlyoutProjectionRetentionRepro;

public sealed class MainPage : ContentPage
{
	const int ShellCount = 40;
	const int MenuItemsPerShell = 3;
	const int PayloadBytes = 1024 * 1024;
	const int SurvivorTolerance = 2;
	const int TotalMenuItems = ShellCount * MenuItemsPerShell;

	static readonly FieldInfo FlyoutManagerField =
		typeof(Shell).GetField("_flyoutManager", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(Shell).FullName, "_flyoutManager");

	static readonly FieldInfo LastGeneratedFlyoutItemsField =
		FlyoutManagerField.FieldType.GetField("_lastGeneratedFlyoutItems", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(FlyoutManagerField.FieldType.FullName, "_lastGeneratedFlyoutItems");

	static readonly MethodInfo CheckIfFlyoutItemsChangedMethod =
		FlyoutManagerField.FieldType.GetMethod("CheckIfFlyoutItemsChanged", BindingFlags.Instance | BindingFlags.Public)
		?? throw new MissingMethodException(FlyoutManagerField.FieldType.FullName, "CheckIfFlyoutItemsChanged");

	readonly string? _resultsPath;
	readonly Label _status;
	bool _started;

	public MainPage(string? resultsPath)
	{
		_resultsPath = resultsPath;
		Title = "Shell MenuItems Flyout Projection Retention";

		_status = new Label
		{
			Text = "Running Shell MenuItems flyout projection retention repro...",
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
				? "PROVEN: generated Shell flyout groups retained removed MenuItems."
				: "NOT PROVEN: removed MenuItems did not remain alive.";

			if (!string.IsNullOrWhiteSpace(_resultsPath))
				File.WriteAllText(_resultsPath, report);

			await Task.Delay(250);
			Environment.Exit(result.Proven ? 0 : 2);
		}
		catch (Exception ex)
		{
			var report = "Shell MenuItems flyout projection retention repro failed." + Environment.NewLine + ex;
			_status.Text = report;

			if (!string.IsNullOrWhiteSpace(_resultsPath))
				File.WriteAllText(_resultsPath, report);

			await Task.Delay(250);
			Environment.Exit(3);
		}
	}

	static async Task<ReproResult> RunReproAsync()
	{
		var detached = await RunDetachedShellContentScenarioAsync();
		var control = await RunScenarioAsync(clearGeneratedFlyoutGroups: true);
		var current = await RunScenarioAsync(clearGeneratedFlyoutGroups: false);

		var detachedCollected = detached.MenuItemSurvivors <= SurvivorTolerance
			&& detached.PayloadSurvivors <= SurvivorTolerance
			&& detached.PayloadBufferSurvivors <= SurvivorTolerance;

		var controlCollected = control.MenuItemSurvivors <= SurvivorTolerance
			&& control.PayloadSurvivors <= SurvivorTolerance
			&& control.PayloadBufferSurvivors <= SurvivorTolerance
			&& control.GeneratedMenuItemReferences == 0;

		var currentRetained = current.MenuItemSurvivors >= TotalMenuItems - SurvivorTolerance
			&& current.PayloadSurvivors >= TotalMenuItems - SurvivorTolerance
			&& current.PayloadBufferSurvivors >= TotalMenuItems - SurvivorTolerance
			&& current.GeneratedMenuItemReferences >= TotalMenuItems - SurvivorTolerance;

		return new ReproResult(detached, control, current, detachedCollected && controlCollected && currentRetained);
	}

	static async Task<ScenarioResult> RunDetachedShellContentScenarioAsync()
	{
		var retainedContents = new List<ShellContent>(ShellCount);
		var menuItemRefs = new List<WeakReference<MenuItem>>(TotalMenuItems);
		var payloadRefs = new List<WeakReference<Payload>>(TotalMenuItems);
		var payloadBufferRefs = new List<WeakReference<byte[]>>(TotalMenuItems);

		ForceFullGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		for (var contentIndex = 0; contentIndex < ShellCount; contentIndex++)
		{
			CreateLiveShellContentAndRemoveMenuItems(
				contentIndex,
				retainedContents,
				menuItemRefs,
				payloadRefs,
				payloadBufferRefs);

			if (contentIndex % 8 == 0)
				await Task.Yield();
		}

		await WaitForCollectionAsync();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			"Baseline: detached ShellContent after one-by-one MenuItems removal",
			false,
			retainedContents.Count,
			0,
			CountAlive(menuItemRefs),
			CountAlive(payloadRefs),
			CountAlive(payloadBufferRefs),
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedContents);
		return result;
	}

	static async Task<ScenarioResult> RunScenarioAsync(bool clearGeneratedFlyoutGroups)
	{
		var retainedShells = new List<Shell>(ShellCount);
		var menuItemRefs = new List<WeakReference<MenuItem>>(TotalMenuItems);
		var payloadRefs = new List<WeakReference<Payload>>(TotalMenuItems);
		var payloadBufferRefs = new List<WeakReference<byte[]>>(TotalMenuItems);

		ForceFullGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		for (var shellIndex = 0; shellIndex < ShellCount; shellIndex++)
		{
			CreateLiveShellAndRemoveMenuItems(
				shellIndex,
				clearGeneratedFlyoutGroups,
				retainedShells,
				menuItemRefs,
				payloadRefs,
				payloadBufferRefs);

			if (shellIndex % 8 == 0)
				await Task.Yield();
		}

		await WaitForCollectionAsync();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);
		var generatedMenuItemReferences = CountGeneratedMenuItems(retainedShells);

		var result = new ScenarioResult(
			clearGeneratedFlyoutGroups ? "Control: clear generated flyout groups after removal" : "Current MAUI behavior",
			clearGeneratedFlyoutGroups,
			retainedShells.Count,
			generatedMenuItemReferences,
			CountAlive(menuItemRefs),
			CountAlive(payloadRefs),
			CountAlive(payloadBufferRefs),
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedShells);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateLiveShellContentAndRemoveMenuItems(
		int contentIndex,
		List<ShellContent> retainedContents,
		List<WeakReference<MenuItem>> menuItemRefs,
		List<WeakReference<Payload>> payloadRefs,
		List<WeakReference<byte[]>> payloadBufferRefs)
	{
		var shellContent = new ShellContent
		{
			Title = $"Detached content {contentIndex}",
			Content = new ContentPage
			{
				Title = $"Detached page {contentIndex}",
				Content = new Label { Text = $"Detached page {contentIndex}" }
			}
		};

		for (var menuIndex = 0; menuIndex < MenuItemsPerShell; menuIndex++)
		{
			var payload = new Payload(contentIndex * MenuItemsPerShell + menuIndex, PayloadBytes);
			var menuItem = new MenuItem
			{
				Text = $"Detached {contentIndex} action {menuIndex}",
				BindingContext = payload
			};

			shellContent.MenuItems.Add(menuItem);
			menuItemRefs.Add(new WeakReference<MenuItem>(menuItem));
			payloadRefs.Add(new WeakReference<Payload>(payload));
			payloadBufferRefs.Add(new WeakReference<byte[]>(payload.Buffer));
		}

		for (var menuIndex = shellContent.MenuItems.Count - 1; menuIndex >= 0; menuIndex--)
		{
			var removed = shellContent.MenuItems[menuIndex];
			shellContent.MenuItems.RemoveAt(menuIndex);

			if (removed.Parent is not null)
				throw new InvalidOperationException("Detached removed MenuItem still had a logical parent after RemoveAt.");
		}

		retainedContents.Add(shellContent);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateLiveShellAndRemoveMenuItems(
		int shellIndex,
		bool clearGeneratedFlyoutGroups,
		List<Shell> retainedShells,
		List<WeakReference<MenuItem>> menuItemRefs,
		List<WeakReference<Payload>> payloadRefs,
		List<WeakReference<byte[]>> payloadBufferRefs)
	{
		var shell = new Shell
		{
			Title = $"Tenant shell {shellIndex}",
			FlyoutBehavior = FlyoutBehavior.Flyout
		};

		var shellContent = new ShellContent
		{
			Title = $"Orders {shellIndex}",
			Content = new ContentPage
			{
				Title = $"Orders {shellIndex}",
				Content = new Label { Text = $"Orders shell {shellIndex}" }
			}
		};

		for (var menuIndex = 0; menuIndex < MenuItemsPerShell; menuIndex++)
		{
			var payload = new Payload(shellIndex * MenuItemsPerShell + menuIndex, PayloadBytes);
			var menuItem = new MenuItem
			{
				Text = $"Tenant {shellIndex} action {menuIndex}",
				BindingContext = payload
			};

			shellContent.MenuItems.Add(menuItem);
			menuItemRefs.Add(new WeakReference<MenuItem>(menuItem));
			payloadRefs.Add(new WeakReference<Payload>(payload));
			payloadBufferRefs.Add(new WeakReference<byte[]>(payload.Buffer));
		}

		var shellSection = new ShellSection
		{
			Title = $"Orders section {shellIndex}",
			FlyoutDisplayOptions = FlyoutDisplayOptions.AsSingleItem
		};
		shellSection.Items.Add(shellContent);
		shellSection.CurrentItem = shellContent;

		var shellItem = new ShellItem
		{
			Title = $"Tenant {shellIndex}",
			FlyoutDisplayOptions = FlyoutDisplayOptions.AsMultipleItems
		};
		shellItem.Items.Add(shellSection);
		shellItem.CurrentItem = shellSection;

		shell.Items.Add(shellItem);
		shell.CurrentItem = shellItem;

		var generatedBeforeRemoval = CountGeneratedMenuItems(shell);
		if (generatedBeforeRemoval != MenuItemsPerShell)
			throw new InvalidOperationException($"Expected {MenuItemsPerShell} generated menu items before removal, found {generatedBeforeRemoval}.");

		for (var menuIndex = shellContent.MenuItems.Count - 1; menuIndex >= 0; menuIndex--)
		{
			var removed = shellContent.MenuItems[menuIndex];
			shellContent.MenuItems.RemoveAt(menuIndex);

			if (removed.Parent is not null)
				throw new InvalidOperationException("Removed MenuItem still had a logical parent after RemoveAt.");
		}

		if (clearGeneratedFlyoutGroups)
			ClearAndRegenerateFlyoutGroups(shell);

		retainedShells.Add(shell);
	}

	static void ClearAndRegenerateFlyoutGroups(Shell shell)
	{
		var manager = FlyoutManagerField.GetValue(shell)
			?? throw new InvalidOperationException("Shell flyout manager was null.");

		LastGeneratedFlyoutItemsField.SetValue(manager, null);
		CheckIfFlyoutItemsChangedMethod.Invoke(manager, null);
	}

	static int CountGeneratedMenuItems(IEnumerable<Shell> shells)
	{
		var count = 0;
		foreach (var shell in shells)
			count += CountGeneratedMenuItems(shell);

		return count;
	}

	static int CountGeneratedMenuItems(Shell shell)
	{
		var groups = ((IShellController)shell).GenerateFlyoutGrouping();
		var count = 0;
		foreach (var group in groups)
		{
			foreach (var element in group)
			{
				if (element is MenuItem)
					count++;
			}
		}

		return count;
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
		bool ClearGeneratedFlyoutGroups,
		int RetainedOwners,
		int GeneratedMenuItemReferences,
		int MenuItemSurvivors,
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
			builder.AppendLine($"  Retained live owners: {RetainedOwners}");
			builder.AppendLine($"  Menu items removed per owner: {MenuItemsPerShell}");
			builder.AppendLine($"  Clear generated flyout groups: {ClearGeneratedFlyoutGroups}");
			builder.AppendLine($"  Generated flyout MenuItem references: {GeneratedMenuItemReferences}/{TotalMenuItems}");
			builder.AppendLine($"  MenuItem survivors: {MenuItemSurvivors}/{TotalMenuItems}");
			builder.AppendLine($"  Payload survivors: {PayloadSurvivors}/{TotalMenuItems}");
			builder.AppendLine($"  Payload buffer survivors: {PayloadBufferSurvivors}/{TotalMenuItems}");
			builder.AppendLine($"  Retained payload estimate: {RetainedPayloadMiB:F1} MiB");
			builder.AppendLine($"  Managed heap before: {HeapBeforeBytes:N0} bytes");
			builder.AppendLine($"  Managed heap after: {HeapAfterBytes:N0} bytes");
			builder.AppendLine($"  Managed heap delta: {HeapDeltaBytes:N0} bytes");
		}
	}

	readonly record struct ReproResult(ScenarioResult Detached, ScenarioResult Control, ScenarioResult Current, bool Proven)
	{
		public string ToReport()
		{
			var builder = new StringBuilder();
			builder.AppendLine("Shell MenuItems flyout projection retention repro");
			builder.AppendLine($"Result: {(Proven ? "PROVEN" : "NOT PROVEN")}");
			builder.AppendLine();
			Detached.AppendTo(builder);
			builder.AppendLine();
			Control.AppendTo(builder);
			builder.AppendLine();
			Current.AppendTo(builder);
			builder.AppendLine();
			builder.AppendLine("Expected proof signal:");
			builder.AppendLine($"- Detached ShellContent baseline survivors <= {SurvivorTolerance} after one-by-one removal.");
			builder.AppendLine($"- Shell control survivors <= {SurvivorTolerance} after clearing and regenerating Shell flyout groups.");
			builder.AppendLine($"- Current behavior survivors >= {TotalMenuItems - SurvivorTolerance} after public MenuItems.RemoveAt removes every menu item.");
			builder.AppendLine();
			builder.AppendLine("Retained graph under current behavior:");
			builder.AppendLine("Live Shell -> ShellFlyoutItemsManager generated flyout collections -> removed MenuItem -> BindingContext payload");
			builder.AppendLine();
			builder.AppendLine("Why this is distinct from nearby tracked leaks:");
			builder.AppendLine("- C139 covers MenuItems.Clear() reset skipping old-item logical-parent cleanup.");
			builder.AppendLine("- C303 covers app-retained MenuItems collection handles retaining discarded ShellContent owners.");
			builder.AppendLine("- C319 covers stale read-only flyout groups after generated group-count shrink.");
			builder.AppendLine("- This repro uses one-by-one RemoveAt so old MenuItems are deparented, then proves the live Shell flyout grouping cache still retains them.");
			return builder.ToString();
		}
	}
}
