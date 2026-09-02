using System.Globalization;

namespace ShinyGo60.Protocol.Messages;

public readonly record struct LayoutFingerprint(ulong Value)
{
    private const string IdentifierPrefix = "sg60-v1-";

    public static LayoutFingerprint FromLayoutIdentifier(string layoutIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layoutIdentifier);
        if (layoutIdentifier.Length != IdentifierPrefix.Length + 32 ||
            !layoutIdentifier.StartsWith(IdentifierPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("The layout identifier has an unsupported format.", nameof(layoutIdentifier));
        }

        ReadOnlySpan<char> hexadecimal = layoutIdentifier.AsSpan(IdentifierPrefix.Length, 16);
        if (!ulong.TryParse(hexadecimal, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong value) || value == 0)
        {
            throw new ArgumentException("The layout identifier does not contain a usable wire fingerprint.", nameof(layoutIdentifier));
        }

        return new LayoutFingerprint(value);
    }

    public override string ToString()
    {
        return this.Value.ToString("x16", CultureInfo.InvariantCulture);
    }
}
