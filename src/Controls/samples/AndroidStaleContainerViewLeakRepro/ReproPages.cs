using System.Threading;

namespace Maui.Controls.Sample.AndroidStaleContainerViewLeakRepro;

public class MonitorPage : ContentPage
{
	readonly Label _countsLabel;
	readonly Label _resultLabel;

	public MonitorPage()
	{
		Title = "Stale ContainerView Leak";

		var openButton = new Button
		{
			Text = "Open FlyoutPage",
			AutomationId = "OpenFlyoutPage"
		};

		openButton.Clicked += (_, _) =>
		{
			if (Window is not null)
				Window.Page = new LeakingFlyoutRootPage();
		};

		var collectButton = new Button
		{
			Text = "Force GC",
			AutomationId = "ForceGC"
		};

		collectButton.Clicked += (_, _) => UpdateCounts(LeakTracker.CollectAndSnapshot());

		_countsLabel = new Label
		{
			AutomationId = "Counts",
			FontFamily = "monospace",
			FontSize = 16
		};

		_resultLabel = new Label
		{
			AutomationId = "Result",
			FontAttributes = FontAttributes.Bold,
			FontSize = 18
		};

		Content = new ScrollView
		{
			Content = new VerticalStackLayout
			{
				Padding = new Thickness(24),
				Spacing = 14,
				Children =
				{
					new Label
					{
						Text = "Android stale ContainerView leak repro",
						FontSize = 24,
						FontAttributes = FontAttributes.Bold
					},
					new Label
					{
						Text = "Open the FlyoutPage, return to this page, then force GC. On an unfixed build the old FlyoutPage graph remains alive.",
						FontSize = 16
					},
					openButton,
					collectButton,
					_resultLabel,
					_countsLabel
				}
			}
		};

		UpdateCounts(LeakTracker.Snapshot());
	}

	void UpdateCounts(LeakSnapshot snapshot)
	{
		_countsLabel.Text =
			$"Root FlyoutPage:      {snapshot.RootFlyoutCount}\n" +
			$"Flyout ContentPage:   {snapshot.FlyoutPageCount}\n" +
			$"Detail NavigationPage:{snapshot.DetailNavigationPageCount}\n" +
			$"Detail ContentPage:   {snapshot.DetailContentPageCount}\n" +
			$"Weak refs alive:      {snapshot.AliveWeakReferences}/{snapshot.TotalWeakReferences}";

		_resultLabel.Text =
			snapshot.RootFlyoutCount + snapshot.FlyoutPageCount + snapshot.DetailNavigationPageCount + snapshot.DetailContentPageCount == 0
				? "No tracked repro pages are alive."
				: "Tracked repro pages are still alive.";
	}
}

public class LeakingFlyoutRootPage : FlyoutPage
{
	public LeakingFlyoutRootPage()
	{
		Interlocked.Increment(ref LeakTracker.RootFlyoutCount);
		LeakTracker.Track(this);

		Title = "Repro Flyout";
		Flyout = new LeakingFlyoutMenuPage();
		Detail = new LeakingDetailNavigationPage();
	}

	~LeakingFlyoutRootPage()
	{
		Interlocked.Decrement(ref LeakTracker.RootFlyoutCount);
	}
}

public class LeakingFlyoutMenuPage : ContentPage
{
	public LeakingFlyoutMenuPage()
	{
		Interlocked.Increment(ref LeakTracker.FlyoutPageCount);
		LeakTracker.Track(this);

		Title = "Flyout";
		Content = new VerticalStackLayout
		{
			Padding = new Thickness(24),
			Children =
			{
				new Label
				{
					Text = "Flyout page retained by the old root when the stale Android ContainerView is not cleared.",
					FontSize = 16
				}
			}
		};
	}

	~LeakingFlyoutMenuPage()
	{
		Interlocked.Decrement(ref LeakTracker.FlyoutPageCount);
	}
}

public class LeakingDetailNavigationPage : NavigationPage
{
	public LeakingDetailNavigationPage() : base(new LeakingDetailContentPage())
	{
		Interlocked.Increment(ref LeakTracker.DetailNavigationPageCount);
		LeakTracker.Track(this);

		Title = "Detail";
	}

	~LeakingDetailNavigationPage()
	{
		Interlocked.Decrement(ref LeakTracker.DetailNavigationPageCount);
	}
}

public class LeakingDetailContentPage : ContentPage
{
	public LeakingDetailContentPage()
	{
		Interlocked.Increment(ref LeakTracker.DetailContentPageCount);
		LeakTracker.Track(this);

		Title = "Detail Content";

		var returnButton = new Button
		{
			Text = "Return to monitor",
			AutomationId = "ReturnToMonitor"
		};

		returnButton.Clicked += (_, _) =>
		{
			if (Window is not null)
				Window.Page = new MonitorPage();
		};

		Content = new VerticalStackLayout
		{
			Padding = new Thickness(24),
			Spacing = 14,
			Children =
			{
				new Label
				{
					Text = "This page replaces Window.Page with a new monitor page.",
					FontSize = 16
				},
				returnButton
			}
		};
	}

	~LeakingDetailContentPage()
	{
		Interlocked.Decrement(ref LeakTracker.DetailContentPageCount);
	}
}
