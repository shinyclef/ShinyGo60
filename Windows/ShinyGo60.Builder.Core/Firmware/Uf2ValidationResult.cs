namespace ShinyGo60.Builder.Core.Firmware;

public sealed record Uf2ValidationResult(
    long FileSize,
    int BlockCount,
    int SegmentCount);
