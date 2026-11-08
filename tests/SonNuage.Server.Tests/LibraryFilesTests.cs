using SonNuage.Server.Library;

namespace SonNuage.Server.Tests;

/// <summary>Ce que le serveur sait de chaque extension : audio, sans perte, lisible partout, type MIME.</summary>
public sealed class LibraryFilesTests
{
    [Theory]
    [InlineData("a.mp3", true, false, true, "audio/mpeg")]
    [InlineData("a.FLAC", true, true, true, "audio/flac")]
    [InlineData("a.wav", true, true, true, "audio/wav")]
    [InlineData("a.ogg", true, false, true, "audio/ogg")]
    [InlineData("a.oga", true, false, false, "audio/ogg")]
    [InlineData("a.opus", true, false, true, "audio/ogg")]
    [InlineData("a.m4a", true, false, true, "audio/mp4")]
    [InlineData("a.alac", true, true, false, "audio/mp4")]
    [InlineData("a.aac", true, false, true, "audio/aac")]
    [InlineData("a.wma", true, false, false, "audio/x-ms-wma")]
    [InlineData("a.aiff", true, true, false, "audio/aiff")]
    [InlineData("a.aif", true, true, false, "audio/aiff")]
    [InlineData("a.ape", true, true, false, "application/octet-stream")]
    [InlineData("a.wv", true, true, false, "application/octet-stream")]
    [InlineData("a.jpg", false, false, false, "image/jpeg")]
    [InlineData("a.jpeg", false, false, false, "image/jpeg")]
    [InlineData("a.png", false, false, false, "image/png")]
    [InlineData("a.webp", false, false, false, "image/webp")]
    [InlineData("a.txt", false, false, false, "application/octet-stream")]
    [InlineData("noext", false, false, false, "application/octet-stream")]
    public void Each_extension_has_one_description(string path, bool audio, bool lossless, bool playable, string contentType)
    {
        Assert.Equal(audio, LibraryFiles.IsAudio(path));
        Assert.Equal(lossless, LibraryFiles.IsLossless(path));
        Assert.Equal(playable, LibraryFiles.IsDirectlyPlayable(LibraryFiles.Format(path)));
        Assert.Equal(contentType, LibraryFiles.ContentType(path));
        Assert.Equal(path.EndsWith(".jpg") || path.EndsWith(".jpeg") || path.EndsWith(".png") || path.EndsWith(".webp"), LibraryFiles.IsImage(path));
    }
}
