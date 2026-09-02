namespace ShinyGo60.Protocol;

public readonly record struct ProtocolVersion(ushort Major, ushort Minor)
{
    public static readonly ProtocolVersion Current = new(1, 1);

    public override string ToString()
    {
        return $"{this.Major}.{this.Minor}";
    }
}
