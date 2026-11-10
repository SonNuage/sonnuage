using Microsoft.EntityFrameworkCore;

namespace SonNuage.Server.Data;

public sealed class SonNuageDbContext(DbContextOptions<SonNuageDbContext> options) : DbContext(options)
{
    public DbSet<Artist> Artists => Set<Artist>();
    public DbSet<Album> Albums => Set<Album>();
    public DbSet<Track> Tracks => Set<Track>();
    public DbSet<TrackGuest> TrackGuests => Set<TrackGuest>();
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Playlist> Playlists => Set<Playlist>();
    public DbSet<PlaylistEntry> PlaylistEntries => Set<PlaylistEntry>();
    public DbSet<Favorite> Favorites => Set<Favorite>();
    public DbSet<FavoriteAlbum> FavoriteAlbums => Set<FavoriteAlbum>();
    public DbSet<FavoriteArtist> FavoriteArtists => Set<FavoriteArtist>();
    public DbSet<UserRating> Ratings => Set<UserRating>();
    public DbSet<PlayEvent> PlayEvents => Set<PlayEvent>();
    public DbSet<DiscoveryArtist> DiscoveryArtists => Set<DiscoveryArtist>();
    public DbSet<DiscoveryTrack> DiscoveryTracks => Set<DiscoveryTrack>();
    public DbSet<YoutubeItem> YoutubeItems => Set<YoutubeItem>();
    public DbSet<YoutubeSubscription> YoutubeSubscriptions => Set<YoutubeSubscription>();
    public DbSet<DownloadRequest> DownloadRequests => Set<DownloadRequest>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<JobSuite> JobSuites => Set<JobSuite>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobLog> JobLogs => Set<JobLog>();
    public DbSet<JobLanePause> JobLanePauses => Set<JobLanePause>();
    public DbSet<CrashReport> CrashReports => Set<CrashReport>();
    public DbSet<SavedQueue> SavedQueues => Set<SavedQueue>();
    public DbSet<SavedQueueEntry> SavedQueueEntries => Set<SavedQueueEntry>();
    public DbSet<TrackAudioFeatures> AudioFeatures => Set<TrackAudioFeatures>();
    public DbSet<TrackTag> TrackTags => Set<TrackTag>();
    public DbSet<RescanProposal> RescanProposals => Set<RescanProposal>();
    public DbSet<TrackFingerprint> Fingerprints => Set<TrackFingerprint>();
    public DbSet<HiddenFile> HiddenFiles => Set<HiddenFile>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Artist>(e =>
        {
            e.HasIndex(x => x.NormalizedName).IsUnique();
            e.Property(x => x.Name).HasMaxLength(512);
            e.Property(x => x.NormalizedName).HasMaxLength(512);
            e.Property(x => x.MusicBrainzId).HasMaxLength(36);
            e.HasIndex(x => x.MusicBrainzId);
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
            e.HasOne(x => x.Artist).WithMany(x => x.Tracks).HasForeignKey(x => x.ArtistId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<TrackGuest>(e =>
        {
            e.HasKey(x => new { x.TrackId, x.ArtistId });
            e.HasIndex(x => x.ArtistId);
            e.HasOne(x => x.Track).WithMany().HasForeignKey(x => x.TrackId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Artist).WithMany().HasForeignKey(x => x.ArtistId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CrashReport>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.ReceivedAt });
            e.HasIndex(x => x.ReceivedAt);
            foreach (var p in new[] { nameof(CrashReport.App), nameof(CrashReport.AppVersion), nameof(CrashReport.Device), nameof(CrashReport.Os), nameof(CrashReport.Source) })
                e.Property(p).HasMaxLength(CrashReport.MaxFieldLength);
            e.Property(x => x.Headline).HasMaxLength(CrashReport.MaxHeadlineLength);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SavedQueue>(e =>
        {
            e.HasKey(x => x.UserId);
            e.Property(x => x.ChangedBy).HasMaxLength(SavedQueue.MaxClientLength);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Entries).WithOne().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SavedQueueEntry>(e =>
        {
            e.HasKey(x => new { x.UserId, x.Position });
            e.HasIndex(x => x.TrackId);
            e.HasOne(x => x.Track).WithMany().HasForeignKey(x => x.TrackId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.NormalizedUsername).IsUnique();
            e.Property(x => x.Username).HasMaxLength(64);
            e.Property(x => x.Language).HasMaxLength(16);
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

        b.Entity<FavoriteAlbum>(e =>
        {
            e.HasKey(x => new { x.UserId, x.AlbumId });
            e.HasIndex(x => x.AlbumId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Album).WithMany().HasForeignKey(x => x.AlbumId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TrackAudioFeatures>(e =>
        {
            e.HasKey(x => x.TrackId);
            e.Property(x => x.Error).HasMaxLength(TrackAudioFeatures.MaxErrorLength);
            e.HasOne(x => x.Track).WithOne().HasForeignKey<TrackAudioFeatures>(x => x.TrackId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TrackTag>(e =>
        {
            e.HasKey(x => new { x.TrackId, x.Name });
            e.Property(x => x.Name).HasMaxLength(TrackTag.MaxNameLength);
            e.HasIndex(x => x.Name);
            e.HasOne<Track>().WithMany().HasForeignKey(x => x.TrackId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RescanProposal>(e =>
        {
            e.HasIndex(x => new { x.Status, x.Score });
            e.HasIndex(x => new { x.TrackId, x.Signature });
            e.HasIndex(x => x.SuiteId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Signature).HasMaxLength(64);
            e.Property(x => x.Title).HasMaxLength(RescanProposal.MaxTextLength);
            e.Property(x => x.ArtistName).HasMaxLength(RescanProposal.MaxTextLength);
            e.Property(x => x.AlbumTitle).HasMaxLength(RescanProposal.MaxTextLength);
            e.Property(x => x.DecidedBy).HasMaxLength(RescanProposal.MaxDecidedByLength);
            e.HasOne(x => x.Track).WithMany().HasForeignKey(x => x.TrackId).OnDelete(DeleteBehavior.Cascade);
            // Les refus survivent à la suppression de la suite qui les a produits.
            e.HasOne(x => x.Suite).WithMany().HasForeignKey(x => x.SuiteId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<TrackFingerprint>(e =>
        {
            e.HasKey(x => x.TrackId);
            e.Property(x => x.Error).HasMaxLength(TrackFingerprint.MaxErrorLength);
            e.HasOne(x => x.Track).WithOne().HasForeignKey<TrackFingerprint>(x => x.TrackId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<HiddenFile>(e =>
        {
            e.HasKey(x => x.Path);
            e.Property(x => x.Path).HasMaxLength(HiddenFile.MaxPathLength);
            e.Property(x => x.HiddenBy).HasMaxLength(RescanProposal.MaxDecidedByLength);
        });

        b.Entity<UserRating>(e =>
        {
            e.HasKey(x => new { x.UserId, x.ItemId });
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<FavoriteArtist>(e =>
        {
            e.HasKey(x => new { x.UserId, x.ArtistId });
            e.HasIndex(x => x.ArtistId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Artist).WithMany().HasForeignKey(x => x.ArtistId).OnDelete(DeleteBehavior.Cascade);
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

        b.Entity<DiscoveryArtist>(e =>
        {
            e.HasIndex(x => x.MusicBrainzId).IsUnique();
            e.HasIndex(x => x.Score);
            e.Property(x => x.MusicBrainzId).HasMaxLength(36);
            e.Property(x => x.Name).HasMaxLength(512);
            e.Property(x => x.Comment).HasMaxLength(512);
            e.Property(x => x.BecauseOf).HasMaxLength(2000);
            e.HasMany(x => x.Tracks).WithOne(t => t.Artist).HasForeignKey(t => t.ArtistId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<DiscoveryTrack>(e =>
        {
            e.HasIndex(x => x.VideoId);
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.RecordingMbid).HasMaxLength(36);
            e.Property(x => x.ReleaseName).HasMaxLength(500);
            e.Property(x => x.VideoId).HasMaxLength(11);
            e.Property(x => x.VideoTitle).HasMaxLength(500);
            e.Property(x => x.Channel).HasMaxLength(200);
        });
        b.Entity<DownloadRequest>(e =>
        {
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasIndex(x => x.VideoId);
            e.Property(x => x.VideoId).HasMaxLength(32);
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.Channel).HasMaxLength(200);
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.DenyReason).HasMaxLength(500);
            e.Property(x => x.DecidedBy).HasMaxLength(100);
            e.Property(x => x.Decision).HasConversion<string>().HasMaxLength(16);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.YoutubeItem).WithMany().HasForeignKey(x => x.YoutubeItemId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<YoutubeSubscription>(e =>
        {
            e.HasIndex(x => x.Url).IsUnique();
        });

        b.Entity<ApiKey>(e =>
        {
            e.HasIndex(x => x.KeyHash).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            e.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<JobSuite>(e =>
        {
            e.HasIndex(x => x.CreatedAt);
            e.Property(x => x.Kind).HasMaxLength(64);
            e.Property(x => x.Title).HasMaxLength(300);
        });

        b.Entity<Job>(e =>
        {
            // Index de la requête de réservation (file, état, priorité, date d'éligibilité).
            e.HasIndex(x => new { x.Lane, x.Status, x.Priority, x.NotBefore });
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.DependsOnId);
            e.HasIndex(x => x.SubjectId);
            e.Property(x => x.Kind).HasMaxLength(64);
            e.Property(x => x.Lane).HasMaxLength(32);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.Property(x => x.WorkerId).HasMaxLength(100);
            e.HasOne(x => x.Suite).WithMany(x => x.Jobs).HasForeignKey(x => x.SuiteId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<JobLanePause>(e =>
        {
            e.HasKey(x => x.Lane);
            e.Property(x => x.Lane).HasMaxLength(32);
        });

        b.Entity<JobLog>(e =>
        {
            e.HasIndex(x => new { x.JobId, x.Id });
            e.Property(x => x.Level).HasMaxLength(8);
            e.Property(x => x.Message).HasMaxLength(4000);
            e.HasOne<Job>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
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
