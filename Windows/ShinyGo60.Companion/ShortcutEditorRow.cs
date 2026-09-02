using System.ComponentModel;
using System.Runtime.CompilerServices;
using ShinyGo60.Companion.Core.Shortcuts;

namespace ShinyGo60.Companion;

public sealed class ShortcutEditorRow : INotifyPropertyChanged
{
    private string shortcut;
    private ShortcutActionKind action;
    private string targetLayer;

    public ShortcutEditorRow(string shortcut, ShortcutActionKind action, string targetLayer)
    {
        this.shortcut = shortcut;
        this.action = action;
        this.targetLayer = targetLayer;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Shortcut
    {
        get => this.shortcut;
        set => this.SetField(ref this.shortcut, value);
    }

    public ShortcutActionKind Action
    {
        get => this.action;
        set => this.SetField(ref this.action, value);
    }

    public string TargetLayer
    {
        get => this.targetLayer;
        set => this.SetField(ref this.targetLayer, value);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
