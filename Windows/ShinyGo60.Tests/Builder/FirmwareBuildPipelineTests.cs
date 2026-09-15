using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ShinyGo60.Builder.Core.Build;
using ShinyGo60.Builder.Core.Processes;
using ShinyGo60.Protocol.Manifests;
using ShinyGo60.Tests.Fakes;
using ShinyGo60.Tests.Testing;

namespace ShinyGo60.Tests.Builder;

internal static class FirmwareBuildPipelineTests
{
    private const uint FirstMagic = 0x0A324655;
    private const uint SecondMagic = 0x9E5D5157;
    private const uint FinalMagic = 0x0AB16F30;

    public static async ValueTask RunAsync()
    {
        string temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            $"ShinyGo60 Step8 生成 path {Guid.NewGuid():N}",
            new string('x', 48));

        try
        {
            Directory.CreateDirectory(temporaryRoot);
            await VerifySuccessfulAndRepeatedBuildsAsync(Path.Combine(temporaryRoot, "success case"));
            await VerifyCompilerFailureAsync(Path.Combine(temporaryRoot, "compiler failure"));
            await VerifyMissingOutputFailureAsync(Path.Combine(temporaryRoot, "stale output guard"));
            await VerifyUnexpectedImageMetadataFailureAsync(Path.Combine(temporaryRoot, "wrong image guard"));
            await VerifyCancellationCleanupAsync(Path.Combine(temporaryRoot, "cancel case"));
            VerifyOutputRetention(Path.Combine(temporaryRoot, "rotation"));
            VerifyPublicationRollback(Path.Combine(temporaryRoot, "rollback"));
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    private static async ValueTask VerifySuccessfulAndRepeatedBuildsAsync(string caseRoot)
    {
        FakeProcessRunner runner = new()
        {
            Handler = HandleSuccessfulDockerAsync,
        };
        FirmwareBuildPipeline pipeline = new(runner);
        FirmwareBuildRequest request = CreateRequest(caseRoot);
        RecordingProgress progress = new();

        FirmwareBuildResult first = await pipeline.BuildAsync(request, progress);
        FirmwareBuildResult second = await pipeline.BuildAsync(request);

        AssertEx.True(File.Exists(first.Uf2Path), "A successful build should publish its UF2.");
        AssertEx.True(File.Exists(first.ManifestPath), "A successful build should publish its manifest.");
        AssertEx.True(File.Exists(first.LogPath), "A successful build should publish its log.");
        AssertEx.True(
            string.Equals(request.OutputDirectory, second.OutputSetDirectory, StringComparison.OrdinalIgnoreCase),
            "The newest firmware should always be published in Output itself.");

        LayoutManifest manifest = await LayoutManifestJson.ReadAsync(first.ManifestPath);
        AssertEx.Equal(first.LayoutIdentifier, manifest.LayoutIdentifier);
        AssertEx.Equal(first.KeymapSha256, manifest.KeymapSha256);
        AssertEx.True(new FileInfo(first.Uf2Path).Length > 0, "The published UF2 should not be empty.");
        AssertEx.True(
            (await File.ReadAllTextAsync(first.LogPath)).Contains("Status: succeeded", StringComparison.Ordinal),
            "The matched build log should record success.");
        AssertEx.Equal(1, Directory.GetDirectories(request.OutputDirectory, "Firmware-*").Length);
        AssertEx.Equal(0, Directory.GetDirectories(request.GeneratedWorkspaceDirectory, "build-*").Length);

        ProcessInvocation dockerRun = runner.Invocations.Find(invocation => invocation.Arguments[0] == "run")
            ?? throw new InvalidOperationException("The pipeline did not invoke docker run.");
        int networkIndex = FindArgumentIndex(dockerRun.Arguments, "--network");
        AssertEx.Equal("none", dockerRun.Arguments[networkIndex + 1]);
        AssertEx.True(
            dockerRun.Arguments.Any(argument => argument.Contains("生成 path", StringComparison.Ordinal)),
            "Docker should receive Unicode workspace paths as one unsplit argument.");
        AssertEx.True(
            progress.Stages.SequenceEqual(Enum.GetValues<FirmwareBuildStage>()),
            "The graphical builder should receive every build stage in order.");
    }

    private static async ValueTask VerifyCompilerFailureAsync(string caseRoot)
    {
        FakeProcessRunner runner = new()
        {
            Handler = (invocation, _) => ValueTask.FromResult(
                invocation.Arguments[0] == "image"
                    ? SuccessfulImageInspection()
                    : new ProcessResult(1, string.Empty, "compiler error")),
        };
        FirmwareBuildRequest request = CreateRequest(caseRoot);
        FirmwareBuildPipeline pipeline = new(runner);

        FirmwareBuildException exception = await AssertEx.ThrowsAsync<FirmwareBuildException>(async () =>
        {
            await pipeline.BuildAsync(request);
        });

        AssertEx.True(exception.FailureLogPath is not null, "Compiler failures should retain a clearly named failure log.");
        AssertEx.True(File.Exists(exception.FailureLogPath), "The failure log should exist.");
        AssertEx.True(!Directory.Exists(Path.Combine(request.OutputDirectory, "Failures")), "Failure logs should stay outside firmware output.");
        AssertEx.Equal(0, Directory.GetDirectories(request.OutputDirectory, "ShinyGo60-*").Length);
        AssertEx.Equal(0, Directory.GetDirectories(request.GeneratedWorkspaceDirectory, "build-*").Length);
    }

    private static void VerifyOutputRetention(string root)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "personal-notes.txt"), "keep");
        Directory.CreateDirectory(Path.Combine(root, "Firmware-unmanaged"));
        DateTimeOffset created = new(2026, 9, 10, 1, 2, 3, TimeSpan.FromHours(9));
        for (int index = 0; index < 7; index++)
        {
            string stage = CreateOutputStage(root, index.ToString(CultureInfo.InvariantCulture));
            FirmwareOutputStore.Publish(stage, root, created.AddHours(index), Guid.NewGuid().ToString("N"));
            if (index == 4)
            {
                string oldest = Directory.GetDirectories(root, "Firmware-*").Single(path =>
                    File.Exists(Path.Combine(path, FirmwareOutputStore.Uf2FileName)) &&
                    File.ReadAllText(Path.Combine(path, FirmwareOutputStore.Uf2FileName)) == "0");
                foreach (string file in Directory.GetFiles(oldest))
                {
                    File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
                }

                File.SetAttributes(oldest, File.GetAttributes(oldest) | FileAttributes.ReadOnly);
            }
        }

        AssertEx.Equal("6", File.ReadAllText(Path.Combine(root, FirmwareOutputStore.Uf2FileName)));
        string[] archives = Directory.GetDirectories(root, "Firmware-*")
            .Where(path => File.Exists(Path.Combine(path, FirmwareOutputStore.MetadataFileName))).ToArray();
        AssertEx.Equal(4, archives.Length);
        string[] retained = archives.Select(path => File.ReadAllText(Path.Combine(path, FirmwareOutputStore.Uf2FileName)))
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();
        AssertEx.Equal("2,3,4,5", string.Join(',', retained));
        foreach (string archive in archives)
        {
            string value = File.ReadAllText(Path.Combine(archive, FirmwareOutputStore.Uf2FileName));
            AssertEx.Equal(value, File.ReadAllText(Path.Combine(archive, "layout-manifest.json")));
            AssertEx.Equal(value, File.ReadAllText(Path.Combine(archive, "build.log")));
            FirmwareOutputMetadata metadata = JsonSerializer.Deserialize<FirmwareOutputMetadata>(
                File.ReadAllText(Path.Combine(archive, FirmwareOutputStore.MetadataFileName)))!;
            AssertEx.Equal(created.AddHours(int.Parse(value, CultureInfo.InvariantCulture)), metadata.CreatedAt);
            string timestamp = metadata.CreatedAt.ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture);
            AssertEx.True(Path.GetFileName(archive).Contains(timestamp, StringComparison.Ordinal),
                "Archives should use the build's creation date, not its later rotation date.");
        }

        AssertEx.Equal("keep", File.ReadAllText(Path.Combine(root, "personal-notes.txt")));
        AssertEx.True(Directory.Exists(Path.Combine(root, "Firmware-unmanaged")), "Unmarked directories must not be pruned.");
        AssertEx.True(!File.Exists(Path.Combine(root, ".shinygo60-output.lock")), "Publishing must remove its lock file.");
    }

    private static void VerifyPublicationRollback(string root)
    {
        Directory.CreateDirectory(root);
        DateTimeOffset created = DateTimeOffset.UtcNow;
        FirmwareOutputStore.Publish(CreateOutputStage(root, "previous"), root, created, Guid.NewGuid().ToString("N"));
        string stage = CreateOutputStage(root, "next");
        // The UF2 move succeeds first; locking the manifest exercises rollback of an already moved file.
        using (FileStream heldFile = new(Path.Combine(root, "layout-manifest.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            AssertEx.Throws<IOException>(() => FirmwareOutputStore.Publish(stage, root, created.AddMinutes(1), Guid.NewGuid().ToString("N")));
        }

        AssertEx.Equal("previous", File.ReadAllText(Path.Combine(root, FirmwareOutputStore.Uf2FileName)));
        AssertEx.Equal("previous", File.ReadAllText(Path.Combine(root, "layout-manifest.json")));
        AssertEx.Equal("next", File.ReadAllText(Path.Combine(stage, FirmwareOutputStore.Uf2FileName)));
        AssertEx.Equal(0, Directory.GetDirectories(root, "Firmware-*").Length);
        using (FileStream heldFile = new(Path.Combine(stage, "build.log"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            AssertEx.Throws<IOException>(() => FirmwareOutputStore.Publish(stage, root, created.AddMinutes(1), Guid.NewGuid().ToString("N")));
        }

        AssertEx.Equal("previous", File.ReadAllText(Path.Combine(root, FirmwareOutputStore.Uf2FileName)));
        AssertEx.Equal("previous", File.ReadAllText(Path.Combine(root, "build.log")));
        AssertEx.Equal("next", File.ReadAllText(Path.Combine(stage, "layout-manifest.json")));
        AssertEx.Equal(0, Directory.GetDirectories(root, "Firmware-*").Length);
        FirmwareOutputStore.Publish(stage, root, created, Guid.NewGuid().ToString("N"));
        FirmwareOutputStore.Publish(CreateOutputStage(root, "same timestamp"), root, created, Guid.NewGuid().ToString("N"));
        AssertEx.Equal(2, Directory.GetDirectories(root, "Firmware-*").Length);
    }

    private static string CreateOutputStage(string root, string value)
    {
        string stage = Path.Combine(root, ".shinygo60-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        foreach (string name in new[] { FirmwareOutputStore.Uf2FileName, "layout-manifest.json", "build.log" })
        {
            File.WriteAllText(Path.Combine(stage, name), value);
        }

        return stage;
    }

    private static async ValueTask VerifyMissingOutputFailureAsync(string caseRoot)
    {
        FakeProcessRunner runner = new()
        {
            Handler = (invocation, _) => ValueTask.FromResult(
                invocation.Arguments[0] == "image"
                    ? SuccessfulImageInspection()
                    : new ProcessResult(0, "claimed success", string.Empty)),
        };
        FirmwareBuildRequest request = CreateRequest(caseRoot);
        Directory.CreateDirectory(request.OutputDirectory);
        string existingSet = Path.Combine(request.OutputDirectory, "Existing-known-good");
        Directory.CreateDirectory(existingSet);
        string existingUf2 = Path.Combine(existingSet, "known.uf2");
        await File.WriteAllTextAsync(existingUf2, "known output");

        FirmwareBuildPipeline pipeline = new(runner);
        FirmwareBuildException exception = await AssertEx.ThrowsAsync<FirmwareBuildException>(async () =>
        {
            await pipeline.BuildAsync(request);
        });

        AssertEx.True(
            exception.Message.Contains("did not produce go60.uf2", StringComparison.Ordinal),
            "A false compiler success should identify the missing fresh UF2.");
        AssertEx.True(File.Exists(existingUf2), "A failed build must not alter an existing output set.");
        AssertEx.Equal(0, Directory.GetDirectories(request.OutputDirectory, "ShinyGo60-*").Length);
    }

    private static async ValueTask VerifyCancellationCleanupAsync(string caseRoot)
    {
        FakeProcessRunner runner = new()
        {
            Handler = (invocation, cancellationToken) =>
            {
                string command = invocation.Arguments[0];
                if (command == "image")
                {
                    return ValueTask.FromResult(SuccessfulImageInspection());
                }

                if (command == "run")
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                return ValueTask.FromResult(new ProcessResult(0, string.Empty, string.Empty));
            },
        };
        FirmwareBuildRequest request = CreateRequest(caseRoot);
        FirmwareBuildPipeline pipeline = new(runner);

        await AssertEx.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await pipeline.BuildAsync(request);
        });

        AssertEx.True(
            runner.Invocations.Any(invocation => invocation.Arguments[0] == "rm"),
            "Cancellation should request removal of the exact managed build container.");
        AssertEx.Equal(0, Directory.GetDirectories(request.OutputDirectory, "ShinyGo60-*").Length);
        AssertEx.Equal(0, Directory.GetDirectories(request.GeneratedWorkspaceDirectory, "build-*").Length);
    }

    private static async ValueTask VerifyUnexpectedImageMetadataFailureAsync(string caseRoot)
    {
        FakeProcessRunner runner = new()
        {
            Handler = (_, _) => ValueTask.FromResult(
                new ProcessResult(
                    0,
                    $"true|unmanaged-role|{PinnedFirmwareBuild.FirmwareTag}|{PinnedFirmwareBuild.FirmwareRevision}|sha256:fixture",
                    string.Empty)),
        };
        FirmwareBuildRequest request = CreateRequest(caseRoot);
        FirmwareBuildPipeline pipeline = new(runner);

        FirmwareBuildException exception = await AssertEx.ThrowsAsync<FirmwareBuildException>(async () =>
        {
            await pipeline.BuildAsync(request);
        });

        AssertEx.True(
            exception.Message.Contains(PinnedFirmwareBuild.FirmwareRevision, StringComparison.Ordinal),
            "An image with substituted metadata should report the firmware revision that was expected.");
        AssertEx.Equal(1, runner.Invocations.Count);
        AssertEx.Equal(0, Directory.GetDirectories(request.OutputDirectory, "ShinyGo60-*").Length);
    }

    private static FirmwareBuildRequest CreateRequest(string caseRoot)
    {
        string repositoryRoot = FindRepositoryRoot();
        string inputDirectory = Path.Combine(caseRoot, "入力 files with spaces");
        Directory.CreateDirectory(inputDirectory);
        string keymapPath = Path.Combine(inputDirectory, "My 長い layout.keymap");
        File.Copy(FixturePath("LayerOrderA.keymap"), keymapPath, overwrite: true);

        return PinnedFirmwareBuild.CreateRequest(
            repositoryRoot,
            keymapPath,
            Path.Combine(caseRoot, "generated workspaces"),
            Path.Combine(caseRoot, "published outputs"));
    }

    private static ValueTask<ProcessResult> HandleSuccessfulDockerAsync(
        ProcessInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string command = invocation.Arguments[0];
        if (command == "image")
        {
            return ValueTask.FromResult(SuccessfulImageInspection());
        }

        if (command != "run")
        {
            return ValueTask.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }

        string workspace = ReadConfigMountSource(invocation.Arguments);
        string configuration = File.ReadAllText(Path.Combine(workspace, "config", "go60.conf"));
        string layoutIdentifier = ReadConfigurationValue(configuration, "CONFIG_SHINYGO60_LAYOUT_IDENTIFIER");
        string keymapSha256 = ReadConfigurationValue(configuration, "CONFIG_SHINYGO60_KEYMAP_SHA256");
        File.WriteAllBytes(
            Path.Combine(workspace, "go60.uf2"),
            CreateCombinedUf2($"{layoutIdentifier}\0{keymapSha256}\0"));
        return ValueTask.FromResult(new ProcessResult(0, "firmware built", string.Empty, TimeSpan.FromSeconds(1)));
    }

    private static ProcessResult SuccessfulImageInspection()
    {
        return new ProcessResult(
            0,
            $"true|firmware-builder|{PinnedFirmwareBuild.FirmwareTag}|{PinnedFirmwareBuild.FirmwareRevision}|sha256:fixture",
            string.Empty,
            TimeSpan.FromMilliseconds(10));
    }

    private static string ReadConfigMountSource(IReadOnlyList<string> arguments)
    {
        const string prefix = "type=bind,source=";
        const string suffix = ",target=/config";
        string mount = arguments.First(argument =>
            argument.StartsWith(prefix, StringComparison.Ordinal) && argument.EndsWith(suffix, StringComparison.Ordinal));
        return mount[prefix.Length..^suffix.Length];
    }

    private static int FindArgumentIndex(IReadOnlyList<string> arguments, string value)
    {
        for (int index = 0; index < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], value, StringComparison.Ordinal))
            {
                return index;
            }
        }

        throw new InvalidOperationException($"Docker argument '{value}' was not found.");
    }

    private static string ReadConfigurationValue(string configuration, string name)
    {
        string prefix = name + "=\"";
        string line = configuration.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Last(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal));
        return line[prefix.Length..^1];
    }

    private static byte[] CreateCombinedUf2(string leftPayloadText)
    {
        byte[] leftPayload = Encoding.UTF8.GetBytes(leftPayloadText);
        byte[] rightPayload = "right-half"u8.ToArray();
        byte[] combined = new byte[1024];
        WriteUf2Block(combined.AsSpan(0, 512), leftPayload);
        WriteUf2Block(combined.AsSpan(512, 512), rightPayload);
        return combined;
    }

    private static void WriteUf2Block(Span<byte> block, ReadOnlySpan<byte> payload)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(block, FirstMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(block[4..], SecondMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(block[16..], checked((uint)payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(block[20..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(block[24..], 1);
        payload.CopyTo(block[32..]);
        BinaryPrimitives.WriteUInt32LittleEndian(block[508..], FinalMagic);
    }

    private static string FixturePath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", "Keymaps", fileName);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DEVELOPMENT_PLAN.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find the ShinyGo60 repository root.");
    }

    private sealed class RecordingProgress : IProgress<FirmwareBuildProgress>
    {
        public List<FirmwareBuildStage> Stages { get; } = [];

        public void Report(FirmwareBuildProgress value)
        {
            this.Stages.Add(value.Stage);
        }
    }
}
