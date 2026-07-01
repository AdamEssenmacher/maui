using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Controls;

namespace ShellSectionPlatformEnabledRemovalRetentionRepro;

public sealed class App : Application
{
	const int OwnerCount = 80;
	const int PayloadBytes = 1024 * 1024;
	const string ResultPath = "/tmp/shellsection-platformenabled-removal-retention-results.txt";

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var page = new ContentPage
		{
			Content = new Label
			{
				Text = "Running ShellSection platform-enabled removal retention repro",
				HorizontalOptions = LayoutOptions.Center,
				VerticalOptions = LayoutOptions.Center
			}
		};

		page.Dispatcher.Dispatch(async () =>
		{
			await Task.Delay(250).ConfigureAwait(false);
			var report = Run();
			File.WriteAllText(ResultPath, report);
			Environment.Exit(0);
		});

		return new Window(page);
	}

	static string Run()
	{
		var control = CreateScenario("explicit platform disable cleanup", completeDelayedRemoval: true);
		var current = CreateScenario("current MAUI delayed removal", completeDelayedRemoval: false);

		ForceFullCollection();

		var controlSummary = control.Summarize();
		var currentSummary = current.Summarize();

		var proved = controlSummary.RetainedPayloadBytes == 0 &&
			controlSummary.SectionsAlive == 0 &&
			controlSummary.SiblingPayloadsAlive == 0 &&
			controlSummary.PlatformEnabledCallbacks == 0 &&
			currentSummary.PlatformEnabledCallbacks == OwnerCount &&
			currentSummary.SectionsAlive == OwnerCount &&
			currentSummary.RemovedPagesAlive == OwnerCount &&
			currentSummary.SiblingPagesAlive == OwnerCount &&
			currentSummary.SiblingPayloadsAlive == OwnerCount &&
			currentSummary.RetainedPayloadBytes == (long)OwnerCount * PayloadBytes;

		return string.Join(Environment.NewLine,
			$"RESULT: {(proved ? "PROVEN" : "NOT PROVEN")}",
			$"Owners retained in both scenarios: {OwnerCount} removed ShellContent handles whose pages were platform-enabled at removal time",
			$"Payload per sibling page: {PayloadBytes:N0} bytes",
			string.Empty,
			controlSummary.ToReportBlock(),
			string.Empty,
			currentSummary.ToReportBlock(),
			string.Empty,
			"Interpretation:",
			"Current MAUI keeps a PlatformEnabledChanged callback on each removed ShellContent page after ShellSection.Items removal.",
			"The callback captures the removed ShellContent and ShellSection until the removed page later becomes platform-disabled.",
			"Retaining only those removed ShellContent handles therefore retains the old ShellSection graph and unrelated sibling page payloads.",
			$"Result file: {ResultPath}");
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static Scenario CreateScenario(string name, bool completeDelayedRemoval)
	{
		var scenario = new Scenario(name);

		for (var i = 0; i < OwnerCount; i++)
		{
			var section = new Tab();

			var removedPage = new ContentPage
			{
				Title = $"Removed page {i}",
				Content = new Label { Text = "Removed page retained by external app/native code" }
			};
			var removedContent = new ShellContent
			{
				Title = $"Removed {i}",
				Content = removedPage
			};

			var payload = new Payload(i, PayloadBytes);
			var siblingPage = new ContentPage
			{
				Title = $"Sibling page {i}",
				BindingContext = payload,
				Content = new Label { Text = $"Sibling payload {i}" }
			};
			var siblingContent = new ShellContent
			{
				Title = $"Sibling {i}",
				Content = siblingPage
			};

			section.Items.Add(removedContent);
			section.Items.Add(siblingContent);
			removedPage.IsPlatformEnabled = true;
			section.Items.Remove(removedContent);

			if (completeDelayedRemoval)
				removedPage.IsPlatformEnabled = false;

			scenario.RetainedRemovedShellContents.Add(removedContent);
			scenario.Sections.Add(new WeakReference<ShellSection>(section));
			scenario.RemovedPages.Add(new WeakReference<Page>(removedPage));
			scenario.SiblingPages.Add(new WeakReference<Page>(siblingPage));
			scenario.SiblingPayloads.Add(new WeakReference<Payload>(payload));
			scenario.SiblingPayloadBuffers.Add(new WeakReference<byte[]>(payload.Buffer));
		}

		return scenario;
	}

	static void ForceFullCollection()
	{
		for (var i = 0; i < 8; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(50);
		}
	}

	sealed class Payload
	{
		public Payload(int index, int size)
		{
			Buffer = new byte[size];
			for (var i = 0; i < Buffer.Length; i += 4096)
				Buffer[i] = (byte)(index + i);
		}

		public byte[] Buffer { get; }
	}

	sealed class Scenario(string name)
	{
		static readonly FieldInfo? PlatformEnabledChangedField =
			typeof(VisualElement).GetField("PlatformEnabledChanged", BindingFlags.Instance | BindingFlags.NonPublic);

		public string Name { get; } = name;
		public List<ShellContent> RetainedRemovedShellContents { get; } = [];
		public List<WeakReference<ShellSection>> Sections { get; } = [];
		public List<WeakReference<Page>> RemovedPages { get; } = [];
		public List<WeakReference<Page>> SiblingPages { get; } = [];
		public List<WeakReference<Payload>> SiblingPayloads { get; } = [];
		public List<WeakReference<byte[]>> SiblingPayloadBuffers { get; } = [];

		public ScenarioSummary Summarize()
		{
			var callbacks = 0;
			foreach (var shellContent in RetainedRemovedShellContents)
			{
				if (shellContent.Content is not Page page)
					continue;

				if (PlatformEnabledChangedField?.GetValue(page) is MulticastDelegate del)
					callbacks += del.GetInvocationList().Length;
			}

			var buffersAlive = CountAlive(SiblingPayloadBuffers);

			return new ScenarioSummary(
				Name,
				callbacks,
				CountAlive(Sections),
				CountAlive(RemovedPages),
				CountAlive(SiblingPages),
				CountAlive(SiblingPayloads),
				buffersAlive,
				(long)buffersAlive * PayloadBytes);
		}

		static int CountAlive<T>(IEnumerable<WeakReference<T>> refs)
			where T : class
		{
			var count = 0;
			foreach (var weak in refs)
			{
				if (weak.TryGetTarget(out _))
					count++;
			}

			return count;
		}
	}

	readonly record struct ScenarioSummary(
		string Name,
		int PlatformEnabledCallbacks,
		int SectionsAlive,
		int RemovedPagesAlive,
		int SiblingPagesAlive,
		int SiblingPayloadsAlive,
		int SiblingPayloadBuffersAlive,
		long RetainedPayloadBytes)
	{
		public string ToReportBlock() =>
			string.Join(Environment.NewLine,
				$"{Name}:",
				$"  PlatformEnabledChanged callbacks: {PlatformEnabledCallbacks}/{OwnerCount}",
				$"  ShellSections alive: {SectionsAlive}/{OwnerCount}",
				$"  removed pages alive through retained ShellContent: {RemovedPagesAlive}/{OwnerCount}",
				$"  sibling pages alive: {SiblingPagesAlive}/{OwnerCount}",
				$"  sibling payloads alive: {SiblingPayloadsAlive}/{OwnerCount}",
				$"  sibling payload buffers alive: {SiblingPayloadBuffersAlive}/{OwnerCount}",
				$"  retained sibling payload bytes: {RetainedPayloadBytes:N0}");
	}
}
