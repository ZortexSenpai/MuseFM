using MuseFM.Services;

namespace MuseFM.Tests;

public class StatsTests : IClassFixture<MuseFmFactory>
{
    private readonly MuseFmFactory _factory;

    public StatsTests(MuseFmFactory factory) => _factory = factory;

    [Fact]
    public async Task Charts_CountMultiArtistTracksForEachArtist()
    {
        var (user, _) = await _factory.CreateUserAsync("stats-user");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        ScrobbleInput S(int minutesAgo, string artist, string title, string? album = null) => new()
        {
            Title = title, Artists = [artist], Album = album, ListenedAt = now - minutesAgo * 60,
        };

        await _factory.WithScopeAsync(sp => sp.GetRequiredService<ScrobbleService>().IngestAsync(user.Id,
        [
            S(10, "Bicep", "Glue", "Bicep"),
            S(20, "Bicep", "Glue", "Bicep"),
            S(30, "Bicep feat. Clara La San", "Saku", "Isles"),
            S(40, "Floating Points", "Silhouettes"),
            S(60 * 24 * 40, "Floating Points", "Silhouettes"), // 40 days ago: outside the 30-day window
        ]));

        var result = await _factory.WithScopeAsync(async sp =>
        {
            var stats = sp.GetRequiredService<StatsService>();
            var period = StatPeriod.Create(StatRange.Month, TimeZoneInfo.Utc);
            return new
            {
                Totals = await stats.GetTotalsAsync(user.Id, period.From),
                AllTime = await stats.GetTotalsAsync(user.Id, null),
                Artists = await stats.TopArtistsAsync(user.Id, period.From, 1, 10),
                Tracks = await stats.TopTracksAsync(user.Id, period.From, 1, 10),
                Albums = await stats.TopAlbumsAsync(user.Id, period.From, 1, 10),
                Activity = await stats.ActivityAsync(user.Id, period),
            };
        });

        Assert.Equal(new Totals(4, 3, 2, 3), result.Totals);
        Assert.Equal(5, result.AllTime.Scrobbles);

        Assert.Equal(["Bicep", "Clara La San", "Floating Points"], result.Artists.Items.Select(a => a.Title));
        Assert.Equal([3, 1, 1], result.Artists.Items.Select(a => a.Count));

        Assert.Equal("Glue", result.Tracks.Items[0].Title);
        Assert.Equal(2, result.Tracks.Items[0].Count);
        Assert.Equal(["Bicep", "Clara La San"], result.Tracks.Items.Single(t => t.Title == "Saku").Artists.Select(a => a.Name));

        Assert.Equal(2, result.Albums.Total);
        Assert.Equal(30, result.Activity.Count);
        Assert.Equal(4, result.Activity.Sum(b => b.Count));
    }
}
