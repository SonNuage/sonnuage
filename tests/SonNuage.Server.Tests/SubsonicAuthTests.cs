using SonNuage.Server.Subsonic;

namespace SonNuage.Server.Tests;

/// <summary>Identifiants Subsonic : jeton <c>t = md5(mot de passe + s)</c>, mot de passe en clair ou <c>enc:</c> hexadécimal.</summary>
public sealed class SubsonicAuthTests
{
    [Fact]
    public void The_token_is_the_md5_of_password_and_salt()
    {
        // Exemple de la documentation Subsonic : sesame + c19b2d.
        Assert.True(SubsonicCredentials.TokenMatches("sesame", "c19b2d", "26719a1196d2a940705a59634eb18eab"));
        Assert.True(SubsonicCredentials.TokenMatches("sesame", "c19b2d", "26719A1196D2A940705A59634EB18EAB"));
        Assert.False(SubsonicCredentials.TokenMatches("sesame", "other", "26719a1196d2a940705a59634eb18eab"));
        Assert.False(SubsonicCredentials.TokenMatches("sesame", "c19b2d", "not-hex"));
    }

    [Theory]
    [InlineData("sesame", "sesame")]
    [InlineData("enc:736573616d65", "sesame")]
    [InlineData("enc:zz", null)]
    public void A_password_may_be_hex_encoded(string given, string? expected)
    {
        Assert.Equal(expected, SubsonicCredentials.DecodePassword(given));
    }

    [Fact]
    public void Generated_passwords_are_long_and_easy_to_type()
    {
        var a = SubsonicCredentials.NewPassword();
        var b = SubsonicCredentials.NewPassword();

        Assert.NotEqual(a, b);
        Assert.Equal(20, a.Length);
        Assert.All(a, c => Assert.True(char.IsAsciiLetterOrDigit(c)));
        Assert.DoesNotContain(a, c => "0O1lI".Contains(c));
    }
}
