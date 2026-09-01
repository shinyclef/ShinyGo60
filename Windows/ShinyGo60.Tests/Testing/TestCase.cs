namespace ShinyGo60.Tests.Testing;

internal sealed record TestCase(string Name, Func<ValueTask> ExecuteAsync);
