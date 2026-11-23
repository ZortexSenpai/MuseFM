using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MuseFM.Data;
using MuseFM.Data.Entities;
using MuseFM.Services;
using MuseFM.Services.Metadata;

namespace MuseFM.Tests;

public sealed class NowPlayingTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (NowPlayingStore Store, Clock Clock) Create(int? timeoutSeconds = null)
    {
        var options = new MuseFmOptions();
        if (timeoutSeconds is { } seconds) options.NowPlayingTimeoutSeconds = seconds;
        var clock = new Clock();
        return (new NowPlayingStore(Options.Create(options), clock), clock);
    }

    private static ScrobbleInput Track(int? length = null) =>
        new() { Title = "Baby", Artists = ["Four Tet"], ListenedAt = 0, LengthSeconds = length };

    [Fact]
    public void WithoutLength_ExpiresAfterTheTimeout()
    {
        var (store, clock) = Create();
        Assert.True(store.Set("user", Track()));

        clock.Now += TimeSpan.FromSeconds(89);
        Assert.NotNull(store.Get("user"));
        clock.Now += TimeSpan.FromSeconds(2);
        Assert.Null(store.Get("user"));
    }

    [Fact]
    public void WithoutLength_StaysWhileTheClientRepeatsIt()
    {
        var (store, clock) = Create();
        for (var i = 0; i < 10; i++)
        {
            store.Set("user", Track());
            clock.Now += TimeSpan.FromSeconds(35);
            Assert.NotNull(store.Get("user"));
        }

        clock.Now += TimeSpan.FromSeconds(60);
        Assert.Null(store.Get("user"));
    }

    [Fact]
    public void WithLength_LastsForTheTrack()
    {
        var (store, clock) = Create();
        store.Set("user", Track(length: 300));

        clock.Now += TimeSpan.FromSeconds(299);
        Assert.NotNull(store.Get("user"));
        clock.Now += TimeSpan.FromSeconds(2);
        Assert.Null(store.Get("user"));
    }

    [Fact]
    public void Timeout_IsConfigurable()
    {
        var (store, clock) = Create(timeoutSeconds: 600);
        store.Set("user", Track());

        clock.Now += TimeSpan.FromSeconds(599);
        Assert.NotNull(store.Get("user"));
        clock.Now += TimeSpan.FromSeconds(2);
        Assert.Null(store.Get("user"));
    }
}

/// <summary>The "now playing" block of a profile, and the fragment live.js refreshes it with.</summary>
public sealed class NowPlayingPageTests
{
    /// <summary>Answers every outgoing request of the app with 404, so an enabled connector finds nothing.</summary>
    private sealed class NoInternet : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static ScrobbleInput Track(string artist, string title, string? album = null, long listenedAt = 0) =>
        new() { Title = title, Artists = [artist], Album = album, ListenedAt = listenedAt };

    [Fact]
    public async Task Profile_ShowsTheCover_AndLinksWhatTheListenerScrobbledBefore()
    {
        using var factory = new MuseFmFactory(null, s => s.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => new NoInternet())));
        var (listener, _) = await factory.CreateUserAsync("np-listener");
        var (other, _) = await factory.CreateUserAsync("np-other");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var (track, artist, album, othersAlbum) = await factory.WithScopeAsync(async sp =>
        {
            var scrobbles = sp.GetRequiredService<ScrobbleService>();
            await scrobbles.IngestAsync(listener.Id, Track("Four Tet", "Baby", "Sixteen Oceans", now - 600));
            await scrobbles.IngestAsync(other.Id, Track("Burial", "Archangel", "Untrue", now - 600));
            var db = sp.GetRequiredService<AppDbContext>();
            return (
                (await db.Tracks.SingleAsync(t => t.Title == "Baby")).Id,
                (await db.Artists.SingleAsync(a => a.Name == "Four Tet")).Id,
                (await db.Albums.SingleAsync(a => a.Title == "Sixteen Oceans")).Id,
                (await db.Albums.SingleAsync(a => a.Title == "Untrue")).Id);
        });
        // Artwork is only shown while a metadata connector is active.
        var connectors = factory.Services.GetRequiredService<ConnectorSettings>();
        await connectors.SaveAsync(connectors.Get(ConnectorKind.Maloja) with { Enabled = true, BaseUrl = "http://maloja.test" });

        var store = factory.Services.GetRequiredService<NowPlayingStore>();
        var client = factory.CreatePlainClient();
        const string fragmentUrl = "/u/np-listener?handler=NowPlaying";

        // Nothing is playing: the block is there for the script to fill, and the fragment is empty.
        var idle = await client.GetStringAsync("/u/np-listener");
        Assert.Contains($"data-refresh=\"{fragmentUrl}\" data-refresh-seconds=\"10\"", idle);
        Assert.DoesNotContain("card nowplaying", idle);
        Assert.Equal("", (await client.GetStringAsync(fragmentUrl)).Trim());

        // Matched like a scrobble would be, whatever the spelling.
        store.Set(listener.Id, Track("four tet", "Baby (Radio Edit)", "Sixteen Oceans (Deluxe Edition)"));
        var response = await client.GetAsync(fragmentUrl);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var fragment = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", fragment);
        Assert.Contains($"<a class=\"title\" href=\"/u/np-listener/track/{track}\">Baby (Radio Edit)</a>", fragment);
        Assert.Contains($"<a href=\"/u/np-listener/artist/{artist}\">four tet</a>", fragment);
        Assert.Contains($"<a href=\"/u/np-listener/album/{album}\">Sixteen Oceans (Deluxe Edition)</a>", fragment);
        Assert.Contains($"<img src=\"/media/album/{album}\"", fragment);
        var profile = await client.GetStringAsync("/u/np-listener");
        Assert.Contains($"<a class=\"title\" href=\"/u/np-listener/track/{track}\">Baby (Radio Edit)</a>", profile);
        Assert.Contains("Now playing", profile);

        // New to this listener: their pages for it don't exist yet, but the catalog has the cover from someone else.
        store.Set(listener.Id, Track("Burial", "Archangel", "Untrue"));
        fragment = await client.GetStringAsync(fragmentUrl);
        Assert.Contains("Archangel", fragment);
        Assert.Contains($"<img src=\"/media/album/{othersAlbum}\"", fragment);
        Assert.DoesNotContain("href=", fragment);

        // Never heard by anyone: plain text, and showing it adds nothing to the catalog.
        store.Set(listener.Id, Track("Nobody Yet", "Brand New", "First Album"));
        fragment = await client.GetStringAsync(fragmentUrl);
        Assert.Contains("Brand New", fragment);
        Assert.Contains("Nobody Yet", fragment);
        Assert.DoesNotContain("href=", fragment);
        Assert.DoesNotContain("<img", fragment);
        Assert.False(await factory.WithScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Artists.AnyAsync(a => a.Name == "Nobody Yet")));

        // A private profile's fragment is as hidden as the profile.
        await factory.WithScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Users.Where(u => u.Id == listener.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsPublic, false)));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(fragmentUrl)).StatusCode);
    }
}
