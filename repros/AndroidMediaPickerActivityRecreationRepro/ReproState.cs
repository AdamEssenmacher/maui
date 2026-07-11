namespace AndroidMediaPickerActivityRecreationRepro;

internal static class ReproState
{
	const int MaxEvents = 14;
	static readonly object s_gate = new();
	static readonly List<string> s_events = new();

	static int s_nextActivityId;
	static int s_nextRequestId;
	static int s_requestId;
	static int s_launchActivityId;
	static Task? s_pickerTask;
	static DateTimeOffset? s_launchedAt;
	static DateTimeOffset? s_completedAt;
	static bool s_hangObserved;
	static string s_outcome = "READY: no picker request started";

	public static int AllocateActivityId() => Interlocked.Increment(ref s_nextActivityId);

	public static int BeginRequest(int activityId)
	{
		lock (s_gate)
		{
			s_requestId = Interlocked.Increment(ref s_nextRequestId);
			s_launchActivityId = activityId;
			s_pickerTask = null;
			s_launchedAt = DateTimeOffset.UtcNow;
			s_completedAt = null;
			s_hangObserved = false;
			s_outcome = "WAITING: PickPhotosAsync has not completed";
			AppendLocked($"Activity {activityId}: request {s_requestId} started");
			return s_requestId;
		}
	}

	public static void AttachTask(int requestId, Task pickerTask)
	{
		ArgumentNullException.ThrowIfNull(pickerTask);

		lock (s_gate)
		{
			if (requestId != s_requestId)
				return;

			s_pickerTask = pickerTask;
			AppendLocked($"Request {requestId}: task attached ({pickerTask.Status})");
		}
	}

	public static bool CompleteRequest(int requestId, string outcome)
	{
		lock (s_gate)
		{
			if (requestId != s_requestId)
				return false;

			s_completedAt = DateTimeOffset.UtcNow;
			s_outcome = outcome;
			AppendLocked(outcome);
			return true;
		}
	}

	public static bool MarkHangObserved(int requestId, int currentActivityId)
	{
		lock (s_gate)
		{
			if (requestId != s_requestId || s_hangObserved || s_pickerTask?.IsCompleted != false)
				return false;

			s_hangObserved = true;
			s_outcome = $"FAIL: picker returned through activity {currentActivityId}, but request {requestId} from activity {s_launchActivityId} is still pending";
			AppendLocked(s_outcome);
			return true;
		}
	}

	public static void Record(string message)
	{
		lock (s_gate)
			AppendLocked(message);
	}

	public static ReproSnapshot GetSnapshot()
	{
		lock (s_gate)
		{
			var task = s_pickerTask;
			var elapsed = TimeSpan.Zero;

			if (s_launchedAt is DateTimeOffset launchedAt)
				elapsed = (s_completedAt ?? DateTimeOffset.UtcNow) - launchedAt;

			return new ReproSnapshot(
				s_requestId,
				s_launchActivityId,
				s_outcome,
				task is not null,
				task?.IsCompleted == true,
				task?.Status.ToString() ?? (s_requestId == 0 ? "Not started" : "Starting"),
				s_requestId != 0 && s_completedAt is null && (task is null || !task.IsCompleted),
				s_hangObserved,
				elapsed,
				s_events.ToArray());
		}
	}

	static void AppendLocked(string message)
	{
		s_events.Add($"{DateTimeOffset.Now:HH:mm:ss.fff}  {message}");

		if (s_events.Count > MaxEvents)
			s_events.RemoveAt(0);
	}
}

internal sealed record ReproSnapshot(
	int RequestId,
	int LaunchActivityId,
	string Outcome,
	bool HasTask,
	bool TaskIsCompleted,
	string TaskStatus,
	bool IsPending,
	bool HangObserved,
	TimeSpan Elapsed,
	IReadOnlyList<string> Events);
