using System.Diagnostics;
using Microsoft.Maui.Controls.Shapes;
#if IOS || MACCATALYST
using CoreAnimation;
using Foundation;
#endif
using Maui.Controls.Sample;

namespace Maui.Controls.Sample.Issues;

[Issue(IssueTracker.None, 0, "CollectionView MeasureFirstItem scroll perf proof", PlatformAffected.iOS | PlatformAffected.macOS)]
public class CollectionViewMeasureFirstItemScrollPerfProof : ContentPage
{
	const int ItemCount = 500;
	const int ScrollSettleMilliseconds = 70;
	const string CurrentCv2Label = "Current Items2 CV2";
	const string FixedCv2Label = "Fixed Items2 CV2";

	readonly ContentView _collectionHost;
	readonly Label _statusLabel;
	readonly Label _currentCv2ResultLabel;
	readonly Label _fixedCv2ResultLabel;
	readonly List<Button> _buttons = new();
	readonly ScrollPerfProofVariant _variant;
	int[] _gcBaseline = new int[3];
	bool _autoRunStarted;
	bool _running;

	static readonly int[] ScrollIndexes =
	{
		0, 40, 80, 120, 160, 200, 240, 280, 320, 360, 400, 440, 480,
		440, 400, 360, 320, 280, 240, 200, 160, 120, 80, 40, 0
	};

	public CollectionViewMeasureFirstItemScrollPerfProof()
	{
		Title = "MeasureFirstItem scroll perf proof";
		BackgroundColor = Colors.White;
		_variant = GetVariant();

		_statusLabel = new Label
		{
			AutomationId = "MeasureFirstItemPerfStatus",
			FontSize = 12,
			TextColor = Colors.Black,
			Margin = new Thickness(12, 2, 12, 4)
		};

		_currentCv2ResultLabel = CreateResultLabel("MeasureFirstItemPerfCurrentCv2Result", CurrentCv2Label);
		_fixedCv2ResultLabel = CreateResultLabel("MeasureFirstItemPerfFixedCv2Result", FixedCv2Label);
		_collectionHost = new ContentView
		{
			AutomationId = "MeasureFirstItemPerfCollectionHost"
		};

		var root = new Grid
		{
			RowDefinitions =
			{
				new RowDefinition { Height = GridLength.Auto },
				new RowDefinition { Height = GridLength.Auto },
				new RowDefinition { Height = GridLength.Auto },
				new RowDefinition { Height = GridLength.Star }
			}
		};

		root.Add(CreateHeader(), 0, 0);
		root.Add(CreateButtonGrid(), 0, 1);
		root.Add(CreateResultsView(), 0, 2);
		root.Add(_collectionHost, 0, 3);

		Content = root;
		ResetProof();
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();

		if (_autoRunStarted)
		{
			return;
		}

		_autoRunStarted = true;
		var autoRun = Environment.GetEnvironmentVariable("MEASURE_FIRST_ITEM_PERF_AUTORUN");
		if (string.IsNullOrWhiteSpace(autoRun))
		{
			return;
		}

		Dispatcher.Dispatch(async () =>
		{
			await Task.Delay(350);

			if (string.Equals(autoRun, "Warmup", StringComparison.OrdinalIgnoreCase))
			{
				await WarmupAsync();
			}
			else if (string.Equals(autoRun, "CV2", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(autoRun, "Both", StringComparison.OrdinalIgnoreCase))
			{
				await RunCv2Async();
			}
		});
	}

	View CreateHeader()
	{
		return new VerticalStackLayout
		{
			Margin = new Thickness(12, 12, 12, 0),
			Spacing = 3,
			Children =
			{
				new Label
				{
					Text = $"Deterministic CV2 scroll proof. Realistic inbox template with no synthetic measure delay. Active result row: {GetActiveCv2Label()}.",
					AutomationId = "MeasureFirstItemPerfLegend",
					FontAttributes = FontAttributes.Bold,
					FontSize = 13,
					TextColor = Colors.Black
				},
				_statusLabel
			}
		};
	}

	Grid CreateButtonGrid()
	{
		var buttonGrid = new Grid
		{
			Margin = new Thickness(12, 2, 12, 8),
			ColumnSpacing = 6,
			ColumnDefinitions =
			{
				new ColumnDefinition { Width = GridLength.Star },
				new ColumnDefinition { Width = GridLength.Star },
				new ColumnDefinition { Width = GridLength.Star }
			}
		};

		AddButton(buttonGrid, "Reset", "MeasureFirstItemPerfReset", 0, ResetProof);
		AddButton(buttonGrid, "Warmup", "MeasureFirstItemPerfWarmup", 1, () => _ = WarmupAsync());
		AddButton(buttonGrid, "Run CV2", "MeasureFirstItemPerfRunCv2", 2, () => _ = RunCv2Async());

		return buttonGrid;
	}

	void AddButton(Grid grid, string text, string automationId, int column, Action action)
	{
		var button = new Button
		{
			Text = text,
			AutomationId = automationId,
			FontSize = 12,
			Padding = new Thickness(6, 4)
		};
		button.Clicked += (_, _) => action();
		_buttons.Add(button);
		grid.Add(button, column, 0);
	}

	View CreateResultsView()
	{
		return new VerticalStackLayout
		{
			Margin = new Thickness(12, 0, 12, 8),
			Spacing = 2,
			Children =
			{
				_currentCv2ResultLabel,
				_fixedCv2ResultLabel
			}
		};
	}

	static Label CreateResultLabel(string automationId, string label)
	{
		return new Label
		{
			AutomationId = automationId,
			FontSize = 11,
			LineBreakMode = LineBreakMode.WordWrap,
			TextColor = Colors.Black,
			Text = $"{label}: pending"
		};
	}

	void ResetProof()
	{
		if (_running)
		{
			return;
		}

		_collectionHost.Content = null;
		ScrollPerfProbeMetrics.Reset();
		ForceFullCollection();
		_gcBaseline = GetGcCounts();

		_currentCv2ResultLabel.Text = $"{CurrentCv2Label}: pending";
		_fixedCv2ResultLabel.Text = $"{FixedCv2Label}: pending";
		_statusLabel.Text = $"Ready. Items: {ItemCount}; scroll stops: {ScrollIndexes.Length}; GC baseline: {FormatGcCounts(_gcBaseline)}.";
	}

	async Task WarmupAsync()
	{
		if (!BeginOperation("Warmup running..."))
		{
			return;
		}

		try
		{
			await WarmupActiveTemplateAsync(GetActiveCv2Label());
			_statusLabel.Text = "Warmup complete. Results were not recorded.";
		}
		finally
		{
			EndOperation();
		}
	}

	async Task RunCv2Async()
	{
		if (!BeginOperation($"Running {GetActiveCv2Label()}..."))
		{
			return;
		}

		try
		{
			var label = GetActiveCv2Label();
			_statusLabel.Text = $"Warming {label} image and layout caches...";
			await WarmupActiveTemplateAsync(label);
			_statusLabel.Text = $"Running {label}...";

			var result = await RunScrollSequenceAsync(CreateCollectionView<CollectionView2>(label), label, record: true);
			GetActiveCv2ResultLabel().Text = result.ToDisplayText();
			_statusLabel.Text = $"Completed {label}.";
		}
		finally
		{
			EndOperation();
		}
	}

	async Task WarmupActiveTemplateAsync(string label)
	{
		await RunScrollSequenceAsync(CreateCollectionView<CollectionView2>(label), label, record: false);
	}

	async Task<ScrollPerfProofResult> RunScrollSequenceAsync(CollectionView collectionView, string label, bool record)
	{
		_collectionHost.Content = collectionView;
		await Task.Delay(180);
		collectionView.ScrollTo(0, position: ScrollToPosition.Start, animate: false);
		await Task.Delay(180);

		var frameSampler = ScrollPerfFrameSampler.Create(Dispatcher);
		var elapsed = Stopwatch.StartNew();

		if (record)
		{
			ScrollPerfProbeMetrics.BeginRun(label);
			frameSampler.Start();
		}

		try
		{
			foreach (var index in ScrollIndexes)
			{
				collectionView.ScrollTo(index, position: ScrollToPosition.Start, animate: false);
				await Task.Delay(ScrollSettleMilliseconds);
			}

			await Task.Delay(180);
		}
		finally
		{
			if (record)
			{
				frameSampler.Stop();
			}
		}

		elapsed.Stop();

		if (!record)
		{
			return ScrollPerfProofResult.Empty(label);
		}

		var snapshot = ScrollPerfProbeMetrics.EndRun(label);
		var frameStats = frameSampler.GetStats();
		var gcCounts = GetGcCounts();
		return new ScrollPerfProofResult(
			label,
			snapshot.TotalMeasureCalls,
			snapshot.CachedHeightNonFirstMeasureCalls,
			snapshot.UncachedNonFirstMeasureCalls,
			snapshot.TotalMeasureTime,
			frameStats,
			elapsed.Elapsed,
			gcCounts[0] - _gcBaseline[0],
			gcCounts[1] - _gcBaseline[1],
			gcCounts[2] - _gcBaseline[2]);
	}

	static TCollectionView CreateCollectionView<TCollectionView>(string label)
		where TCollectionView : CollectionView, new()
	{
		return new TCollectionView
		{
			AutomationId = $"{SanitizeAutomationId(label)}CollectionView",
			BackgroundColor = Colors.White,
			ItemSizingStrategy = ItemSizingStrategy.MeasureFirstItem,
			ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Vertical)
			{
				ItemSpacing = 4
			},
			ItemTemplate = new DataTemplate(() => new ScrollPerfProbeCell()),
			ItemsSource = CreateItems(label)
		};
	}

	static List<ScrollPerfProbeItem> CreateItems(string label)
	{
		var items = new List<ScrollPerfProbeItem>();
		for (int n = 0; n < ItemCount; n++)
		{
			items.Add(new ScrollPerfProbeItem(label, n));
		}

		return items;
	}

	bool BeginOperation(string status)
	{
		if (_running)
		{
			return false;
		}

		_running = true;
		SetButtonsEnabled(false);
		_statusLabel.Text = status;
		return true;
	}

	void EndOperation()
	{
		_running = false;
		SetButtonsEnabled(true);
	}

	void SetButtonsEnabled(bool enabled)
	{
		foreach (var button in _buttons)
		{
			button.IsEnabled = enabled;
		}
	}

	string GetActiveCv2Label()
	{
		return _variant == ScrollPerfProofVariant.Fixed
			? FixedCv2Label
			: CurrentCv2Label;
	}

	Label GetActiveCv2ResultLabel()
	{
		return _variant == ScrollPerfProofVariant.Fixed
			? _fixedCv2ResultLabel
			: _currentCv2ResultLabel;
	}

	static ScrollPerfProofVariant GetVariant()
	{
		var variant = Environment.GetEnvironmentVariable("MEASURE_FIRST_ITEM_PERF_VARIANT");
		return string.Equals(variant, "Fixed", StringComparison.OrdinalIgnoreCase)
			? ScrollPerfProofVariant.Fixed
			: ScrollPerfProofVariant.Current;
	}

	static string SanitizeAutomationId(string value)
	{
		return value.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("2", "Two", StringComparison.Ordinal);
	}

	static void ForceFullCollection()
	{
		GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
		GC.WaitForPendingFinalizers();
		GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
	}

	static int[] GetGcCounts()
	{
		return new[]
		{
			GC.CollectionCount(0),
			GC.CollectionCount(1),
			GC.CollectionCount(2)
		};
	}

	static string FormatGcCounts(int[] counts)
	{
		return $"{counts[0]}/{counts[1]}/{counts[2]}";
	}

	enum ScrollPerfProofVariant
	{
		Current,
		Fixed
	}

	sealed class ScrollPerfProbeItem
	{
		static readonly string[] Senders =
		{
			"Design Systems",
			"Jordan Lee",
			"Build Notifications",
			"Operations Desk",
			"Alex Morgan",
			"Customer Success",
			"Finance Review",
			"Release Coordination"
		};

		static readonly string[] Subjects =
		{
			"Updated review notes for the mobile dashboard",
			"Follow-up on the onboarding checklist",
			"Nightly validation finished with warnings",
			"Schedule change for the launch readiness review",
			"New comments on the accessibility audit",
			"Action needed: partner rollout summary",
			"Invoice package ready for approval",
			"Status report for the service migration"
		};

		static readonly string[] Previews =
		{
			"The latest build includes the revised list layout, profile treatment, and compact metadata rows requested in yesterday's review.",
			"Please take another pass through the remaining tasks before we send the package to the implementation team this afternoon.",
			"The workflow completed successfully, but three scenarios still need attention before the release branch is considered stable.",
			"We moved the checkpoint by thirty minutes so the iOS and Catalyst results can be reviewed together with the trace summary.",
			"The audit now includes screenshots, contrast notes, and the remaining issues found in the account settings entry points.",
			"The customer-facing summary is ready, including open risks, recommended next steps, and owners for each follow-up item.",
			"The export has been generated with receipts, tax details, and reconciliation notes for the current reporting period.",
			"The migration is still on track; the latest dry run reduced manual cleanup and confirmed the new rollback checklist."
		};

		static readonly string[] Timestamps =
		{
			"Now",
			"8:42 AM",
			"9:15 AM",
			"10:30 AM",
			"11:05 AM",
			"Yesterday",
			"Mon",
			"Jun 7"
		};

		static readonly string[] Categories =
		{
			"Review",
			"Work",
			"Build",
			"Ops",
			"Design",
			"Partner",
			"Finance",
			"Release"
		};

		public ScrollPerfProbeItem(string label, int index)
		{
			var unread = index % 3 == 0;

			Label = label;
			Index = index;
			AvatarSource = "avatar.png";
			Sender = Senders[index % Senders.Length];
			Subject = Subjects[index % Subjects.Length];
			Preview = Previews[index % Previews.Length];
			Timestamp = Timestamps[index % Timestamps.Length];
			CategoryText = Categories[index % Categories.Length];
			StatusText = unread ? "Unread" : index % 5 == 0 ? "Flagged" : "Open";
			SenderFontAttributes = unread ? FontAttributes.Bold : FontAttributes.None;
			SubjectFontAttributes = unread ? FontAttributes.Bold : FontAttributes.None;
			SenderColor = unread ? Colors.Black : Color.FromArgb("#374151");
			SubjectColor = unread ? Color.FromArgb("#111827") : Color.FromArgb("#1F2937");
			PreviewColor = unread ? Color.FromArgb("#374151") : Color.FromArgb("#6B7280");
			StatusBackgroundColor = unread ? Color.FromArgb("#DBEAFE") : index % 5 == 0 ? Color.FromArgb("#FEF3C7") : Color.FromArgb("#E5E7EB");
			StatusTextColor = unread ? Color.FromArgb("#1D4ED8") : index % 5 == 0 ? Color.FromArgb("#92400E") : Color.FromArgb("#374151");
			CategoryBackgroundColor = Color.FromArgb("#F3F4F6");
			CategoryTextColor = Color.FromArgb("#4B5563");
		}

		public string Label { get; }

		public int Index { get; }

		public string AvatarSource { get; }

		public string Sender { get; }

		public string Subject { get; }

		public string Preview { get; }

		public string Timestamp { get; }

		public string StatusText { get; }

		public string CategoryText { get; }

		public FontAttributes SenderFontAttributes { get; }

		public FontAttributes SubjectFontAttributes { get; }

		public Color SenderColor { get; }

		public Color SubjectColor { get; }

		public Color PreviewColor { get; }

		public Color StatusBackgroundColor { get; }

		public Color StatusTextColor { get; }

		public Color CategoryBackgroundColor { get; }

		public Color CategoryTextColor { get; }
	}

	sealed class ScrollPerfProbeCell : Grid
	{
		ScrollPerfProbeItem _item;

		public ScrollPerfProbeCell()
		{
			HeightRequest = 116;
			Padding = new Thickness(12, 10);
			BackgroundColor = Color.FromArgb("#F8FAFC");
			ColumnSpacing = 10;
			RowSpacing = 3;

			ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
			ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
			ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });

			var avatarImage = new Image
			{
				Aspect = Aspect.AspectFill,
				WidthRequest = 48,
				HeightRequest = 48
			};
			avatarImage.SetBinding(Image.SourceProperty, nameof(ScrollPerfProbeItem.AvatarSource));

			var avatarBorder = new Border
			{
				WidthRequest = 48,
				HeightRequest = 48,
				StrokeThickness = 0,
				StrokeShape = new RoundRectangle { CornerRadius = 24 },
				BackgroundColor = Color.FromArgb("#E0E7FF"),
				HorizontalOptions = LayoutOptions.Start,
				VerticalOptions = LayoutOptions.Start,
				Content = avatarImage
			};

			var senderLabel = new Label
			{
				FontSize = 14,
				MaxLines = 1,
				LineBreakMode = LineBreakMode.TailTruncation,
				VerticalOptions = LayoutOptions.Center
			};
			senderLabel.SetBinding(Label.TextProperty, nameof(ScrollPerfProbeItem.Sender));
			senderLabel.SetBinding(Label.TextColorProperty, nameof(ScrollPerfProbeItem.SenderColor));
			senderLabel.SetBinding(Label.FontAttributesProperty, nameof(ScrollPerfProbeItem.SenderFontAttributes));

			var timestampLabel = new Label
			{
				FontSize = 11,
				TextColor = Color.FromArgb("#6B7280"),
				HorizontalTextAlignment = TextAlignment.End,
				VerticalOptions = LayoutOptions.Center,
				MaxLines = 1
			};
			timestampLabel.SetBinding(Label.TextProperty, nameof(ScrollPerfProbeItem.Timestamp));

			var subjectLabel = new Label
			{
				FontSize = 13,
				MaxLines = 1,
				LineBreakMode = LineBreakMode.TailTruncation
			};
			subjectLabel.SetBinding(Label.TextProperty, nameof(ScrollPerfProbeItem.Subject));
			subjectLabel.SetBinding(Label.TextColorProperty, nameof(ScrollPerfProbeItem.SubjectColor));
			subjectLabel.SetBinding(Label.FontAttributesProperty, nameof(ScrollPerfProbeItem.SubjectFontAttributes));

			var previewLabel = new Label
			{
				FontSize = 12,
				MaxLines = 2,
				LineBreakMode = LineBreakMode.TailTruncation
			};
			previewLabel.SetBinding(Label.TextProperty, nameof(ScrollPerfProbeItem.Preview));
			previewLabel.SetBinding(Label.TextColorProperty, nameof(ScrollPerfProbeItem.PreviewColor));

			var chipRow = new HorizontalStackLayout
			{
				Spacing = 6,
				HeightRequest = 22,
				VerticalOptions = LayoutOptions.Center,
				Children =
				{
					CreateChip(nameof(ScrollPerfProbeItem.StatusText), nameof(ScrollPerfProbeItem.StatusBackgroundColor), nameof(ScrollPerfProbeItem.StatusTextColor)),
					CreateChip(nameof(ScrollPerfProbeItem.CategoryText), nameof(ScrollPerfProbeItem.CategoryBackgroundColor), nameof(ScrollPerfProbeItem.CategoryTextColor))
				}
			};

			var separator = new BoxView
			{
				HeightRequest = 1,
				BackgroundColor = Color.FromArgb("#E5E7EB"),
				VerticalOptions = LayoutOptions.End
			};

			Children.Add(avatarBorder);
			Grid.SetColumn(avatarBorder, 0);
			Grid.SetRow(avatarBorder, 0);
			Grid.SetRowSpan(avatarBorder, 4);
			Children.Add(senderLabel);
			Grid.SetColumn(senderLabel, 1);
			Grid.SetRow(senderLabel, 0);
			Children.Add(timestampLabel);
			Grid.SetColumn(timestampLabel, 2);
			Grid.SetRow(timestampLabel, 0);
			Children.Add(subjectLabel);
			Grid.SetColumn(subjectLabel, 1);
			Grid.SetRow(subjectLabel, 1);
			Grid.SetColumnSpan(subjectLabel, 2);
			Children.Add(previewLabel);
			Grid.SetColumn(previewLabel, 1);
			Grid.SetRow(previewLabel, 2);
			Grid.SetColumnSpan(previewLabel, 2);
			Children.Add(chipRow);
			Grid.SetColumn(chipRow, 1);
			Grid.SetRow(chipRow, 3);
			Grid.SetColumnSpan(chipRow, 2);
			Children.Add(separator);
			Grid.SetColumn(separator, 0);
			Grid.SetRow(separator, 4);
			Grid.SetColumnSpan(separator, 3);
		}

		static Border CreateChip(string textProperty, string backgroundColorProperty, string textColorProperty)
		{
			var label = new Label
			{
				FontSize = 10,
				MaxLines = 1,
				LineBreakMode = LineBreakMode.TailTruncation,
				VerticalOptions = LayoutOptions.Center
			};
			label.SetBinding(Label.TextProperty, textProperty);
			label.SetBinding(Label.TextColorProperty, textColorProperty);

			var chip = new Border
			{
				Padding = new Thickness(8, 2),
				StrokeThickness = 0,
				StrokeShape = new RoundRectangle { CornerRadius = 10 },
				HeightRequest = 20,
				VerticalOptions = LayoutOptions.Center,
				Content = label
			};
			chip.SetBinding(VisualElement.BackgroundColorProperty, backgroundColorProperty);

			return chip;
		}

		protected override void OnBindingContextChanged()
		{
			base.OnBindingContextChanged();
			_item = BindingContext as ScrollPerfProbeItem;
		}

		protected override Size MeasureOverride(double widthConstraint, double heightConstraint)
		{
			var stopwatch = Stopwatch.StartNew();
			var size = base.MeasureOverride(widthConstraint, heightConstraint);
			stopwatch.Stop();

			if (_item is not null)
			{
				ScrollPerfProbeMetrics.RecordMeasurement(_item.Label, _item.Index, heightConstraint, size, stopwatch.Elapsed);
			}

			return size;
		}
	}

	static class ScrollPerfProbeMetrics
	{
		static readonly object Lock = new();
		static readonly Dictionary<string, double> FirstMeasuredHeights = new();
		static ScrollPerfMeasureSnapshot _current;
		static string _activeLabel;

		public static void Reset()
		{
			lock (Lock)
			{
				FirstMeasuredHeights.Clear();
				_current = default;
				_activeLabel = null;
			}
		}

		public static void BeginRun(string label)
		{
			lock (Lock)
			{
				_current = new ScrollPerfMeasureSnapshot();
				_activeLabel = label;
			}
		}

		public static ScrollPerfMeasureSnapshot EndRun(string label)
		{
			lock (Lock)
			{
				var snapshot = string.Equals(_activeLabel, label, StringComparison.Ordinal)
					? _current
					: default;
				_activeLabel = null;
				return snapshot;
			}
		}

		public static void RecordMeasurement(string label, int index, double heightConstraint, Size measuredSize, TimeSpan elapsed)
		{
			lock (Lock)
			{
				if (index == 0 && measuredSize.Height > 0)
				{
					FirstMeasuredHeights[label] = measuredSize.Height;
				}

				if (!string.Equals(_activeLabel, label, StringComparison.Ordinal))
				{
					return;
				}

				_current.TotalMeasureCalls++;
				_current.TotalMeasureTime += elapsed;

				if (index == 0)
				{
					return;
				}

				var firstHeight = FirstMeasuredHeights.TryGetValue(label, out var value)
					? value
					: 0;

				if (IsCachedHeightConstraint(heightConstraint, firstHeight))
				{
					_current.CachedHeightNonFirstMeasureCalls++;
				}
				else
				{
					_current.UncachedNonFirstMeasureCalls++;
				}
			}
		}

		static bool IsCachedHeightConstraint(double heightConstraint, double firstHeight)
		{
			return firstHeight > 0
				&& !double.IsInfinity(heightConstraint)
				&& Math.Abs(heightConstraint - firstHeight) < 0.5;
		}
	}

	interface IScrollPerfFrameSampler
	{
		void Start();

		void Stop();

		ScrollPerfFrameStats GetStats();
	}

	static class ScrollPerfFrameSampler
	{
		public static IScrollPerfFrameSampler Create(IDispatcher dispatcher)
		{
#if IOS || MACCATALYST
			return new AppleScrollPerfFrameSampler();
#else
			return new DispatcherTimerScrollPerfFrameSampler(dispatcher);
#endif
		}
	}

	sealed class ScrollPerfFrameRecorder
	{
		const double DefaultFrameBudgetMilliseconds = 1000d / 60d;
		int _frameSamples;
		int _estimatedDroppedFrames;
		TimeSpan _maxFrameGap;
		TimeSpan _totalFrameInterval;

		public void Reset()
		{
			_frameSamples = 0;
			_estimatedDroppedFrames = 0;
			_maxFrameGap = TimeSpan.Zero;
			_totalFrameInterval = TimeSpan.Zero;
		}

		public void RecordFrame(TimeSpan frameInterval, double frameBudgetMilliseconds = DefaultFrameBudgetMilliseconds)
		{
			if (frameInterval <= TimeSpan.Zero)
			{
				return;
			}

			_frameSamples++;
			_totalFrameInterval += frameInterval;

			if (frameInterval > _maxFrameGap)
			{
				_maxFrameGap = frameInterval;
			}

			var frameBudget = Math.Max(1, frameBudgetMilliseconds);
			var intervalMilliseconds = frameInterval.TotalMilliseconds;
			_estimatedDroppedFrames += Math.Max(0, (int)Math.Floor(intervalMilliseconds / frameBudget) - 1);
		}

		public ScrollPerfFrameStats GetStats()
		{
			var averageFrameInterval = _frameSamples == 0
				? TimeSpan.Zero
				: TimeSpan.FromTicks(_totalFrameInterval.Ticks / _frameSamples);

			return new ScrollPerfFrameStats(
				_frameSamples,
				_estimatedDroppedFrames,
				_maxFrameGap,
				averageFrameInterval);
		}
	}

#if IOS || MACCATALYST
	sealed class AppleScrollPerfFrameSampler : IScrollPerfFrameSampler
	{
		readonly ScrollPerfFrameRecorder _recorder = new();
		CADisplayLink _displayLink;
		double _lastTimestamp;

		public void Start()
		{
			Stop();

			_recorder.Reset();
			_lastTimestamp = 0;
			_displayLink = CADisplayLink.Create(OnFrame);
			_displayLink.AddToRunLoop(NSRunLoop.Current, NSRunLoopMode.Common);
		}

		public void Stop()
		{
			if (_displayLink is null)
			{
				return;
			}

			_displayLink.RemoveFromRunLoop(NSRunLoop.Current, NSRunLoopMode.Common);
			_displayLink.Dispose();
			_displayLink = null;
		}

		public ScrollPerfFrameStats GetStats()
		{
			return _recorder.GetStats();
		}

		void OnFrame()
		{
			if (_displayLink is null)
			{
				return;
			}

			var timestamp = _displayLink.Timestamp;
			if (_lastTimestamp > 0)
			{
				var frameBudgetMilliseconds = _displayLink.Duration > 0
					? _displayLink.Duration * 1000
					: 1000d / 60d;

				_recorder.RecordFrame(TimeSpan.FromSeconds(timestamp - _lastTimestamp), frameBudgetMilliseconds);
			}

			_lastTimestamp = timestamp;
		}
	}
#else
	sealed class DispatcherTimerScrollPerfFrameSampler : IScrollPerfFrameSampler
	{
		readonly IDispatcherTimer _timer;
		readonly Stopwatch _stopwatch = new();
		readonly ScrollPerfFrameRecorder _recorder = new();
		TimeSpan _lastTick;

		public DispatcherTimerScrollPerfFrameSampler(IDispatcher dispatcher)
		{
			_timer = dispatcher.CreateTimer();
			_timer.Interval = TimeSpan.FromMilliseconds(16);
			_timer.Tick += OnTick;
		}

		public void Start()
		{
			_recorder.Reset();
			_stopwatch.Restart();
			_lastTick = _stopwatch.Elapsed;
			_timer.Start();
		}

		public void Stop()
		{
			_timer.Stop();
		}

		public ScrollPerfFrameStats GetStats()
		{
			return _recorder.GetStats();
		}

		void OnTick(object sender, EventArgs e)
		{
			var now = _stopwatch.Elapsed;
			_recorder.RecordFrame(now - _lastTick);
			_lastTick = now;
		}
	}
#endif

	readonly record struct ScrollPerfFrameStats(
		int FrameSamples,
		int EstimatedDroppedFrames,
		TimeSpan MaxFrameGap,
		TimeSpan AverageFrameInterval)
	{
		public static ScrollPerfFrameStats Empty { get; } = new(0, 0, TimeSpan.Zero, TimeSpan.Zero);

		public double EstimatedMissedFramePercentage
		{
			get
			{
				var expectedFrames = FrameSamples + EstimatedDroppedFrames;
				return expectedFrames == 0
					? 0
					: EstimatedDroppedFrames * 100d / expectedFrames;
			}
		}
	}

	struct ScrollPerfMeasureSnapshot
	{
		public int TotalMeasureCalls { get; set; }

		public int CachedHeightNonFirstMeasureCalls { get; set; }

		public int UncachedNonFirstMeasureCalls { get; set; }

		public TimeSpan TotalMeasureTime { get; set; }
	}

	readonly record struct ScrollPerfProofResult(
		string Label,
		int TotalMeasureCalls,
		int CachedHeightNonFirstMeasureCalls,
		int UncachedNonFirstMeasureCalls,
		TimeSpan TotalMeasureTime,
		ScrollPerfFrameStats FrameStats,
		TimeSpan Elapsed,
		int Gen0Delta,
		int Gen1Delta,
		int Gen2Delta)
	{
		public static ScrollPerfProofResult Empty(string label)
		{
			return new ScrollPerfProofResult(label, 0, 0, 0, TimeSpan.Zero, ScrollPerfFrameStats.Empty, TimeSpan.Zero, 0, 0, 0);
		}

		public string ToDisplayText()
		{
			return $"{Label}: elapsed {Elapsed.TotalMilliseconds:0}ms; measures {TotalMeasureCalls}; cached non-first {CachedHeightNonFirstMeasureCalls}; uncached non-first {UncachedNonFirstMeasureCalls}; measure time {TotalMeasureTime.TotalMilliseconds:0}ms; GC {Gen0Delta}/{Gen1Delta}/{Gen2Delta}\nframes {FrameStats.FrameSamples}; missed {FrameStats.EstimatedDroppedFrames} ({FrameStats.EstimatedMissedFramePercentage:0.0}%); max frame gap {FrameStats.MaxFrameGap.TotalMilliseconds:0}ms; avg frame {FrameStats.AverageFrameInterval.TotalMilliseconds:0.0}ms";
		}
	}
}
