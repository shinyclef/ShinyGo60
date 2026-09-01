namespace ShinyGo60.TransportSpike;

internal static class Program
{
    public static Task<int> Main(string[] args)
    {
        return TransportSpikeApplication.RunAsync(args);
    }
}
