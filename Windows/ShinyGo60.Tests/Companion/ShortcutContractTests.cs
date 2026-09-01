using ShinyGo60.Companion.Core.Shortcuts;
using ShinyGo60.Tests.Testing;

namespace ShinyGo60.Tests.Companion;

internal static class ShortcutContractTests
{
    public static ValueTask RunAsync()
    {
        ShortcutBinding binding = new("Ctrl+Alt+Shift+F13", ShortcutActionKind.MomentaryLayer, 3);

        AssertEx.Equal(ShortcutActionKind.MomentaryLayer, binding.Action);
        AssertEx.Equal(3, binding.TargetLayerId);
        return ValueTask.CompletedTask;
    }
}
