namespace Pulsar.Contracts.Tests;

/// <summary>
/// The first layer of the file manager's defense (docs/architecture/08, "File manager"): a path from the browser names
/// something inside the server directory, by its plain name, or it is refused before anything touches the disk.
/// </summary>
public sealed class ServerFilePathTests
{
    public static TheoryData<string> Malicious =>
    [
        // Roots, drives, UNC and device paths.
        "/", "/etc/passwd", "/server.cfg", "C:", "C:/Windows", "c:server.cfg", "C:\\Windows\\System32", "Z:/",
        "\\\\server\\share", "//server/share", "\\\\?\\C:\\Windows", "//?/C:/Windows", "\\\\.\\PhysicalDrive0", "//./COM1",
        "\\\\?\\UNC\\server\\share", "\\??\\C:\\",
        // Backslashes are separators on Windows: only forward slashes are.
        "\\", "a\\b", "..\\..\\Windows", "game\\csgo\\cfg",
        // Dot segments and empty segments.
        ".", "..", "./server.cfg", "../server.cfg", "a/..", "a/../b", "a/./b", "a/.", "a//b", "a/", "game/../../x", "...", "....",
        // Alternate data streams.
        "server.cfg:hidden", "server.cfg::$DATA", "a/b:c", "game/csgo:$INDEX_ALLOCATION/x",
        // Reserved device names, with or without an extension, in any case, at any depth.
        "CON", "con", "Con.txt", "CON .txt", "PRN", "prn.log", "AUX", "aux.cfg", "NUL", "nul.tar.gz", "game/NUL",
        "COM1", "com9.txt", "COM0", "LPT1", "lpt9.dat", "LPT0", "COM\u00b9", "LPT\u00b3.txt", "CONIN$", "conout$.log",
        // Names Windows silently changes (trailing dots and spaces) or that hide (leading spaces).
        "server.cfg.", "server.cfg ", " server.cfg", "cfg./server.cfg", "cfg /server.cfg",
        // Control characters and characters Windows does not allow in names.
        "a\0b", "a\nb", "a\rb", "a\tb", "a\u001fb", "a\u007fb", "a\u0085b", "a<b", "a>b", "a\"b", "a|b", "a?b", "a*b",
        // Characters that hide or reorder what a name shows ("invoice" + U+202E + "fdp.exe" reads as "invoiceexe.pdf"): format
        // characters (direction marks and overrides, zero-width spaces, the byte order mark, tag characters) and line separators.
        "invoice\U0000202Efdp.exe", "a\U0000200Bb", "a\U0000200Eb", "a\U0000200Fb", "a\U00002066b", "a\U00002069b", "a\U0000061Cb", "a\U0000FEFFb",
        "a\U00002028b", "a\U00002029b",
        "a\U000E0041b",
        // Broken UTF-16.
        "\ud800", "a/\udc00b", "x\ud83d",
    ];

    public static TheoryData<string> Plain =>
    [
        "server.cfg", "game/csgo/cfg/server.cfg", ".pulsar", ".pulsar/steamcmd", "a b/c d.txt", "file..txt", "a.b.c",
        "\u00dcn\u00efc\u00f6d\u00e9/\u540d\u524d.cfg", "emoji\ud83d\ude00.txt", "COM10", "LPT10", "CONSOLE", "NULL", "auxiliary.cfg",
        "con_settings.cfg", "nul-sink", "$name", "~tmp", "#x", "[brackets]", "{braces}", "a;b", "a=b", "a,b", "a'b", "a&b",
        "a+b", "a@b", "a!b", "a%b", "a`b", "a^b", "PROGRA~1",
    ];

    [Theory]
    [MemberData(nameof(Malicious))]
    public void MaliciousPathsAreRefused(string path)
    {
        Assert.False(ServerFilePath.IsValid(path));
        Assert.False(ServerFilePath.IsValid(path, allowRoot: true));
        Assert.NotNull(ServerFilePath.Error(path));
    }

    [Theory]
    [MemberData(nameof(Plain))]
    public void PlainNamesAreAccepted(string path) => Assert.Null(ServerFilePath.Error(path));

    [Fact]
    public void TheServerDirectoryItselfIsOnlyAcceptedWhereAsked()
    {
        Assert.False(ServerFilePath.IsValid(""));
        Assert.True(ServerFilePath.IsValid("", allowRoot: true));
        Assert.False(ServerFilePath.IsValid(null));
        Assert.False(ServerFilePath.IsValid(null, allowRoot: true));
    }

    [Fact]
    public void LengthsAreBounded()
    {
        Assert.True(ServerFilePath.IsValid(new string('a', ServerFilePath.MaxSegmentLength)));
        Assert.False(ServerFilePath.IsValid(new string('a', ServerFilePath.MaxSegmentLength + 1)));

        var deep = string.Join('/', Enumerable.Repeat("abcdefghi", ServerFilePath.MaxLength / 10));
        Assert.True(ServerFilePath.IsValid(deep));
        Assert.False(ServerFilePath.IsValid(deep + "/abcdefghijk"));
    }

    [Theory]
    [InlineData("server.cfg", "", "server.cfg")]
    [InlineData("game/csgo/cfg/server.cfg", "game/csgo/cfg", "server.cfg")]
    [InlineData("game", "", "game")]
    public void SplitsIntoParentAndName(string path, string parent, string name)
    {
        Assert.Equal(parent, ServerFilePath.Parent(path));
        Assert.Equal(name, ServerFilePath.Name(path));
    }

    [Theory]
    [InlineData("", "server.cfg", "server.cfg")]
    [InlineData("game/csgo", "cfg", "game/csgo/cfg")]
    public void JoinsADirectoryAndAName(string directory, string name, string path) => Assert.Equal(path, ServerFilePath.Join(directory, name));

    [Theory]
    [InlineData("game", "game/csgo")]
    [InlineData("game", "game/csgo/cfg/server.cfg")]
    [InlineData("Game", "game/x")]
    public void KnowsWhatLiesInsideADirectory(string directory, string path) => Assert.True(ServerFilePath.IsInside(path, directory));

    [Theory]
    [InlineData("game", "game")]
    [InlineData("game", "gamecfg/x")]
    [InlineData("game/csgo", "game")]
    public void KnowsWhatDoesNot(string directory, string path) => Assert.False(ServerFilePath.IsInside(path, directory));
}
