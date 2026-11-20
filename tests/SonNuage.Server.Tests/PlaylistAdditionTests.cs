using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonNuage.Contracts;
using SonNuage.Server.Data;
using SonNuage.Server.Youtube;

namespace SonNuage.Server.Tests;

/// <summary>Ajout à une playlist : pas de doublon, on voit où un titre est déjà, et un titre demandé y entre une fois importé.</summary>
public sealed class PlaylistAdditionTests(SonNuageFactory factory) : IClassFixture<SonNuageFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(HttpClient Admin, IReadOnlyList<TrackDto> Tracks)> LibraryAsync()
    {
        var admin = await factory.AdminClientAsync();
        await factory.WaitForScanAsync(admin);
        var album = (await admin.GetFromJsonAsync<PagedResult<AlbumDto>>("/api/albums?q=first", Ct))!.Items[0];
        return (admin, (await admin.GetFromJsonAsync<AlbumDetailDto>($"/api/albums/{album.Id}", Ct))!.Tracks);
    }

    private static async Task<PlaylistDto> CreateAsync(HttpClient client, string name, params Guid[] tracks) =>
        (await (await client.PostAsJsonAsync("/api/playlists", new CreatePlaylistRequest(name, null, tracks), Ct))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<PlaylistDto>(Ct))!;

    private static async Task<List<Guid>> TrackIdsAsync(HttpClient client, Guid playlist) =>
        (await client.GetFromJsonAsync<PlaylistDetailDto>($"/api/playlists/{playlist}", Ct))!.Entries.Select(e => e.Track.Id).ToList();

    [Fact]
    public async Task A_track_already_in_the_playlist_is_not_added_twice()
    {
        var (admin, tracks) = await LibraryAsync();
        var playlist = await CreateAsync(admin, "Sans doublon", tracks[0].Id, tracks[0].Id);
        Assert.Equal([tracks[0].Id], await TrackIdsAsync(admin, playlist.Id));

        var result = await (await admin.PostAsJsonAsync($"/api/playlists/{playlist.Id}/tracks",
                new AddPlaylistTracksRequest([tracks[0].Id, tracks[1].Id, tracks[1].Id], null), Ct))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<AddPlaylistTracksResultDto>(Ct);

        Assert.Equal(new AddPlaylistTracksResultDto(1, 1), result);
        Assert.Equal([tracks[0].Id, tracks[1].Id], await TrackIdsAsync(admin, playlist.Id));
    }

    [Fact]
    public async Task The_picker_sees_which_playlists_already_hold_the_tracks()
    {
        var (admin, tracks) = await LibraryAsync();
        var both = await CreateAsync(admin, "Les deux", tracks[0].Id, tracks[1].Id);
        var one = await CreateAsync(admin, "Un seul", tracks[1].Id);
        var none = await CreateAsync(admin, "Aucun");

        var counts = (await admin.GetFromJsonAsync<List<PlaylistContentsDto>>(
            $"/api/playlists/containing?track={tracks[0].Id}&track={tracks[1].Id}", Ct))!.ToDictionary(c => c.PlaylistId, c => c.Count);

        Assert.Equal(2, counts[both.Id]);
        Assert.Equal(1, counts[one.Id]);
        Assert.False(counts.ContainsKey(none.Id));
    }

    [Fact]
    public async Task A_requested_track_joins_the_playlist_once_imported()
    {
        var (admin, tracks) = await LibraryAsync();
        var playlist = await CreateAsync(admin, "À compléter");

        var request = await (await admin.PostAsJsonAsync("/api/requests",
                new CreateDownloadRequest("pppppppppp1", "Un morceau", "Une chaîne", 200, null, playlist.Id), Ct))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<DownloadRequestDto>(Ct);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
            var item = await db.YoutubeItems.SingleAsync(i => i.VideoId == "pppppppppp1", Ct);
            item.TrackId = tracks[1].Id;
            item.Status = YoutubeItemStatus.Done;
            await db.SaveChangesAsync(Ct);
            await RequestedPlaylists.AddImportedAsync(db, item, Ct);
            // Une seconde fois (tâche relancée) n'ajoute rien.
            await RequestedPlaylists.AddImportedAsync(db, item, Ct);
        }

        Assert.NotNull(request);
        Assert.Equal([tracks[1].Id], await TrackIdsAsync(admin, playlist.Id));
    }

    [Fact]
    public async Task A_request_cannot_target_someone_elses_playlist()
    {
        var (admin, _) = await LibraryAsync();
        var playlist = await CreateAsync(admin, "Privée");
        await (await admin.PostAsJsonAsync("/api/admin/users", new CreateUserRequest("pl-contrib", "pl-contrib-password", Roles.Contributor), Ct))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<UserDto>(Ct);
        var contributor = await factory.ClientAsync("pl-contrib", "pl-contrib-password");

        var response = await contributor.PostAsJsonAsync("/api/requests",
            new CreateDownloadRequest("pppppppppp2", "Un morceau", "Une chaîne", 200, null, playlist.Id), Ct);
        var album = await contributor.PostAsJsonAsync("/api/requests/album", new CreateAlbumRequest("Daft Punk", "Discovery", playlist.Id), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, album.StatusCode);
    }
}
