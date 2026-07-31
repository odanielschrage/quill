using System.Text.Json.Nodes;
using Xunit;

namespace Quill.Tests;

/// Writing config from the tray means quill now edits a file the user also edits.
/// The cases that matter are the ones where it must not destroy their work.
public sealed class ConfigWriteTests : IDisposable
{
    private readonly string _root = Temp.Dir();
    private readonly string? _previousConfig =
        Environment.GetEnvironmentVariable(Config.PathVariable);
    private readonly string _path;

    public ConfigWriteTests()
    {
        _path = Path.Combine(_root, "config.json");
        Environment.SetEnvironmentVariable(Config.PathVariable, _path);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Config.PathVariable, _previousConfig);
        Temp.Nuke(_root);
    }

    [Fact]
    public void SettingALanguageKeepsEverythingElse()
    {
        File.WriteAllText(_path, """
        {
          "recordings_dir": "D:\\Meetings",
          "on_stop": "python summarise.py",
          "transcription": { "model": "base", "vad": false }
        }
        """);

        Config.SetTranscription("language", "pt");

        Assert.Equal("pt", Config.TranscriptionLanguage());
        Assert.Equal(@"D:\Meetings", Config.ResolveRoot(null));
        Assert.Equal("python summarise.py", Config.OnStop());
        Assert.Equal("base", Config.TranscriptionModel());
        Assert.False(Config.TranscriptionVad());
    }

    [Fact]
    public void SettingALanguageWithNoConfigCreatesOne()
    {
        Config.SetTranscription("language", "es");

        Assert.True(File.Exists(_path));
        Assert.Equal("es", Config.TranscriptionLanguage());
        // Defaults survive for everything untouched.
        Assert.Equal("small", Config.TranscriptionModel());
        Assert.True(Config.TranscriptionEnabled());
    }

    [Fact]
    public void SettingALanguageWithNoTranscriptionBlockCreatesIt()
    {
        File.WriteAllText(_path, """{ "recordings_dir": "~/Recordings" }""");

        Config.SetTranscription("language", "en");

        Assert.Equal("en", Config.TranscriptionLanguage());
        Assert.NotNull(Config.RecordingsDir());
    }

    /// The important one. A config with a stray comma is recoverable by hand;
    /// silently replacing it with a fresh file is not.
    [Fact]
    public void ABrokenConfigIsRefusedRatherThanOverwritten()
    {
        const string broken = "{ \"transcription\": { \"model\": \"medium\",, } ";
        File.WriteAllText(_path, broken);

        var warnings = new StringWriter();
        var previous = Console.Error;
        Console.SetError(warnings);
        try
        {
            Assert.Throws<InvalidOperationException>(() => Config.SetTranscription("language", "pt"));
        }
        finally
        {
            Console.SetError(previous);
        }

        Assert.Equal(broken, File.ReadAllText(_path));
    }

    [Fact]
    public void TheWrittenFileIsValidJsonAndReadableAgain()
    {
        Config.SetTranscription("language", "pt");
        Config.SetTranscription("model", "base");

        var reparsed = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject;

        Assert.NotNull(reparsed);
        Assert.Equal("pt", reparsed!["transcription"]!["language"]!.GetValue<string>());
        Assert.Equal("base", reparsed["transcription"]!["model"]!.GetValue<string>());
    }

    [Fact]
    public void AccentedValuesSurviveTheRoundTrip()
    {
        // The default JSON encoder escapes non-ASCII; a path with an accent must
        // come back as itself, not as an escape sequence.
        Config.Update(root => root["recordings_dir"] = @"D:\Reuniões");

        Assert.Contains("Reuniões", File.ReadAllText(_path), StringComparison.Ordinal);
        Assert.Equal(@"D:\Reuniões", Config.ResolveRoot(null));
    }

    [Fact]
    public void TheStarterConfigIsCreatedAndParses()
    {
        var created = Config.EnsureExists();

        Assert.Equal(_path, created);
        // The template is commented for discoverability, which the parser allows.
        Assert.Contains("//", File.ReadAllText(_path), StringComparison.Ordinal);
        Assert.Equal("auto", Config.TranscriptionLanguage());
        Assert.Equal("small", Config.TranscriptionModel());
    }

    [Fact]
    public void EnsureExistsLeavesAnExistingConfigAlone()
    {
        const string mine = """{ "transcription": { "language": "ja" } }""";
        File.WriteAllText(_path, mine);

        Config.EnsureExists();

        Assert.Equal(mine, File.ReadAllText(_path));
    }

    /// Writes go to the config already in use, not to the native path — creating
    /// a second file there would silently shadow the first.
    [Fact]
    public void WritesLandInTheConfigAlreadyInUse()
    {
        File.WriteAllText(_path, "{}");

        Assert.Equal(_path, Config.PathToWrite());
    }
}
