namespace GridLayoutManagerGridStructureRetentionRepro;

public sealed class Payload
{
	public Payload(int id, int bytes)
	{
		Id = id;
		Buffer = new byte[bytes];

		for (var i = 0; i < Buffer.Length; i += 4096)
			Buffer[i] = (byte)(id % 251);
	}

	public int Id { get; }

	public byte[] Buffer { get; }
}
