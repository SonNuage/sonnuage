using System.Text.Json;
using System.Xml.Linq;
using SonNuage.Server.Subsonic;

namespace SonNuage.Server.Tests;

/// <summary>Enveloppe Subsonic : même réponse en XML (par défaut) et en JSON (<c>f=json</c>), erreurs codées.</summary>
public sealed class SubsonicFormatTests
{
    private static readonly XNamespace Ns = "http://subsonic.org/restapi";

    private static SubsonicNode Sample()
    {
        var root = SubsonicNode.Ok("1.2.3");
        var artists = root.Add("artists").Attr("ignoredArticles", "The Le La Les");
        var index = artists.List("index");
        var a = index.Add().Attr("name", "A");
        a.List("artist").Add().Attr("id", "ar-1").Attr("name", "ABBA").Attr("albumCount", 3).Attr("starred", (DateTimeOffset?)null);
        index.Add().Attr("name", "B").List("artist");
        return root;
    }

    [Fact]
    public void Xml_has_the_subsonic_namespace_and_attributes()
    {
        var text = Sample().ToXml();
        var xml = XDocument.Parse(text);

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", text);
        var root = xml.Root!;
        Assert.Equal(Ns + "subsonic-response", root.Name);
        Assert.Equal("ok", (string?)root.Attribute("status"));
        Assert.Equal(SubsonicNode.ApiVersion, (string?)root.Attribute("version"));
        Assert.Equal("true", (string?)root.Attribute("openSubsonic"));
        Assert.Equal("1.2.3", (string?)root.Attribute("serverVersion"));
        var artist = root.Element(Ns + "artists")!.Element(Ns + "index")!.Element(Ns + "artist")!;
        Assert.Equal("ABBA", (string?)artist.Attribute("name"));
        Assert.Equal("3", (string?)artist.Attribute("albumCount"));
        Assert.Null(artist.Attribute("starred"));
    }

    [Fact]
    public void Json_wraps_in_subsonic_response_with_typed_values_and_arrays()
    {
        using var json = JsonDocument.Parse(Sample().ToJson());

        var root = json.RootElement.GetProperty("subsonic-response");
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("openSubsonic").GetBoolean());
        var index = root.GetProperty("artists").GetProperty("index");
        Assert.Equal(JsonValueKind.Array, index.ValueKind);
        var artist = index[0].GetProperty("artist")[0];
        Assert.Equal(3, artist.GetProperty("albumCount").GetInt32());
        Assert.False(artist.TryGetProperty("starred", out _));
        // Une liste vide reste un tableau.
        Assert.Equal(0, index[1].GetProperty("artist").GetArrayLength());
    }

    [Fact]
    public void Booleans_and_dates_use_the_subsonic_formats()
    {
        var root = SubsonicNode.Ok("1");
        root.Add("song").Attr("isDir", false).Attr("created", new DateTimeOffset(2026, 11, 9, 18, 5, 0, TimeSpan.Zero)).Attr("duration", 241.7);

        var song = XDocument.Parse(root.ToXml()).Root!.Element(Ns + "song")!;
        Assert.Equal("false", (string?)song.Attribute("isDir"));
        Assert.Equal("2026-11-09T18:05:00.000Z", (string?)song.Attribute("created"));
        using var json = JsonDocument.Parse(root.ToJson());
        Assert.False(json.RootElement.GetProperty("subsonic-response").GetProperty("song").GetProperty("isDir").GetBoolean());
    }

    [Fact]
    public void Text_content_is_value_in_json()
    {
        var root = SubsonicNode.Ok("1");
        root.Add("lyrics").Attr("artist", "Madonna").Text("Last night I dreamt");

        Assert.Equal("Last night I dreamt", XDocument.Parse(root.ToXml()).Root!.Element(Ns + "lyrics")!.Value);
        using var json = JsonDocument.Parse(root.ToJson());
        Assert.Equal("Last night I dreamt", json.RootElement.GetProperty("subsonic-response").GetProperty("lyrics").GetProperty("value").GetString());
    }

    [Fact]
    public void An_error_is_a_failed_response_with_code_and_message()
    {
        var error = SubsonicNode.Error("1", SubsonicError.WrongCredentials, "Wrong username or password");

        var root = XDocument.Parse(error.ToXml()).Root!;
        Assert.Equal("failed", (string?)root.Attribute("status"));
        Assert.Equal("40", (string?)root.Element(Ns + "error")!.Attribute("code"));
        using var json = JsonDocument.Parse(error.ToJson());
        Assert.Equal(40, json.RootElement.GetProperty("subsonic-response").GetProperty("error").GetProperty("code").GetInt32());
    }
}
