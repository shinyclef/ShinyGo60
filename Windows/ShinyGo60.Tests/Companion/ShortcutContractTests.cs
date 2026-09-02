using System.Text;
using ShinyGo60.Companion.Core.Configuration;
using ShinyGo60.Companion.Core.Shortcuts;
using ShinyGo60.Protocol;
using ShinyGo60.Protocol.Manifests;
using ShinyGo60.Tests.Testing;

namespace ShinyGo60.Tests.Companion;

internal static class ShortcutContractTests
{
    public static async ValueTask RunAsync()
    {
        VerifyConfigurationResolution();
        VerifyGestureParsing();
        VerifySyntheticF23Sequence();
        VerifyHeldKeyAcrossSessionLoss();
        VerifyInvalidConfiguration();
        await VerifyConfigurationWriteAsync();
    }

    private static void VerifyConfigurationResolution()
    {
        ResolvedCompanionConfiguration configuration = CompanionConfigurationJson.DeserializeAndResolve(
            Encoding.UTF8.GetBytes(ValidConfiguration),
            CreateManifest());

        AssertEx.Equal(TransportPreference.Automatic, configuration.TransportPreference);
        AssertEx.Equal(1, configuration.Shortcuts.Count);
        ShortcutBinding binding = configuration.Shortcuts[0];
        AssertEx.Equal("F23", binding.Gesture.ToString());
        AssertEx.Equal(ShortcutActionKind.MomentaryLayer, binding.Action);
        AssertEx.Equal((byte)1, binding.TargetLayerId);
        AssertEx.Equal("Navigation", binding.TargetLayerName);
    }

    private static void VerifyGestureParsing()
    {
        ShortcutGesture gesture = ShortcutGesture.Parse("shift + ctrl + alt + f23");
        AssertEx.Equal("Ctrl+Alt+Shift+F23", gesture.ToString());
        AssertEx.Equal(
            ShortcutModifiers.Control | ShortcutModifiers.Alt | ShortcutModifiers.Shift,
            gesture.Modifiers);
        AssertEx.Equal("F23", gesture.Key);
        AssertEx.Throws<FormatException>(() => ShortcutGesture.Parse("Ctrl+Ctrl+F23"));
        AssertEx.Throws<FormatException>(() => ShortcutGesture.Parse("F25"));
    }

    private static void VerifySyntheticF23Sequence()
    {
        ShortcutBinding binding = new(
            ShortcutGesture.Parse("F23"),
            ShortcutActionKind.MomentaryLayer,
            1,
            "Navigation");
        ShortcutRouter router = new([binding]);

        ShortcutRoute press = router.Route(F23(ShortcutKeyState.Down));
        AssertEx.Equal(ShortcutRouteKind.Pressed, press.Kind);
        AssertEx.Equal(binding, press.Binding);
        AssertEx.Equal(ShortcutRouteKind.RepeatSuppressed, router.Route(F23(ShortcutKeyState.Down)).Kind);
        ShortcutRoute release = router.Route(F23(ShortcutKeyState.Up));
        AssertEx.Equal(ShortcutRouteKind.Released, release.Kind);
        AssertEx.Equal(binding, release.Binding);
        AssertEx.Equal(ShortcutRouteKind.Ignored, router.Route(F23(ShortcutKeyState.Up)).Kind);

        ShortcutRoute injectedPress = router.Route(F23(ShortcutKeyState.Down, isInjected: true));
        AssertEx.Equal(ShortcutRouteKind.Pressed, injectedPress.Kind);
        AssertEx.Equal(ShortcutRouteKind.Released, router.Route(F23(ShortcutKeyState.Up, isInjected: true)).Kind);
    }

    private static void VerifyHeldKeyAcrossSessionLoss()
    {
        ShortcutBinding binding = new(
            ShortcutGesture.Parse("F23"),
            ShortcutActionKind.MomentaryLayer,
            1,
            "Navigation");
        ShortcutRouter router = new([binding]);

        AssertEx.Equal(ShortcutRouteKind.Pressed, router.Route(F23(ShortcutKeyState.Down)).Kind);
        router.ForgetActiveBindings();
        AssertEx.Equal(ShortcutRouteKind.RepeatSuppressed, router.Route(F23(ShortcutKeyState.Down)).Kind);
        AssertEx.Equal(ShortcutRouteKind.Ignored, router.Route(F23(ShortcutKeyState.Up)).Kind);
        AssertEx.Equal(ShortcutRouteKind.Pressed, router.Route(F23(ShortcutKeyState.Down)).Kind);
        AssertEx.Equal(ShortcutRouteKind.Released, router.Route(F23(ShortcutKeyState.Up)).Kind);

        router.SeedPressedKeys(["F23"]);
        AssertEx.Equal(ShortcutRouteKind.RepeatSuppressed, router.Route(F23(ShortcutKeyState.Down)).Kind);
        AssertEx.Equal(ShortcutRouteKind.Ignored, router.Route(F23(ShortcutKeyState.Up)).Kind);
    }

    private static void VerifyInvalidConfiguration()
    {
        string duplicate = ValidConfiguration.Replace(
            "    }\n  ]",
            "    },\n    { \"shortcut\": \"f23\", \"action\": \"goToLayer\", \"targetLayer\": \"Home\" }\n  ]",
            StringComparison.Ordinal);
        AssertEx.Throws<InvalidDataException>(
            () => CompanionConfigurationJson.DeserializeAndResolve(Encoding.UTF8.GetBytes(duplicate), CreateManifest()));

        string missingLayer = ValidConfiguration.Replace("Navigation", "Missing", StringComparison.Ordinal);
        AssertEx.Throws<InvalidDataException>(
            () => CompanionConfigurationJson.DeserializeAndResolve(Encoding.UTF8.GetBytes(missingLayer), CreateManifest()));

        string extraProperty = ValidConfiguration.Replace(
            "\"schemaVersion\": 1,",
            "\"schemaVersion\": 1, \"unexpected\": true,",
            StringComparison.Ordinal);
        AssertEx.Throws<InvalidDataException>(
            () => CompanionConfigurationJson.DeserializeAndResolve(Encoding.UTF8.GetBytes(extraProperty), CreateManifest()));

        string numericEnum = ValidConfiguration.Replace(
            "\"transportPreference\": \"automatic\"",
            "\"transportPreference\": 0",
            StringComparison.Ordinal);
        AssertEx.Throws<InvalidDataException>(
            () => CompanionConfigurationJson.DeserializeAndResolve(Encoding.UTF8.GetBytes(numericEnum), CreateManifest()));

        string missingShortcut = ValidConfiguration.Replace("\"shortcut\": \"F23\"", "\"shortcut\": null", StringComparison.Ordinal);
        AssertEx.Throws<InvalidDataException>(
            () => CompanionConfigurationJson.DeserializeAndResolve(Encoding.UTF8.GetBytes(missingShortcut), CreateManifest()));
    }

    private static async ValueTask VerifyConfigurationWriteAsync()
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"ShinyGo60-Step14-{Guid.NewGuid():N}");
        string configurationPath = Path.Combine(temporaryDirectory, "companion-settings.json");
        CompanionConfiguration configuration = new(
            CompanionConfiguration.CurrentSchemaVersion,
            TransportPreference.Bluetooth,
            [new ShortcutConfiguration("F23", ShortcutActionKind.MomentaryLayer, "Navigation")]);

        try
        {
            await CompanionConfigurationJson.WriteAsync(configurationPath, configuration, CreateManifest());
            ResolvedCompanionConfiguration resolved = await CompanionConfigurationJson.ReadAndResolveAsync(
                configurationPath,
                CreateManifest());

            AssertEx.Equal(TransportPreference.Bluetooth, resolved.TransportPreference);
            AssertEx.Equal("F23", resolved.Shortcuts[0].Gesture.ToString());
            AssertEx.Equal(0, Directory.GetFiles(temporaryDirectory, "*.tmp").Length);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    private static ShortcutKeyEvent F23(ShortcutKeyState state, bool isInjected = false)
    {
        return new ShortcutKeyEvent("F23", ShortcutModifiers.None, state, isInjected);
    }

    private static LayoutManifest CreateManifest()
    {
        return new LayoutManifest(
            LayoutManifest.CurrentSchemaVersion,
            ProtocolVersion.Current,
            "sg60-v1-0123456789abcdef0123456789abcdef",
            new string('a', 64),
            "fixture-revision",
            [new LayerDefinition(0, "Home"), new LayerDefinition(1, "Navigation")],
            DateTimeOffset.UnixEpoch);
    }

    private const string ValidConfiguration = """
        {
          "schemaVersion": 1,
          "transportPreference": "automatic",
          "shortcuts": [
            {
              "shortcut": "F23",
              "action": "momentaryLayer",
              "targetLayer": "Navigation"
            }
          ]
        }
        """;
}
