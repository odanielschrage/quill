using Xunit;

namespace Quill.Tests;

public sealed class DoctorTests : IDisposable
{
    private readonly string _root = Temp.Dir();
    private readonly string? _previousConfig =
        Environment.GetEnvironmentVariable(Config.PathVariable);

    public DoctorTests() => UseConfig(null);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Config.PathVariable, _previousConfig);
        Temp.Nuke(_root);
    }

    [Fact]
    public void WritableFolderPasses()
    {
        var check = DoctorReport.CheckRecordingsRoot(Path.Combine(_root, "Recordings"));

        Assert.Equal(CheckLevel.Ok, check.Level);
        Assert.True(Directory.Exists(Path.Combine(_root, "Recordings")));
    }

    [Fact]
    public void UncreatableFolderFailsWithRemediation()
    {
        // A path under a file rather than a directory can never be created.
        var file = Path.Combine(_root, "a-file");
        File.WriteAllText(file, "");

        var check = DoctorReport.CheckRecordingsRoot(Path.Combine(file, "Recordings"));

        Assert.Equal(CheckLevel.Fail, check.Level);
        Assert.NotNull(check.Remediation);
    }

    [Fact]
    public void TranscriptionDisabledIsAWarningNotAFailure()
    {
        UseConfig("""{ "transcription": { "enabled": false } }""");

        var check = DoctorReport.CheckTranscription();

        // Recording-only is a legitimate configuration, so it must not block
        // startup.
        Assert.Equal(CheckLevel.Warn, check.Level);
        Assert.Contains("disabled", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingModelIsAWarningSoARecordingStillHappens()
    {
        UseConfig("""{ "transcription": { "model": "large-v3" } }""");

        var check = DoctorReport.CheckTranscription();

        // large-v3 isn't downloaded here; the point is that a missing model must
        // never stop quill from capturing a meeting that is starting now.
        Assert.NotEqual(CheckLevel.Fail, check.Level);
    }

    [Fact]
    public void WarningsDoNotBlockStartupButFailuresDo()
    {
        Assert.True(DoctorReport.AllOk([
            new Check("a", CheckLevel.Ok, "fine"),
            new Check("b", CheckLevel.Warn, "later"),
        ]));

        Assert.False(DoctorReport.AllOk([
            new Check("a", CheckLevel.Ok, "fine"),
            new Check("b", CheckLevel.Fail, "broken"),
        ]));
    }

    /// These read real hardware and the real registry, so the outcome depends on
    /// the machine. What must hold everywhere is that they answer rather than
    /// throw — `doctor` is what a user runs when things are already wrong.
    [Fact]
    public void EnvironmentChecksNeverThrow()
    {
        var checks = DoctorReport.Run(Path.Combine(_root, "Recordings"));

        Assert.Equal(5, checks.Count);
        Assert.All(checks, check =>
        {
            Assert.False(string.IsNullOrWhiteSpace(check.Name));
            Assert.False(string.IsNullOrWhiteSpace(check.Detail));
        });
    }

    /// Every failure has to tell the user what to do about it.
    [Fact]
    public void FailuresAlwaysCarryRemediation()
    {
        var file = Path.Combine(_root, "blocker");
        File.WriteAllText(file, "");

        var checks = DoctorReport.Run(Path.Combine(file, "Recordings"));

        Assert.All(
            checks.Where(c => c.Level == CheckLevel.Fail),
            check => Assert.NotNull(check.Remediation));
    }

    private void UseConfig(string? json)
    {
        var path = Path.Combine(_root, "config.json");
        if (json is null)
        {
            if (File.Exists(path)) File.Delete(path);
        }
        else
        {
            File.WriteAllText(path, json);
        }
        Environment.SetEnvironmentVariable(Config.PathVariable, path);
    }
}
