namespace ActivityStateManagerLeakRepro;

internal sealed record LeakRunOptions(
	int RecreateCount,
	int SubscriberCount,
	int EstimatedWorkMillisecondsPerSubscriber,
	int DelayMilliseconds)
{
	public const int DefaultRecreateCount = 120;
	public const int DefaultSubscriberCount = 4;
	public const int DefaultEstimatedWorkMillisecondsPerSubscriber = 25;
	public const int DefaultDelayMilliseconds = 250;

	public long ExpectedSubscriberInvocationsWithoutLeak(long actualLifecycleEvents) =>
		actualLifecycleEvents * SubscriberCount;

	public long EstimatedAvoidableWorkMilliseconds(long avoidableSubscriberInvocations) =>
		avoidableSubscriberInvocations * EstimatedWorkMillisecondsPerSubscriber;
}
