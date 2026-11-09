using Microsoft.EntityFrameworkCore;

namespace SonNuage.Server.Data;

/// <summary>Notes de 1 à 5 d'un compte sur un titre, un album ou un artiste (API SonNuage et Subsonic).</summary>
public static class UserRatings
{
    public static Task<int?> OfAsync(SonNuageDbContext db, Guid userId, Guid itemId, CancellationToken ct) =>
        db.Ratings.Where(r => r.UserId == userId && r.ItemId == itemId).Select(r => (int?)r.Value).FirstOrDefaultAsync(ct);

    /// <summary>Remplace la note ; 0 la retire. La valeur doit déjà être comprise entre 0 et 5.</summary>
    public static async Task SetAsync(SonNuageDbContext db, Guid userId, Guid itemId, int value, DateTimeOffset now, CancellationToken ct)
    {
        await db.Ratings.Where(r => r.UserId == userId && r.ItemId == itemId).ExecuteDeleteAsync(ct);
        if (value <= 0) return;
        db.Ratings.Add(new UserRating { UserId = userId, ItemId = itemId, Value = value, UpdatedAt = now });
        await db.SaveChangesAsync(ct);
    }
}
