using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using MuseFM.Data.Entities;
using MuseFM.Services;

namespace MuseFM.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<AppUser>(options)
{
    public DbSet<Artist> Artists => Set<Artist>();
    public DbSet<Track> Tracks => Set<Track>();
    public DbSet<TrackArtist> TrackArtists => Set<TrackArtist>();
    public DbSet<Album> Albums => Set<Album>();
    public DbSet<AlbumArtist> AlbumArtists => Set<AlbumArtist>();
    public DbSet<Scrobble> Scrobbles => Set<Scrobble>();
    public DbSet<TrackFeedback> TrackFeedback => Set<TrackFeedback>();
    public DbSet<Obsession> Obsessions => Set<Obsession>();
    public DbSet<CatalogAlias> CatalogAliases => Set<CatalogAlias>();
    public DbSet<Follow> Follows => Set<Follow>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<ScrobblerSession> ScrobblerSessions => Set<ScrobblerSession>();
    public DbSet<ImportJob> ImportJobs => Set<ImportJob>();
    public DbSet<MetadataConnector> MetadataConnectors => Set<MetadataConnector>();
    public DbSet<AppState> AppState => Set<AppState>();
    public DbSet<ArtistMetadata> ArtistMetadata => Set<ArtistMetadata>();
    public DbSet<AlbumMetadata> AlbumMetadata => Set<AlbumMetadata>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<AppUser>(e =>
        {
            e.Property(x => x.Theme).HasDefaultValue("system");
            e.Property(x => x.Accent).HasDefaultValue("violet");
        });

        builder.Entity<Artist>(e => e.HasIndex(x => x.Key).IsUnique());
        // Title keys are lookup keys, not identities: different songs can share a title.
        builder.Entity<Track>(e =>
        {
            e.HasIndex(x => x.Key);
            // Feedback from ListenBrainz clients names recordings by MBID.
            e.HasIndex(x => x.MusicBrainzId);
        });
        builder.Entity<Album>(e => e.HasIndex(x => x.Key));
        builder.Entity<AppState>(e => e.HasKey(x => x.Name));

        builder.Entity<TrackArtist>(e =>
        {
            e.HasKey(x => new { x.TrackId, x.ArtistId });
            e.HasIndex(x => x.ArtistId);
            e.HasOne(x => x.Track).WithMany(x => x.Artists).HasForeignKey(x => x.TrackId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Artist).WithMany(x => x.Tracks).HasForeignKey(x => x.ArtistId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AlbumArtist>(e =>
        {
            e.HasKey(x => new { x.AlbumId, x.ArtistId });
            e.HasIndex(x => x.ArtistId);
            e.HasOne(x => x.Album).WithMany(x => x.Artists).HasForeignKey(x => x.AlbumId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Artist).WithMany(x => x.Albums).HasForeignKey(x => x.ArtistId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Scrobble>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.ListenedAt }).IsUnique();
            e.HasIndex(x => x.TrackId);
            e.HasIndex(x => x.AlbumId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Track).WithMany().HasForeignKey(x => x.TrackId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Album).WithMany().HasForeignKey(x => x.AlbumId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<TrackFeedback>(e =>
        {
            e.HasKey(x => new { x.UserId, x.TrackId });
            e.HasIndex(x => new { x.UserId, x.Score, x.CreatedAt });
            e.HasIndex(x => x.TrackId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Track).WithMany().HasForeignKey(x => x.TrackId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Obsession>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.StartedAt });
            e.HasIndex(x => x.TrackId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            // Merges move obsessions to the surviving track before the merged one is deleted.
            e.HasOne(x => x.Track).WithMany().HasForeignKey(x => x.TrackId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<CatalogAlias>(e =>
        {
            e.HasIndex(x => x.Key);
            e.HasOne(x => x.Artist).WithMany().HasForeignKey(x => x.ArtistId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Album).WithMany().HasForeignKey(x => x.AlbumId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Track).WithMany().HasForeignKey(x => x.TrackId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Follow>(e =>
        {
            e.HasKey(x => new { x.FollowerId, x.FolloweeId });
            e.HasIndex(x => x.FolloweeId);
            e.HasOne(x => x.Follower).WithMany().HasForeignKey(x => x.FollowerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Followee).WithMany().HasForeignKey(x => x.FolloweeId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ApiKey>(e =>
        {
            e.HasIndex(x => x.KeyHash).IsUnique();
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ScrobblerSession>(e =>
        {
            e.HasIndex(x => x.KeyHash).IsUnique();
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ApiKey).WithMany().HasForeignKey(x => x.ApiKeyId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ImportJob>(e =>
        {
            e.Property(x => x.Kind).HasConversion<string>();
            e.Property(x => x.Status).HasConversion<string>();
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<MetadataConnector>(e =>
        {
            e.HasKey(x => x.Kind);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
        });

        builder.Entity<ArtistMetadata>(e =>
        {
            e.HasKey(x => x.ArtistId);
            e.HasOne(x => x.Artist).WithOne().HasForeignKey<ArtistMetadata>(x => x.ArtistId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AlbumMetadata>(e =>
        {
            e.HasKey(x => x.AlbumId);
            e.HasOne(x => x.Album).WithOne().HasForeignKey<AlbumMetadata>(x => x.AlbumId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
        // WAL lets the web UI read while an import is writing.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        if (!await roles.RoleExistsAsync(Roles.Admin))
            await roles.CreateAsync(new IdentityRole(Roles.Admin));

        // Re-key the catalog when the matching rules changed (before anything can scrobble).
        await CatalogMaintenance.UpgradeAsync(db, scope.ServiceProvider.GetRequiredService<DataPaths>(),
            scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("MuseFM.Catalog"));
    }
}
