namespace IndicatorViewTemplateSwapLeakRepro;

public sealed class RetainedPayloadBehavior : Behavior<VisualElement>
{
	static readonly BindableProperty PayloadAnchorProperty = BindableProperty.CreateAttached(
		"PayloadAnchor",
		typeof(RetainedPayloadBehavior),
		typeof(RetainedPayloadBehavior),
		null);

	readonly byte[] _retainedPreviewCache;

	public RetainedPayloadBehavior(string templateName, int generationIndex, int indicatorIndex, long payloadBytes)
	{
		TemplateName = templateName;
		GenerationIndex = generationIndex;
		IndicatorIndex = indicatorIndex;
		_retainedPreviewCache = new byte[Math.Max(0, (int)Math.Min(int.MaxValue, payloadBytes))];

		for (var index = 0; index < _retainedPreviewCache.Length; index += 4096)
			_retainedPreviewCache[index] = (byte)(generationIndex + indicatorIndex + index);
	}

	public string TemplateName { get; }

	public int GenerationIndex { get; }

	public int IndicatorIndex { get; }

	public long PayloadBytes => _retainedPreviewCache.LongLength;

	public string Description => $"{TemplateName} indicator {IndicatorIndex + 1} caches {FormatBytes(PayloadBytes)} of preview data.";

	public static void AttachTo(VisualElement element, RetainedPayloadBehavior payload)
	{
		element.Behaviors.Add(payload);
		element.SetValue(PayloadAnchorProperty, payload);
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
