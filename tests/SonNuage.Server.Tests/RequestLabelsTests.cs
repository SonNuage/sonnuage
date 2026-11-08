using SonNuage.Contracts;
using SonNuage.Contracts.Localization;
using SonNuage.UI.Services;

namespace SonNuage.Server.Tests;

/// <summary>Libellés des rôles et des états de demande dans l'interface.</summary>
public sealed class RequestLabelsTests
{
    [Theory]
    [InlineData(Roles.Admin, "users.roleAdmin")]
    [InlineData(Roles.Contributor, "users.roleContributor")]
    [InlineData(Roles.User, "users.roleUser")]
    [InlineData(null, "users.roleUser")]
    public void Each_role_has_its_label(string? role, string key) => Assert.Equal(key, RoleLabels.Key(role));

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    public void Every_role_and_request_status_is_translated(string language)
    {
        foreach (var role in new[] { Roles.Admin, Roles.Contributor, Roles.User })
        {
            Assert.True(Translations.Instance.Has(language, RoleLabels.Key(role)), role);
            Assert.True(Translations.Instance.Has(language, RoleLabels.Key(role, "settings")), role);
        }
        foreach (var status in DownloadRequestStatus.All)
            Assert.True(Translations.Instance.Has(language, "requests.status." + status), status);
    }
}
