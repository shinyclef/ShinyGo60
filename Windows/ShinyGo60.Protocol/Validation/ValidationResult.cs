namespace ShinyGo60.Protocol.Validation;

public sealed class ValidationResult
{
    public ValidationResult(IEnumerable<ValidationIssue> issues)
    {
        this.Issues = issues.ToArray();
    }

    public IReadOnlyList<ValidationIssue> Issues { get; }

    public bool IsValid => this.Issues.Count == 0;
}
