using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using SonNuage.Contracts;
using SonNuage.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SonNuage.Server.Tests;

/// <summary>Un morceau envoyé (dans uploads/, donc réinscriptible) à côté de la bibliothèque en lecture seule.</summary>
public sealed class UploadsLibraryFactory : SonNuageFactory
{
    public string UploadedFile => Path.Combine(UploadPath, "Uploader", "Envois", "01 Brouillon.wav");

    public UploadsLibraryFactory() => WriteWav(UploadedFile, seconds: 1);
}

public sealed class TagRewriteTests(UploadsLibraryFactory factory) : IClassFixture<UploadsLibraryFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Track> TrackAsync(string path)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SonNuageDbContext>().Tracks.SingleAsync(t => t.FilePath == path, Ct);
    }

    private static UpdateTrackMetadataRequest Edit(string title) => new(title, "Vrai Artiste", "Vrai Album", "Vrai Artiste", 3, 1, 2024, "Chanson");

    /// <summary>Le bloc « data » d'un WAV : le flux audio, qui ne doit pas changer.</summary>
    private static byte[] AudioData(string wav)
    {
        var bytes = File.ReadAllBytes(wav);
        for (var i = 12; i + 8 <= bytes.Length;)
        {
            var size = BitConverter.ToInt32(bytes, i + 4);
            if (Encoding.ASCII.GetString(bytes, i, 4) == "data") return bytes[(i + 8)..(i + 8 + size)];
            i += 8 + size + (size & 1);
        }
        throw new InvalidDataException("pas de bloc data");
    }

    [Fact]
    public async Task Editing_an_uploaded_track_rewrites_its_tags_without_touching_the_audio()
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        var path = Path.GetFullPath(factory.UploadedFile);
        var audioBefore = AudioData(path);
        var track = await TrackAsync(path);

        (await admin.PutAsJsonAsync($"/api/admin/tracks/{track.Id}/metadata", Edit("Titre corrigé"), Ct)).EnsureSuccessStatusCode();

        using (var file = TagLib.File.Create(path))
        {
            Assert.Equal("Titre corrigé", file.Tag.Title);
            Assert.Equal(["Vrai Artiste"], file.Tag.Performers);
            Assert.Equal("Vrai Album", file.Tag.Album);
            Assert.Equal(3u, file.Tag.Track);
            Assert.Equal(2024u, file.Tag.Year);
        }
        Assert.Equal(audioBefore, AudioData(path));
        // Écriture atomique : aucun fichier temporaire ne reste à côté.
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFullPath));
        // La base connaît la nouvelle taille et la nouvelle date : le scan suivant ne relit pas le fichier pour rien.
        var after = await TrackAsync(path);
        Assert.Equal(new FileInfo(path).Length, after.FileSize);
        Assert.Equal(new FileInfo(path).LastWriteTimeUtc, after.FileModifiedUtc);
    }

    [Fact]
    public async Task Editing_a_track_of_the_read_only_library_changes_only_the_database()
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        var path = Path.GetFullPath(Path.Combine(factory.MusicPath, "Other Band", "Live", "Encore.wav"));
        var hashBefore = SHA256.HashData(File.ReadAllBytes(path));
        var track = await TrackAsync(path);

        (await admin.PutAsJsonAsync($"/api/admin/tracks/{track.Id}/metadata", Edit("Rappel"), Ct)).EnsureSuccessStatusCode();

        Assert.Equal(hashBefore, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Equal("Rappel", (await TrackAsync(path)).Title);
    }
}
