namespace ShinyGo60.Platform.Windows.Shell;

public interface ITaskbarWindowProvider
{
    TaskbarWindowInfo? GetCurrent();
}
