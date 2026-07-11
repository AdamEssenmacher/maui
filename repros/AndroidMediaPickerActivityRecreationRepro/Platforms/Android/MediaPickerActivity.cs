using Android.App;
using Android.OS;
using Android.Runtime;
using Android.Views;
using AndroidX.AppCompat.App;
using Microsoft.Maui.Media;
using AButton = global::Android.Widget.Button;
using AColor = global::Android.Graphics.Color;
using ALinearLayout = global::Android.Widget.LinearLayout;
using ALog = global::Android.Util.Log;
using AOrientation = global::Android.Widget.Orientation;
using AScrollView = global::Android.Widget.ScrollView;
using ATextView = global::Android.Widget.TextView;
using ATypeface = global::Android.Graphics.Typeface;
using ATypefaceStyle = global::Android.Graphics.TypefaceStyle;

namespace AndroidMediaPickerActivityRecreationRepro.Platforms.Android;

[Activity(Label = "MediaPicker child activity", Theme = "@style/Maui.SplashTheme")]
[Register("com.microsoft.maui.repros.mediapickerrecreation.MediaPickerActivity")]
public sealed class MediaPickerActivity : AppCompatActivity
{
	public const string LogTag = "MediaPickerRecreationRepro";
	static readonly AColor NeutralColor = AColor.Rgb(55, 65, 81);
	static readonly AColor WaitingColor = AColor.Rgb(180, 83, 9);
	static readonly AColor SuccessColor = AColor.Rgb(21, 128, 61);
	static readonly AColor FailureColor = AColor.Rgb(185, 28, 28);

	readonly int _activityId = ReproState.AllocateActivityId();
	CancellationTokenSource? _failureCheckCancellation;
	ATextView _activityLabel = null!;
	ATextView _statusLabel = null!;
	ATextView _taskLabel = null!;
	ATextView _elapsedLabel = null!;
	ATextView _eventLogLabel = null!;
	AButton _pickButton = null!;
	bool _uiReady;

	protected override void OnCreate(Bundle? savedInstanceState)
	{
		base.OnCreate(savedInstanceState);
		Microsoft.Maui.ApplicationModel.Platform.Init(this, savedInstanceState);

		Record($"Activity {_activityId}: OnCreate");
		BuildContent();
		_uiReady = true;
		RenderSnapshot();
	}

	protected override void OnResume()
	{
		base.OnResume();
		Record($"Activity {_activityId}: OnResume");
		RenderSnapshot();
	}

	public override void OnWindowFocusChanged(bool hasFocus)
	{
		base.OnWindowFocusChanged(hasFocus);

		_failureCheckCancellation?.Cancel();
		_failureCheckCancellation?.Dispose();
		_failureCheckCancellation = null;

		if (!hasFocus || !_uiReady)
			return;

		RenderSnapshot();
		var snapshot = ReproState.GetSnapshot();

		if (snapshot.IsPending && snapshot.LaunchActivityId != _activityId)
		{
			_failureCheckCancellation = new CancellationTokenSource();
			_ = ObservePendingTaskAfterPickerReturnAsync(snapshot.RequestId, _failureCheckCancellation.Token);
		}
	}

	protected override void OnDestroy()
	{
		_failureCheckCancellation?.Cancel();
		_failureCheckCancellation?.Dispose();
		_failureCheckCancellation = null;

		Record($"Activity {_activityId}: OnDestroy changingConfigurations={IsChangingConfigurations} finishing={IsFinishing}");
		base.OnDestroy();
	}

	void BuildContent()
	{
		var scrollView = new AScrollView(this)
		{
			FillViewport = true
		};
		scrollView.SetBackgroundColor(AColor.White);

		var layout = new ALinearLayout(this)
		{
			Orientation = AOrientation.Vertical
		};
		var padding = Dp(20);
		layout.SetPadding(padding, padding, padding, padding);

		var title = CreateTextView("MediaPicker child activity", 24, AColor.Rgb(17, 24, 39));
		title.SetTypeface(title.Typeface, ATypefaceStyle.Bold);
		layout.AddView(title);

		var instructions = CreateTextView(
			"Control: tap Pick photos, then press Back without rotating.\n\n" +
			"Repro: tap Pick photos, rotate while the Android picker is open, then press Back.",
			16,
			NeutralColor);
		instructions.SetPadding(0, Dp(12), 0, Dp(16));
		layout.AddView(instructions);

		_activityLabel = CreateTextView(string.Empty, 18, NeutralColor);
		_activityLabel.SetTypeface(_activityLabel.Typeface, ATypefaceStyle.Bold);
		layout.AddView(_activityLabel);

		_statusLabel = CreateTextView(string.Empty, 17, NeutralColor);
		_statusLabel.SetPadding(0, Dp(12), 0, Dp(8));
		_statusLabel.SetTypeface(_statusLabel.Typeface, ATypefaceStyle.Bold);
		layout.AddView(_statusLabel);

		_taskLabel = CreateTextView(string.Empty, 15, NeutralColor);
		layout.AddView(_taskLabel);

		_elapsedLabel = CreateTextView(string.Empty, 15, NeutralColor);
		_elapsedLabel.SetPadding(0, Dp(4), 0, Dp(14));
		layout.AddView(_elapsedLabel);

		_pickButton = new AButton(this)
		{
			Text = "Pick photos",
			ContentDescription = "PickPhotosButton"
		};
		_pickButton.Click += OnPickPhotosClicked;
		layout.AddView(_pickButton);

		var eventLogTitle = CreateTextView("Event log", 16, AColor.Rgb(17, 24, 39));
		eventLogTitle.SetPadding(0, Dp(20), 0, Dp(8));
		eventLogTitle.SetTypeface(eventLogTitle.Typeface, ATypefaceStyle.Bold);
		layout.AddView(eventLogTitle);

		_eventLogLabel = CreateTextView(string.Empty, 13, NeutralColor);
		_eventLogLabel.Typeface = ATypeface.Monospace;
		_eventLogLabel.SetTextIsSelectable(true);
		layout.AddView(_eventLogLabel);

		scrollView.AddView(layout);
		SetContentView(scrollView);
	}

	async void OnPickPhotosClicked(object? sender, EventArgs e)
	{
		var requestId = ReproState.BeginRequest(_activityId);
		ALog.Info(LogTag, $"Activity {_activityId}: request {requestId} started");
		RenderSnapshot();

		try
		{
			var pickerTask = MediaPicker.PickPhotosAsync();
			ReproState.AttachTask(requestId, pickerTask);
			ALog.Info(LogTag, $"Request {requestId}: task attached ({pickerTask.Status})");
			RenderSnapshot();

			var results = await pickerTask;
			var outcome = $"PASS: request {requestId} completed with {results.Count} result(s)";
			ReproState.CompleteRequest(requestId, outcome);
			ALog.Info(LogTag, outcome);
		}
		catch (System.OperationCanceledException)
		{
			var outcome = $"PASS: request {requestId} completed with cancellation";
			ReproState.CompleteRequest(requestId, outcome);
			ALog.Info(LogTag, outcome);
		}
		catch (Exception ex)
		{
			var outcome = $"ERROR: request {requestId} failed with {ex.GetType().Name}: {ex.Message}";
			ReproState.CompleteRequest(requestId, outcome);
			ALog.Error(LogTag, outcome);
		}

		if (!IsDestroyed && _uiReady)
			RenderSnapshot();
	}

	async Task ObservePendingTaskAfterPickerReturnAsync(int requestId, CancellationToken cancellationToken)
	{
		try
		{
			await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

			if (cancellationToken.IsCancellationRequested || IsDestroyed)
				return;

			RunOnUiThread(() =>
			{
				if (ReproState.MarkHangObserved(requestId, _activityId))
				{
					var snapshot = ReproState.GetSnapshot();
					ALog.Error(LogTag, snapshot.Outcome);
				}

				RenderSnapshot();
			});
		}
		catch (System.OperationCanceledException)
		{
		}
	}

	void RenderSnapshot()
	{
		if (!_uiReady)
			return;

		var snapshot = ReproState.GetSnapshot();
		_activityLabel.Text = $"Current activity instance: {_activityId}";
		_statusLabel.Text = snapshot.Outcome;
		_statusLabel.SetTextColor(GetOutcomeColor(snapshot.Outcome));
		_taskLabel.Text = snapshot.HasTask
			? $"Task: {snapshot.TaskStatus} (IsCompleted={snapshot.TaskIsCompleted})"
			: $"Task: {snapshot.TaskStatus}";
		_elapsedLabel.Text = $"Elapsed: {snapshot.Elapsed.TotalSeconds:F1} seconds";
		_eventLogLabel.Text = snapshot.Events.Count == 0
			? "No events recorded."
			: string.Join(System.Environment.NewLine, snapshot.Events);
		_pickButton.Enabled = !snapshot.IsPending;
	}

	void Record(string message)
	{
		ReproState.Record(message);
		ALog.Info(LogTag, message);
	}

	ATextView CreateTextView(string text, float textSize, AColor color)
	{
		var view = new ATextView(this)
		{
			Text = text,
			TextSize = textSize
		};
		view.SetTextColor(color);
		return view;
	}

	int Dp(int value) => (int)((value * Resources!.DisplayMetrics!.Density) + 0.5f);

	static AColor GetOutcomeColor(string outcome)
	{
		if (outcome.StartsWith("PASS", StringComparison.Ordinal))
			return SuccessColor;

		if (outcome.StartsWith("FAIL", StringComparison.Ordinal) || outcome.StartsWith("ERROR", StringComparison.Ordinal))
			return FailureColor;

		if (outcome.StartsWith("WAITING", StringComparison.Ordinal))
			return WaitingColor;

		return NeutralColor;
	}
}
