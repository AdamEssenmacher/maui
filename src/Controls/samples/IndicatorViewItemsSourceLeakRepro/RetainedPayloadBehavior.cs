namespace IndicatorViewItemsSourceLeakRepro;

internal sealed class RetainedPayloadBehavior : Behavior<VisualElement>
{
	readonly byte[] _payload;

	public RetainedPayloadBehavior(string owner, int cycle, long payloadBytes)
	{
		Owner = owner;
		Cycle = cycle;

		var length = (int)Math.Min(Math.Max(0, payloadBytes), int.MaxValue);
		_payload = new byte[length];

		for (var i = 0; i < _payload.Length; i += 4096)
			_payload[i] = (byte)(cycle + owner.Length + i);
	}

	public string Owner { get; }

	public int Cycle { get; }

	public long PayloadBytes => _payload.LongLength;

	public string Description => $"{Owner} payload for visit {Cycle + 1}: {FormatBytes(PayloadBytes)}";

	static string FormatBytes(long bytes)
	{
		if (bytes >= 1024L * 1024L)
			return $"{bytes / 1024d / 1024d:0.0} MB";

		if (bytes >= 1024L)
			return $"{bytes / 1024d:0.0} KB";

		return $"{bytes} B";
	}
}
