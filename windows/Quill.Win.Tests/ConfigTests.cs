using Xunit;

namespace Quill.Tests;

public sealed class ConfigTests : IDisposable
{
    private readonly string _root = Temp.Dir();
    private readonly string? _previousConfig =
        Environment.GetEnvironmentVariable(Config.PathVariable);

    public ConfigTests() => UseConfig(null);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Config.PathVariable, _previousConfig);
        Temp.Nuke(_root);
    }

    [Fact]
    public void DefaultsApplyWithNoConfigFile()
    {
        Assert.True(Config.TranscriptionEnabled());
        Assert.Equal("whisper", Config.TranscriptionEngine());
        Assert.Equal("small", Config.TranscriptionModel());
        Assert.Equal("auto", Config.TranscriptionLanguage());
        Assert.False(Config.MicVoiceProcessing());
        Assert.Null(Config.OnStop());
        Assert.Equal(Config.DefaultRoot, Config.ResolveRoot(null));
    }

    [Fact]
    public void ConfigValuesAreRead()
    {
        UseConfig("""
        {
          "recordings_dir": "~/Reunioes",
          "transcription": { "engine": "whisper", "model": "medium", "language": "pt" },
          "mic_voice_processing": true,
          "on_stop": "python summarize.py"
        }
        """);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, "Reunioes"), Config.ResolveRoot(null));
        Assert.Equal("medium", Config.TranscriptionModel());
        Assert.Equal("pt", Config.TranscriptionLanguage());
        Assert.True(Config.MicVoiceProcessing());
        Assert.Equal("python summarize.py", Config.OnStop());
    }

    [Fact]
    public void CliOverrideBeatsConfig()
    {
        UseConfig("""{ "recordings_dir": "C:\\FromConfig" }""");
        Assert.Equal(@"C:\FromCli", Config.ResolveRoot(@"C:\FromCli"));
    }

    [Fact]
    public void MalformedConfigFallsBackToDefaults()
    {
        UseConfig("{ this is not json");

        var warnings = new StringWriter();
        var previous = Console.Error;
        Console.SetError(warnings);
        try
        {
            Assert.Equal(Config.DefaultRoot, Config.ResolveRoot(null));
        }
        finally
        {
            Console.SetError(previous);
        }

        // Recordings landing somewhere unexpected is worse than a warning.
        Assert.Contains("is not valid JSON", warnings.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ValidJsonOfTheWrongShapeAlsoWarns()
    {
        UseConfig("""["not", "an", "object"]""");

        var warnings = new StringWriter();
        var previous = Console.Error;
        Console.SetError(warnings);
        try
        {
            Assert.Equal(Config.DefaultRoot, Config.ResolveRoot(null));
        }
        finally
        {
            Console.SetError(previous);
        }

        Assert.Contains("is not valid JSON", warnings.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("~")]
    [InlineData("~/Recordings")]
    [InlineData(@"~\Recordings")]
    public void TildeExpandsToTheUserProfile(string input)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expected = input == "~" ? home : Path.Combine(home, "Recordings");
        Assert.Equal(expected, Config.ExpandPath(input));
    }

    [Fact]
    public void EnvironmentVariablesExpand()
    {
        // %USERPROFILE% is what a Windows user would reach for instead of ~.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(
            Path.Combine(home, "Recordings"),
            Config.ExpandPath(@"%USERPROFILE%\Recordings"));
    }

    [Fact]
    public void TildeInTheMiddleIsNotTouched()
    {
        // Only a leading ~ is a home reference; ~ is legal inside a path.
        Assert.Equal(@"C:\temp\a~b", Config.ExpandPath(@"C:\temp\a~b"));
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
