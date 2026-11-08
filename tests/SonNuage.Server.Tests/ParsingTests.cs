using System.Text.Json.Nodes;
using SonNuage.Server.Search;
using SonNuage.Server.Streaming;
using SonNuage.Server.Youtube;

namespace SonNuage.Server.Tests;

/// <summary>Données venues de l'extérieur (yt-dlp, Elasticsearch, octets d'une image) : lues une fois, à l'entrée.</summary>
public sealed class ParsingTests
{
    private static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    [Theory]
    [InlineData("""{"id":"dQw4w9WgXcQ"}""", true)]
    [InlineData("""{"id":"dQw4w9WgXcQ","ie_key":"Youtube","_type":"url"}""", true)]
    [InlineData("""{"id":"dQw4w9WgXcQ","_type":"video"}""", true)]
    [InlineData("""{"id":"UCabcdefghijklmnopqrstuv","ie_key":"YoutubeTab"}""", false)]
    [InlineData("""{"id":"dQw4w9WgXcQ","ie_key":"YoutubeTab"}""", false)]
    [InlineData("""{"id":"dQw4w9WgXcQ","_type":"playlist"}""", false)]
    [InlineData("""{"id":"short"}""", false)]
    [InlineData("""{"id":12345678901}""", false)]
    public void Only_youtube_videos_count_as_videos(string json, bool expected) => Assert.Equal(expected, YtDlp.IsVideo(Json(json)));

    [Fact]
    public void An_entry_falls_back_on_the_id_the_uploader_and_the_video_thumbnail()
    {
        var bare = YtDlp.ToEntry(Json("""{"id":"dQw4w9WgXcQ","uploader":"Rick","duration":"long"}"""));
        Assert.Equal(new YtEntry("dQw4w9WgXcQ", "dQw4w9WgXcQ", "Rick", null, "https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg"), bare);

        var full = YtDlp.ToEntry(Json("""{"id":"dQw4w9WgXcQ","title":"T","channel":"C","uploader":"U","artist":"A","duration":212.5,"thumbnail":"https://t/x.jpg"}"""));
        Assert.Equal(new YtEntry("dQw4w9WgXcQ", "T", "C", 212.5, "https://t/x.jpg"), full);
        Assert.Equal("A", YtDlp.ToEntry(Json("""{"id":"dQw4w9WgXcQ","artist":"A"}""")).Channel);
    }

    [Fact]
    public void The_largest_thumbnail_wins_then_the_preferred_one()
    {
        var e = Json("""
            {"thumbnail":"https://t/fallback.jpg","thumbnails":[
              {"url":"https://t/small.jpg","width":120,"height":90},
              {"url":"https://t/big.jpg","width":1280,"height":720},
              {"width":4000,"height":4000},
              {"url":"https://t/pref-low.jpg","preference":-5},
              {"url":"https://t/pref-high.jpg","preference":3}]}
            """);
        Assert.Equal("https://t/big.jpg", YtDlp.BestThumbnail(e));
        Assert.Equal("https://t/b.jpg", YtDlp.BestThumbnail(Json("""{"thumbnails":[{"url":"https://t/a.jpg","preference":-5},{"url":"https://t/b.jpg","preference":3}]}""")));
        Assert.Equal("https://t/fallback.jpg", YtDlp.BestThumbnail(Json("""{"thumbnail":"https://t/fallback.jpg","thumbnails":[]}""")));
        Assert.Null(YtDlp.BestThumbnail(Json("{}")));
    }

    [Fact]
    public void A_correction_replaces_each_misspelt_word_by_its_best_suggestion()
    {
        var response = JsonNode.Parse("""
            {"suggest":{"fix":[
              {"offset":0,"length":6,"options":[{"text":"daft"},{"text":"draft"}]},
              {"offset":7,"length":4,"options":[]},
              {"offset":12,"length":5,"options":[{"text":"lucky"}]}]}}
            """);
        Assert.Equal("daft punk lucky", ElasticsearchClient.Correction("dafttt punk lukky", response));
        Assert.Null(ElasticsearchClient.Correction("daft punk", JsonNode.Parse("""{"suggest":{"fix":[{"offset":0,"length":4,"options":[]}]}}""")));
        Assert.Null(ElasticsearchClient.Correction("daft", JsonNode.Parse("{}")));
        Assert.Null(ElasticsearchClient.Correction("daft", null));
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A }, ".png")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50, 0 }, ".webp")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x41, 0x56, 0x45 }, ".jpg")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF }, ".jpg")]
    [InlineData(new byte[0], ".jpg")]
    public void An_embedded_picture_is_named_after_its_magic_bytes(byte[] data, string extension) =>
        Assert.Equal(extension, CoverService.SniffExtension(data));
}
