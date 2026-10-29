using Microsoft.EntityFrameworkCore;

namespace Localify.Server.Data;

public sealed class LocalifyDbContext(DbContextOptions<LocalifyDbContext> options) : DbContext(options)
{
    public DbSet<Artist> Artists => Set<Artist>();
    public DbSet<Album> Albums => Set<Album>();
    public DbSet<Track> Tracks => Set<Track>();
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Playlist> Playlists => Set<Playlist>();
    public DbSet<PlaylistEntry> PlaylistEntries => Set<PlaylistEntry>();
    public DbSet<Favorite> Favorites => Set<Favorite>();
    public DbSet<PlayEvent> PlayEvents => Set<PlayEvent>();
    public DbSet<YoutubeItem> YoutubeItems => Set<YoutubeItem>();
    public DbSet<YoutubeSubscription> YoutubeSubscriptions => Set<YoutubeSubscription>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Artist>(e =>
        {
            e.HasIndex(x => x.NormalizedName).IsUnique();
            e.Property(x => x.Name).HasMaxLength(512);
            e.Property(x => x.NormalizedName).HasMaxLength(512);
        });

        b.Entity<Album>(e =>
        {
            e.HasIndex(x => new { x.ArtistId, x.NormalizedTitle }).IsUnique();
            e.HasIndex(x => x.CreatedAt);
            e.Property(x => x.Title).HasMaxLength(512);
            e.HasOne(x => x.Artist).WithMany(x => x.Albums).HasForeignKey(x => x.ArtistId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Track>(e =>
        {
            e.HasIndex(x => x.FilePath).IsUnique();
            e.HasIndex(x => x.Title);
            e.HasOne(x => x.Album).WithMany(x => x.Tracks).HasForeignKey(x => x.AlbumId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Artist).WithMany().HasForeignKey(x => x.ArtistId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.NormalizedUsername).IsUnique();
            e.Property(x => x.Username).HasMaxLength(64);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Playlist>(e =>
        {
            e.HasIndex(x => x.OwnerId);
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PlaylistEntry>(e =>
        {
            e.HasIndex(x => new { x.PlaylistId, x.Position });
            e.HasOne(x => x.Playlist).WithMany(x => x.Entries).HasForeignKey(x => x.PlaylistId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Track).WithMany().HasForeignKey(x => x.TrackId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Favorite>(e =>
        {
            e.HasKey(x => new { x.UserId, x.TrackId });
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Track).WithMany().HasForeignKey(x => x.TrackId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PlayEvent>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.PlayedAt });
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Track).WithMany().HasForeignKey(x => x.TrackId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<YoutubeItem>(e =>
        {
            e.HasIndex(x => x.VideoId).IsUnique();
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.Property(x => x.VideoId).HasMaxLength(32);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne(x => x.Subscription).WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Track).WithMany().HasForeignKey(x => x.TrackId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<YoutubeSubscription>(e =>
        {
            e.HasIndex(x => x.Url).IsUnique();
        });

        // SQLite ne sait pas trier/comparer des DateTimeOffset : stockage en ticks UTC.
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var prop in entity.GetProperties())
            {
                if (prop.ClrType == typeof(DateTimeOffset))
                    prop.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
                else if (prop.ClrType == typeof(DateTimeOffset?))
                    prop.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
            }
        }
    }
}
