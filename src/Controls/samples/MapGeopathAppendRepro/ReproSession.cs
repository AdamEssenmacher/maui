using System.Diagnostics;

namespace MapGeopathAppendRepro;

internal sealed class ReproSession
{
	readonly Stopwatch _stopwatch = new();
	readonly TaskCompletionSource<ReproResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
	DateTimeOffset _startedAt;
	bool _started;

	public ReproSession(ReproOptions options)
	{
		Options = options;
	}

	public static ReproSession? Current { get; set; }

	public ReproOptions Options { get; }

	public Task<ReproResult> Completion => _completion.Task;

	public void Start()
	{
		if (_started)
			return;

		_started = true;
		_startedAt = DateTimeOffset.UtcNow;
		_stopwatch.Start();
		AutoRunSettings.AppendResult(ReproResult.CreateRunning(Options, _startedAt));
	}

	public ReproResult CreateResult(
		ReproStatus status,
		RuntimeImpact impact,
		int? retainedOptionsPointCountBeforeMutation,
		int? retainedOptionsPointCountAfterMutation,
		int? retainedOptionsPointCountAfterReAdd,
		int? nativePolylinePointCountAfterReAdd,
		string? message)
	{
		return new ReproResult(
			Options,
			status,
			_startedAt,
			_stopwatch.Elapsed,
			impact,
			MapDiagnostics.PlatformName,
			Options.LogicalPointCount,
			retainedOptionsPointCountBeforeMutation,
			retainedOptionsPointCountAfterMutation,
			retainedOptionsPointCountAfterReAdd,
			nativePolylinePointCountAfterReAdd,
			message);
	}

	public void Complete(ReproResult result)
	{
		_stopwatch.Stop();
		AutoRunSettings.AppendResult(result);
		_completion.TrySetResult(result);
	}

	public void Fail(Exception exception)
	{
		_stopwatch.Stop();
		var result = CreateResult(
			ReproStatus.Failed,
			RuntimeImpact.Empty,
			null,
			null,
			null,
			null,
			exception.ToString());

		AutoRunSettings.AppendResult(result);
		_completion.TrySetResult(result);
	}
}
