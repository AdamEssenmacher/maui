namespace AdaptiveTriggerLeakRepro;

public sealed class AdaptiveTriggerLeakPage : ContentPage
{
	readonly ContentView _target;
	bool _readySignaled;

	public AdaptiveTriggerLeakPage()
	{
		var session = ReproSession.Current ?? throw new InvalidOperationException("No active repro session.");
		var options = session.Options;
		var cycle = session.CurrentCycle;
		var payload = new LeakPayloadViewModel(cycle, options.PayloadBytesPerPage);

		Title = payload.Title;
		BindingContext = payload;

		_target = new ContentView
		{
			BindingContext = payload,
			Padding = 18,
			MinimumHeightRequest = 420,
			Content = CreateTargetContent(payload)
		};

		VisualStateManager.SetVisualStateGroups(_target, CreateAdaptiveGroups("Contoso responsive theme", Color.FromArgb("#E8F6F8")));

		if (options.PreloadSwappedTheme)
			VisualStateManager.SetVisualStateGroups(_target, CreateAdaptiveGroups("Northwind preloaded theme", Color.FromArgb("#F0F5EB")));

		_target.Loaded += OnTargetLoaded;
		session.Track(this, _target, payload);

		Content = new ScrollView
		{
			Content = new VerticalStackLayout
			{
				Spacing = 14,
				Padding = new Thickness(16, 16, 16, 24),
				Children =
				{
					new Label
					{
						Text = $"{options.Name}: cycle {cycle + 1}, payload {options.PayloadMegabytesPerPage} MB",
						FontSize = 13,
						TextColor = Color.FromArgb("#57606A")
					},
					_target
				}
			}
		};
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		_ = CompletePageSetupAsync();
	}

	void OnTargetLoaded(object? sender, EventArgs e)
	{
		_target.Loaded -= OnTargetLoaded;
		_ = CompletePageSetupAsync();
	}

	async Task CompletePageSetupAsync()
	{
		if (_readySignaled)
			return;

		if (!await WaitForWindowAsync())
			return;

		await Task.Delay(25);

		var session = ReproSession.Current;
		if (session is null || _readySignaled)
			return;

		if (session.Options.SwapThemeAfterAttach)
			VisualStateManager.SetVisualStateGroups(_target, CreateAdaptiveGroups("Northwind live theme", Color.FromArgb("#F8F0E8")));

		_readySignaled = true;
		session.MarkCurrentPageReady();
	}

	async Task<bool> WaitForWindowAsync()
	{
		for (var i = 0; i < 100 && _target.Window is null; i++)
			await Task.Delay(10);

		return _target.Window is not null;
	}

	static View CreateTargetContent(LeakPayloadViewModel payload)
	{
		var reports = new VerticalStackLayout
		{
			Spacing = 6
		};

		foreach (var report in payload.CachedReports.Take(6))
		{
			reports.Add(new Label
			{
				Text = $"{report.Id}  {report.Summary}",
				FontSize = 12,
				TextColor = Color.FromArgb("#243B4A")
			});
		}

		return new VerticalStackLayout
		{
			Spacing = 12,
			Children =
			{
				new Label
				{
					Text = payload.Title,
					FontSize = 24,
					FontAttributes = FontAttributes.Bold,
					TextColor = Color.FromArgb("#0B1F33")
				},
				new Label
				{
					Text = "Tenant dashboard content with cached report payload. The target ContentView owns this view model through BindingContext.",
					FontSize = 14,
					TextColor = Color.FromArgb("#57606A")
				},
				CreateMetricGrid(payload),
				reports
			}
		};
	}

	static Grid CreateMetricGrid(LeakPayloadViewModel payload)
	{
		var grid = new Grid
		{
			ColumnDefinitions =
			{
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(GridLength.Star)
			},
			ColumnSpacing = 10
		};

		grid.Add(CreateMetric("Cached payload", FormatBytes(payload.PayloadBytes)), 0, 0);
		grid.Add(CreateMetric("Reports", payload.CachedReports.Count.ToString()), 1, 0);

		return grid;
	}

	static Border CreateMetric(string title, string value)
	{
		return new Border
		{
			StrokeThickness = 1,
			Stroke = new SolidColorBrush(Color.FromArgb("#B7C7D1")),
			BackgroundColor = Colors.White,
			Padding = 12,
			Content = new VerticalStackLayout
			{
				Spacing = 3,
				Children =
				{
					new Label
					{
						Text = title,
						FontSize = 11,
						TextColor = Color.FromArgb("#57606A")
					},
					new Label
					{
						Text = value,
						FontSize = 17,
						FontAttributes = FontAttributes.Bold,
						TextColor = Color.FromArgb("#0B1F33")
					}
				}
			}
		};
	}

	static VisualStateGroupList CreateAdaptiveGroups(string label, Color activeColor)
	{
		var groups = new VisualStateGroupList();
		var group = new VisualStateGroup { Name = "ResponsiveStates" };

		group.States.Add(new VisualState
		{
			Name = "Normal",
			Setters =
			{
				new Setter
				{
					Property = VisualElement.BackgroundColorProperty,
					Value = Color.FromArgb("#F6F8FA")
				}
			}
		});

		group.States.Add(new VisualState
		{
			Name = label,
			StateTriggers =
			{
				new AdaptiveTrigger
				{
					MinWindowWidth = 1
				}
			},
			Setters =
			{
				new Setter
				{
					Property = VisualElement.BackgroundColorProperty,
					Value = activeColor
				}
			}
		});

		groups.Add(group);
		return groups;
	}

	static string FormatBytes(long bytes)
	{
		if (bytes >= 1024L * 1024L)
			return $"{bytes / 1024d / 1024d:0.0} MB";

		if (bytes >= 1024L)
			return $"{bytes / 1024d:0.0} KB";

		return $"{bytes} B";
	}
}
