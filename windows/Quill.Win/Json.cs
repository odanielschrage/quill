using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Quill;

/// Serialization details that keep Windows output byte-identical to the macOS
/// build's. Downstream tooling and `on_stop` hooks are shared between the two
/// platforms, so the on-disk contract is not a place for idiomatic drift.
internal static class Json
{
    /// Swift writes both transcripts with [.prettyPrinted, .sortedKeys].
    ///
    /// Two things make this match:
    ///   * WriteIndented gives the same two-space indent Foundation uses.
    ///   * System.Text.Json serializes POCO properties in *declaration* order,
    ///     so every serialized type here declares its properties alphabetically
    ///     to stand in for .sortedKeys. Reordering those declarations silently
    ///     breaks parity — see ContractTests.
    ///
    /// UnsafeRelaxedJsonEscaping matters more than its name suggests: the
    /// default encoder escapes every non-ASCII character, so a Portuguese
    /// transcript would come out as ã soup while the Swift build writes
    /// plain UTF-8. "Unsafe" refers to HTML-injection contexts, which a
    /// transcript file on disk is not.
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// Foundation writes UTF-8 without a BOM. .NET's default for
    /// File.WriteAllText does too, but a stray BOM would corrupt byte-level
    /// parity, so it's stated rather than assumed.
    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// The shape Foundation's ISO8601DateFormatter emits by default: UTC, no
    /// fractional seconds, literal Z. Not "o" — that adds both.
    public static string Iso8601(DateTimeOffset when) =>
        when.UtcDateTime.ToString(
            "yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture);

    /// Temp file in the destination directory, then rename over the target.
    /// A partially written transcript never exists on disk, which is what lets
    /// the coordinator treat "transcript.json exists" as "this session is done".
    public static void WriteAtomic(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path)!;
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, contents, Utf8NoBom);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best effort */ }
            throw;
        }
    }
}
