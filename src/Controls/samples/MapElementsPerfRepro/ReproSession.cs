using System.Diagnostics;

namespace MapElementsPerfRepro;

internal sealed class ReproSession
{
	readonly object _gate = new();
	readonly Stopwatch _stopwatch = new();
	readonly TaskCompletionSource<ReproResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
	readonly CancellationTokenSource _watchdogCancellation = new();
	DateTimeOffset _startedAt;
	DateTimeOffset _lastHeartbeatAt;
	TimeSpan _maxHeartbeatGap;
	int _generatedPolylines;
	int _addedPolylines;
	long _generatedLocations;
	bool _started;
	bool _completed;

	public ReproSession(ReproOptions options)
	{
		Options = options;
	}

	public static ReproSession? Current { get; set; }

	public ReproOptions Options { get; }

	public Task<ReproResult> Completion => _completion.Task;

	public void Start()
	{
		lock (_gate)
		{
			if (_started)
				return;

			_started = true;
			_startedAt = DateTimeOffset.UtcNow;
			_lastHeartbeatAt = _startedAt;
			_stopwatch.Start();
		}

		AutoRunSettings.AppendResult(CreateSnapshot(ReproStatus.Running, "Started."));
		_ = Task.Run(WatchdogLoopAsync);
	}

	public void MarkHeartbeat()
	{
		lock (_gate)
		{
			if (_completed)
				return;

			var now = DateTimeOffset.UtcNow;
			var heartbeatGap = now - _lastHeartbeatAt;
			if (heartbeatGap > _maxHeartbeatGap)
				_maxHeartbeatGap = heartbeatGap;

			_lastHeartbeatAt = now;
		}
	}

	public void MarkGeneratedPolyline(int oneBasedPolylineIndex)
	{
		var shouldWriteSnapshot = false;

		lock (_gate)
		{
			if (_completed)
				return;

			_generatedPolylines = Math.Max(_generatedPolylines, oneBasedPolylineIndex);
			_generatedLocations = (long)_generatedPolylines * Options.PointsPerPolyline;
			shouldWriteSnapshot = ShouldWriteProgressSnapshot(oneBasedPolylineIndex);
		}

		if (shouldWriteSnapshot)
			AutoRunSettings.AppendResult(CreateSnapshot(ReproStatus.Running, "Generated polyline progress."));
	}

	public void MarkAddedPolyline(int oneBasedPolylineIndex)
	{
		var shouldWriteSnapshot = false;

		lock (_gate)
		{
			if (_completed)
				return;

			_addedPolylines = Math.Max(_addedPolylines, oneBasedPolylineIndex);
			shouldWriteSnapshot = ShouldWriteProgressSnapshot(oneBasedPolylineIndex);
		}

		if (shouldWriteSnapshot)
			AutoRunSettings.AppendResult(CreateSnapshot(ReproStatus.Running, "Map.MapElements.Add progress."));
	}

	public void Complete()
	{
		Finish(ReproStatus.Completed, "Completed.");
	}

	public void Fail(Exception exception)
	{
		Finish(ReproStatus.Failed, exception.ToString());
	}

	ReproResult CreateSnapshot(ReproStatus status, string? message)
	{
		lock (_gate)
		{
			return CreateSnapshotNoLock(status, message);
		}
	}

	ReproResult CreateSnapshotNoLock(ReproStatus status, string? message)
	{
		return new ReproResult(
			Options,
			status,
			_startedAt,
			_stopwatch.Elapsed,
			_generatedPolylines,
			_addedPolylines,
			_generatedLocations,
			_lastHeartbeatAt,
			_maxHeartbeatGap,
			message);
	}

	void Finish(ReproStatus status, string? message)
	{
		ReproResult result;

		lock (_gate)
		{
			if (_completed)
				return;

			_completed = true;
			_stopwatch.Stop();
			result = CreateSnapshotNoLock(status, message);
		}

		_watchdogCancellation.Cancel();
		AutoRunSettings.AppendResult(result);
		_completion.TrySetResult(result);
	}

	bool ShouldWriteProgressSnapshot(int oneBasedPolylineIndex)
	{
		var interval = Math.Max(1, Options.ProgressLogInterval);
		return oneBasedPolylineIndex == 1 ||
			oneBasedPolylineIndex == Options.PolylineCount ||
			oneBasedPolylineIndex % interval == 0;
	}

	async Task WatchdogLoopAsync()
	{
		var token = _watchdogCancellation.Token;
		var staleHeartbeatThreshold = TimeSpan.FromSeconds(Math.Min(5, Math.Max(2, Options.WatchdogTimeoutSeconds / 2)));
		var timeout = TimeSpan.FromSeconds(Options.WatchdogTimeoutSeconds);

		while (!token.IsCancellationRequested)
		{
			try
			{
				await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}

			ReproResult snapshot;
			TimeSpan heartbeatAge;

			lock (_gate)
			{
				if (_completed)
					return;

				var now = DateTimeOffset.UtcNow;
				heartbeatAge = now - _lastHeartbeatAt;
				snapshot = CreateSnapshotNoLock(ReproStatus.Running, "Watchdog snapshot.");
			}

			AutoRunSettings.AppendResult(snapshot);

			if (snapshot.Elapsed >= timeout && heartbeatAge >= staleHeartbeatThreshold)
			{
				Finish(
					ReproStatus.Hung,
					$"Watchdog timed out after {snapshot.Elapsed:c}; last UI heartbeat was {heartbeatAge.TotalSeconds:0.0}s ago.");
				return;
			}
		}
	}
}
