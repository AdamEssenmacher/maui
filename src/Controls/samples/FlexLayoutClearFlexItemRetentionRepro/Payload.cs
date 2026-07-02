namespace FlexLayoutClearFlexItemRetentionRepro;

public sealed class Payload
{
	public Payload(int index, int byteCount)
	{
		Index = index;
		Buffer = new byte[byteCount];
		Buffer[0] = (byte)(index % 251);
		Buffer[^1] = (byte)((index + 31) % 251);
	}

	public int Index { get; }
	public byte[] Buffer { get; }
}
