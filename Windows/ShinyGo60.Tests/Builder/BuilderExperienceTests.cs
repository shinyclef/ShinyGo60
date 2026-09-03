using System.ComponentModel;
using ShinyGo60.Builder.Core.Build;
using ShinyGo60.Builder.Core.Keymaps;
using ShinyGo60.Builder.Core.Processes;
using ShinyGo60.Builder.Core.Workspaces;
using ShinyGo60.Tests.Fakes;
using ShinyGo60.Tests.Testing;

namespace ShinyGo60.Tests.Builder;

internal static class BuilderExperienceTests
{
    public static async ValueTask RunAsync()
    {
        string temporaryRoot = Path.Combine(Path.GetTempPath(), $"ShinyGo60 Step15 {Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temporaryRoot);
            VerifyInputDiscovery(Path.Combine(temporaryRoot, "input discovery"));
            VerifyInstallationDiscovery(Path.Combine(temporaryRoot, "installation discovery"));
            await VerifyPrerequisiteStatesAsync(Path.Combine(temporaryRoot, "prerequisites"));
            await VerifyScopedCleanupAsync(Path.Combine(temporaryRoot, "cleanup"));
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    private static void VerifyInputDiscovery(string caseRoot)
    {
        string inputDirectory = Path.Combine(caseRoot, "Input");
        Directory.CreateDirectory(inputDirectory);
        File.WriteAllText(Path.Combine(inputDirectory, "zeta.KEYMAP"), "fixture");
        File.WriteAllText(Path.Combine(inputDirectory, "Alpha.keymap"), "fixture");
        File.WriteAllText(Path.Combine(inputDirectory, "notes.txt"), "not a keymap");
        Directory.CreateDirectory(Path.Combine(inputDirectory, "nested"));
        File.WriteAllText(Path.Combine(inputDirectory, "nested", "ignored.keymap"), "fixture");

        IReadOnlyList<string> candidates = KeymapInputFinder.FindCandidates(inputDirectory);
        AssertEx.Equal(2, candidates.Count);
        AssertEx.Equal("Alpha.keymap", Path.GetFileName(candidates[0]));
        AssertEx.Equal("zeta.KEYMAP", Path.GetFileName(candidates[1]));
        AssertEx.Equal(candidates[0], KeymapInputFinder.ValidateSelection(candidates[0]));
        AssertEx.Throws<InvalidDataException>(() => KeymapInputFinder.ValidateSelection(Path.Combine(inputDirectory, "notes.txt")));
    }

    private static void VerifyInstallationDiscovery(string caseRoot)
    {
        string templateFile = Path.Combine(
            caseRoot,
            "Custom Firmware",
            "BuildSupport",
            "Templates",
            "v25.11",
            "config",
            "default.nix");
        string moduleFile = Path.Combine(caseRoot, "Custom Firmware", "Module", "zephyr", "module.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(templateFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(moduleFile)!);
        File.WriteAllText(templateFile, "fixture");
        File.WriteAllText(moduleFile, "fixture");
        string nestedDirectory = Path.Combine(caseRoot, "Windows", "bin", "Release");
        Directory.CreateDirectory(nestedDirectory);

        AssertEx.Equal(Path.GetFullPath(caseRoot), BuilderInstallationLocator.FindRoot(nestedDirectory));
    }

    private static async ValueTask VerifyPrerequisiteStatesAsync(string caseRoot)
    {
        BuildWorkspaceLayout workspace = BuildWorkspaceLayout.FromRepositoryRoot(caseRoot);
        const long availableBytes = 12L * 1024L * 1024L * 1024L;
        const long imageBytes = 4_456_000_000L;
        FakeProcessRunner readyRunner = new()
        {
            Handler = (invocation, _) => ValueTask.FromResult(
                invocation.Arguments[0] == "info"
                    ? new ProcessResult(0, "29.0.0", string.Empty)
                    : new ProcessResult(
                        0,
                        $"true|firmware-builder|{PinnedFirmwareBuild.FirmwareTag}|" +
                            $"{PinnedFirmwareBuild.FirmwareRevision}|sha256:fixture|{imageBytes}",
                        string.Empty)),
        };
        FirmwareBuildPrerequisiteChecker readyChecker = new(readyRunner, _ => availableBytes);
        FirmwareBuildReadinessResult ready = await readyChecker.CheckAsync(workspace);
        AssertEx.Equal(FirmwareBuildReadiness.Ready, ready.Readiness);
        AssertEx.True(ready.CanBuild, "A matching image and sufficient working space should be ready.");
        AssertEx.Equal(imageBytes, ready.InstalledImageBytes);

        FakeProcessRunner stoppedRunner = new()
        {
            Result = new ProcessResult(1, string.Empty, "engine unavailable"),
        };
        FirmwareBuildReadinessResult stopped = await new FirmwareBuildPrerequisiteChecker(
            stoppedRunner,
            _ => availableBytes).CheckAsync(workspace);
        AssertEx.Equal(FirmwareBuildReadiness.DockerDesktopStopped, stopped.Readiness);

        FakeProcessRunner missingImageRunner = new()
        {
            Handler = (invocation, _) => ValueTask.FromResult(
                invocation.Arguments[0] == "info"
                    ? new ProcessResult(0, "29.0.0", string.Empty)
                    : new ProcessResult(1, string.Empty, "No such image")),
        };
        FirmwareBuildReadinessResult missingImage = await new FirmwareBuildPrerequisiteChecker(
            missingImageRunner,
            _ => availableBytes).CheckAsync(workspace);
        AssertEx.Equal(FirmwareBuildReadiness.BuildImageMissing, missingImage.Readiness);

        FirmwareBuildReadinessResult lowSpace = await new FirmwareBuildPrerequisiteChecker(
            readyRunner,
            _ => FirmwareBuildPrerequisiteChecker.MinimumWorkingSpaceBytes - 1).CheckAsync(workspace);
        AssertEx.Equal(FirmwareBuildReadiness.InsufficientWorkingSpace, lowSpace.Readiness);

        FakeProcessRunner noCommandRunner = new()
        {
            Handler = (_, _) => throw new Win32Exception("docker not found"),
        };
        FirmwareBuildReadinessResult noCommand = await new FirmwareBuildPrerequisiteChecker(
            noCommandRunner,
            _ => availableBytes).CheckAsync(workspace);
        AssertEx.Equal(FirmwareBuildReadiness.DockerCommandMissing, noCommand.Readiness);
    }

    private static async ValueTask VerifyScopedCleanupAsync(string caseRoot)
    {
        BuildWorkspaceLayout workspace = BuildWorkspaceLayout.FromRepositoryRoot(caseRoot);
        string managedWorkspace = Path.Combine(workspace.GeneratedDirectory, $"build-{Guid.NewGuid():N}");
        string unrelatedWorkspace = Path.Combine(workspace.GeneratedDirectory, "build-not-owned");
        string managedStage = Path.Combine(workspace.OutputDirectory, $".shinygo60-stage-{Guid.NewGuid():N}");
        string successfulOutput = Path.Combine(workspace.OutputDirectory, "ShinyGo60-known-good");
        Directory.CreateDirectory(managedWorkspace);
        Directory.CreateDirectory(unrelatedWorkspace);
        Directory.CreateDirectory(managedStage);
        Directory.CreateDirectory(successfulOutput);
        File.WriteAllText(Path.Combine(managedWorkspace, "temporary.txt"), "temporary");
        File.WriteAllText(Path.Combine(managedStage, "partial.uf2"), "partial");
        File.WriteAllText(Path.Combine(successfulOutput, "known.uf2"), "known good");

        FakeProcessRunner runner = new()
        {
            Result = new ProcessResult(0, string.Empty, string.Empty),
        };
        ManagedBuildCacheCleanupResult result = await new ManagedBuildCacheCleaner(runner).CleanAsync(workspace);

        AssertEx.Equal(1, result.RemovedWorkspaceCount);
        AssertEx.Equal(1, result.RemovedOutputStageCount);
        AssertEx.True(result.RemovedConstructionCache, "The exact isolated Buildx cache should be removed when present.");
        AssertEx.True(!Directory.Exists(managedWorkspace), "The GUID-named workspace should be removed.");
        AssertEx.True(!Directory.Exists(managedStage), "The GUID-named incomplete output stage should be removed.");
        AssertEx.True(Directory.Exists(unrelatedWorkspace), "An unrecognized directory must be preserved.");
        AssertEx.True(Directory.Exists(successfulOutput), "Successful output sets must be preserved.");
        AssertEx.Equal(2, runner.Invocations.Count);
        AssertEx.Equal("inspect", runner.Invocations[0].Arguments[1]);
        AssertEx.Equal("rm", runner.Invocations[1].Arguments[1]);
    }
}
