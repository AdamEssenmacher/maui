using System.Diagnostics;

namespace MapElementsSyncPerfRepro;

internal sealed class ReproSession
{
	readonly object _gate = new();
	readonly Stopwatch _stopwatch = new();
	readonly TaskCompletionSource<ReproResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
	readonly CancellationTokenSource _watchdogCancellation = new();
	DateTimeOffset _startedAt;
	DateTimeOffset _lastHeartbeatAt;
	TimeSpan _maxHeartbeatGap;
	TimeSpan _generationElapsed;
	TimeSpan _addElapsed;
	TimeSpan _observationElapsed;
	int _generatedElements;
	int _addedElements;
	int _mapElementCount;
	int _heartbeatCount;
	long _managedMemoryBefore;
	long _managedMemoryAfter;
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
			_managedMemoryBefore = GC.GetTotalMemory(false);
			_managedMemoryAfter = _managedMemoryBefore;
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
			_heartbeatCount++;
		}
	}

	public void MarkGeneratedElement(int oneBasedElementIndex)
	{
		var shouldWriteSnapshot = false;

		lock (_gate)
		{
			if (_completed)
				return;

			_generatedElements = Math.Max(_generatedElements, oneBasedElementIndex);
			shouldWriteSnapshot = ShouldWriteProgressSnapshot(oneBasedElementIndex);
		}

		if (shouldWriteSnapshot)
			AutoRunSettings.AppendResult(CreateSnapshot(ReproStatus.Running, "Generated element progress."));
	}

	public void MarkAddedElement(int oneBasedElementIndex, int mapElementCount)
	{
		var shouldWriteSnapshot = false;

		lock (_gate)
		{
			if (_completed)
				return;

			_addedElements = Math.Max(_addedElements, oneBasedElementIndex);
			_mapElementCount = mapElementCount;
			shouldWriteSnapshot = ShouldWriteProgressSnapshot(oneBasedElementIndex);
		}

		if (shouldWriteSnapshot)
			AutoRunSettings.AppendResult(CreateSnapshot(ReproStatus.Running, "Map.MapElements.Add progress."));
	}

	public void RecordGenerationElapsed(TimeSpan elapsed)
	{
		lock (_gate)
			_generationElapsed = elapsed;
	}

	public void RecordAddElapsed(TimeSpan elapsed)
	{
		lock (_gate)
			_addElapsed = elapsed;
	}

	public void RecordObservationElapsed(TimeSpan elapsed)
	{
		lock (_gate)
			_observationElapsed = elapsed;
	}

	public void RecordMapElementCount(int mapElementCount)
	{
		lock (_gate)
			_mapElementCount = mapElementCount;
	}

	public void RecordManagedMemoryAfter()
	{
		lock (_gate)
			_managedMemoryAfter = GC.GetTotalMemory(false);
	}

	public void Complete()
	{
		RecordManagedMemoryAfter();
		Finish(ReproStatus.Completed, "Completed.");
	}

	public void Fail(Exception exception)
	{
		RecordManagedMemoryAfter();
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
			_generatedElements,
			_addedElements,
			_mapElementCount,
			_generationElapsed,
			_addElapsed,
			_observationElapsed,
			_managedMemoryBefore,
			_managedMemoryAfter,
			_heartbeatCount,
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

	bool ShouldWriteProgressSnapshot(int oneBasedElementIndex)
	{
		var interval = Math.Max(1, Options.ProgressLogInterval);
		return oneBasedElementIndex == 1 ||
			oneBasedElementIndex == Options.ElementCount ||
			oneBasedElementIndex % interval == 0;
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
				_managedMemoryAfter = GC.GetTotalMemory(false);
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
