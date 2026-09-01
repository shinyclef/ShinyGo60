namespace ShinyGo60.Builder.Core.Processes;

public sealed record ProcessInvocation(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);
