using SonNuage.Contracts;
using SonNuage.Server.Auth;
using SonNuage.Server.Storage;
using SonNuage.Server.Youtube;

namespace SonNuage.Server.Api;

/// <summary>Garde-fous disque réglables par l'admin : espace libre minimum et place allouée aux extraits.</summary>
public static class StorageEndpoints
{
    public static void MapStorageEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/storage").RequireAuthorization(AuthConstants.AdminPolicy).WithTags("Admin");

        group.MapGet("", (StorageGuard storage, PreviewService previews) => storage.Status(previews.TotalBytes()));

        group.MapPut("", (StorageSettingsDto settings, StorageGuard storage, PreviewService previews) =>
            storage.Save(settings) is { } error
                ? Results.Problem(error, statusCode: StatusCodes.Status400BadRequest)
                : Results.Ok(storage.Status(previews.TotalBytes())));
    }
}
