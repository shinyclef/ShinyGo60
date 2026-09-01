namespace ShinyGo60.Tests.Testing;

internal static class TestRunner
{
    public static async Task<int> RunAsync(IReadOnlyList<TestCase> tests)
    {
        int failedCount = 0;

        foreach (TestCase test in tests)
        {
            try
            {
                await test.ExecuteAsync();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception exception)
            {
                failedCount++;
                Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
            }
        }

        Console.WriteLine($"{tests.Count - failedCount}/{tests.Count} scaffold checks passed.");
        return failedCount;
    }
}
