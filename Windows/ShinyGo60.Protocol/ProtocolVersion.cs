namespace ShinyGo60.Protocol;

public readonly record struct ProtocolVersion(ushort Major, ushort Minor)
{
    public override string ToString()
    {
        return $"{this.Major}.{this.Minor}";
    }
}
