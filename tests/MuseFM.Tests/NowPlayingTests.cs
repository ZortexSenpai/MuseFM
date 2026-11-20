using Microsoft.Extensions.Options;
using MuseFM.Services;

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
