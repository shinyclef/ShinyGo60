namespace ShinyGo60.Platform.Windows.Shell;

public interface ITaskbarWindowProvider
{
    TaskbarWindowInfo? GetCurrent();

    IReadOnlyList<TaskbarWindowInfo> GetAll()
    {
        TaskbarWindowInfo? current = this.GetCurrent();
        return current is null ? [] : [current];
    }
}
