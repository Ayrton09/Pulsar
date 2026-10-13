using System.Text;
using Pulsar.Agent.Core.Processes;

namespace Pulsar.Agent.Core.Servers;

/// <summary>
/// The game files the agent edits and writes back as UTF-8 (configuration files, the lines addons add). One in another
/// encoding (a server.cfg saved as Windows-1252, or as UTF-16) is refused rather than rewritten: its other characters
/// would become U+FFFD, and the user's own lines must stay as they are. A byte order mark never picks the encoding; a
/// UTF-8 one is left out of the text (the file is written back without it).
/// </summary>
internal static class Utf8Text
{
    private static readonly UTF8Encoding s_strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <exception cref="GameProcessException"><c>config.invalid</c>: the file is not UTF-8 text.</exception>
    public static string Read(Stream stream, string path)
    {
        string text;
        try
        {
            // The bytes of a UTF-16 or UTF-32 byte order mark are never valid UTF-8: such a file is refused here.
            using var reader = new StreamReader(stream, s_strict, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            text = reader.ReadToEnd();
        }
        catch (DecoderFallbackException ex)
        {
            throw new GameProcessException("config.invalid", $"{path} is not UTF-8 text, so Pulsar does not change it: save it as UTF-8.", ex);
        }

        return text.StartsWith('﻿') ? text[1..] : text;
    }
}
