using System.Net;
using System.Text;
using SonNuage.Contracts;
using SonNuage.UI.Services;

namespace SonNuage.Server.Tests;

/// <summary>Lecture des réponses du serveur par le client des applications.</summary>
public sealed class ApiClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_answer_without_content_is_nothing_rather_than_an_error()
    {
        // La recherche par ambiance répond 204 quand la demande n'est pas une ambiance : c'était une erreur JSON non
        // rattrapée à chaque recherche ordinaire.
        using var none = new HttpResponseMessage(HttpStatusCode.NoContent);
        Assert.Null(await ApiClient.ReadAsync<AmbianceResultDto>(none));

        using var empty = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        Assert.Null(await ApiClient.ReadAsync<AmbianceResultDto>(empty));

        using var found = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"stationId":"ambiance:calm","terms":["calme"],"tracks":[]}""", Encoding.UTF8, "application/json"),
        };
        Assert.Equal("ambiance:calm", (await ApiClient.ReadAsync<AmbianceResultDto>(found))!.StationId);
    }
}
