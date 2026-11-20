using System.Net;
using Microsoft.EntityFrameworkCore;
using MuseFM.Data;
using MuseFM.Data.Entities;
using MuseFM.Services;

namespace MuseFM.Tests;

/// <summary>Picking an obsession on a track page and showing it on the profile for the chosen time.</summary>
public class ObsessionTests : IClassFixture<MuseFmFactory>
{
    private readonly MuseFmFactory _factory;

    public ObsessionTests(MuseFmFactory factory) => _factory = factory;

    private static ScrobbleInput S(string artist, string title, int minutesAgo) =>
        new() { Title = title, Artists = [artist], ListenedAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo).ToUnixTimeSeconds() };

    private Task IngestAsync(string userId, params ScrobbleInput[] scrobbles) =>
        _factory.WithScopeAsync(sp => sp.GetRequiredService<ScrobbleService>().IngestAsync(userId, scrobbles));

    private Task<T> Db<T>(Func<AppDbContext, Task<T>> query) => _factory.WithScopeAsync(sp => query(sp.GetRequiredService<AppDbContext>()));

    private Task<int> TrackIdAsync(string title) => Db(db => db.Tracks.Where(t => t.Title == title).Select(t => t.Id).SingleAsync());

    private Task<List<Obsession>> ObsessionsAsync(string userId) =>
        Db(db => db.Obsessions.AsNoTracking().Where(o => o.UserId == userId).OrderBy(o => o.Id).ToListAsync());

    /// <summary>Picks an obsession with the form on the track page (the token comes from Settings, so a missing track can be tried).</summary>
    private static Task<HttpResponseMessage> SetAsync(HttpClient client, string user, int trackId, string length) =>
        MuseFmFactory.PostFormAsync(client, "/settings", $"/u/{user}/track/{trackId}?handler=Obsession", new() { ["length"] = length });

    /// <summary>The track in the profile's obsession card.</summary>
    private static string Card(string user, int trackId, string title) => $"<p class=\"obsession-title\"><a href=\"/u/{user}/track/{trackId}\">{title}</a>";

    private static Task<HttpResponseMessage> RemoveAsync(HttpClient client, string user, int trackId, string? returnUrl = null)
    {
        var page = $"/u/{user}/track/{trackId}";
        return MuseFmFactory.PostFormAsync(client, page, $"{page}?handler=EndObsession", new() { ["returnUrl"] = returnUrl ?? "" });
    }

    [Fact]
    public async Task AnObsession_IsShownOnTheProfile()
    {
        var (user, _) = await _factory.CreateUserAsync("obsessed");
        await IngestAsync(user.Id, S("Röyksopp", "Eple", 30), S("Röyksopp", "Eple", 20), S("Röyksopp", "Poor Leno", 10));
        var eple = await TrackIdAsync("Eple");
        var client = await _factory.SignInAsync("obsessed");
        Assert.Contains("Set as obsession", await client.GetStringAsync($"/u/obsessed/track/{eple}"));

        var set = await SetAsync(client, "obsessed", eple, "1w");
        Assert.Equal(HttpStatusCode.Redirect, set.StatusCode);
        Assert.Equal($"/u/obsessed/track/{eple}", set.Headers.Location!.OriginalString);
        var obsession = Assert.Single(await ObsessionsAsync(user.Id));
        Assert.Equal((eple, TimeSpan.FromDays(7)), (obsession.TrackId, obsession.EndsAt - obsession.StartedAt));

        var own = await client.GetStringAsync("/u/obsessed");
        Assert.Contains("Current obsession", own);
        Assert.Contains(Card("obsessed", eple, "Eple"), own);
        Assert.Contains("2 scrobbles", own);
        Assert.Contains("shown until", own);

        // Visitors see the obsession, but not until when it is shown.
        var visitor = _factory.CreatePlainClient();
        var seen = await visitor.GetStringAsync("/u/obsessed");
        Assert.Contains(Card("obsessed", eple, "Eple"), seen);
        Assert.DoesNotContain("shown until", seen);
        Assert.Contains("Current obsession", await visitor.GetStringAsync($"/u/obsessed/track/{eple}"));
        Assert.DoesNotContain("Current obsession", await visitor.GetStringAsync($"/u/obsessed/track/{await TrackIdAsync("Poor Leno")}"));
    }

    [Fact]
    public async Task ANewObsession_EndsThePreviousOne()
    {
        var (user, _) = await _factory.CreateUserAsync("fickle");
        await IngestAsync(user.Id, S("Bonobo", "Kerala", 30), S("Bonobo", "Cirrus", 20));
        var (kerala, cirrus) = (await TrackIdAsync("Kerala"), await TrackIdAsync("Cirrus"));
        var client = await _factory.SignInAsync("fickle");

        await SetAsync(client, "fickle", kerala, "1w");
        await SetAsync(client, "fickle", cirrus, "always");
        var profile = await client.GetStringAsync("/u/fickle");
        Assert.Contains(Card("fickle", cirrus, "Cirrus"), profile);
        Assert.DoesNotContain(Card("fickle", kerala, "Kerala"), profile);
        Assert.Contains("shown until you change it", profile);
        var history = await ObsessionsAsync(user.Id);
        Assert.Equal([kerala, cirrus], history.Select(o => o.TrackId));
        Assert.True(history[0].EndsAt <= DateTime.UtcNow);
        Assert.Null(history[1].EndsAt);

        // Picking the same track again changes how long it stays, from now, but it is still the same obsession.
        await SetAsync(client, "fickle", cirrus, "1d");
        var again = (await ObsessionsAsync(user.Id))[^1];
        Assert.Equal((history[1].Id, history[1].StartedAt), (again.Id, again.StartedAt));
        Assert.InRange(again.EndsAt!.Value - DateTime.UtcNow, TimeSpan.FromHours(23), TimeSpan.FromDays(1));
    }

    [Fact]
    public async Task ExpiredAndRemovedObsessions_AreNotShown()
    {
        var (user, _) = await _factory.CreateUserAsync("moved-on");
        await IngestAsync(user.Id, S("Burial", "Archangel", 10));
        var archangel = await TrackIdAsync("Archangel");
        var client = await _factory.SignInAsync("moved-on");

        await SetAsync(client, "moved-on", archangel, "1d");
        Assert.Contains("Current obsession", await client.GetStringAsync("/u/moved-on"));
        await Db(db => db.Obsessions.Where(o => o.UserId == user.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.EndsAt, DateTime.UtcNow.AddMinutes(-1))));
        Assert.DoesNotContain("Current obsession", await client.GetStringAsync("/u/moved-on"));

        await SetAsync(client, "moved-on", archangel, "3m");
        Assert.Contains("Current obsession", await client.GetStringAsync("/u/moved-on"));
        var removed = await RemoveAsync(client, "moved-on", archangel, returnUrl: "/u/moved-on");
        Assert.Equal("/u/moved-on", removed.Headers.Location!.OriginalString);
        Assert.DoesNotContain("Current obsession", await client.GetStringAsync("/u/moved-on"));
        Assert.Equal(2, (await ObsessionsAsync(user.Id)).Count);
    }

    [Fact]
    public async Task OnlyTheOwner_PicksTheirObsession()
    {
        var (user, _) = await _factory.CreateUserAsync("picky");
        await _factory.CreateUserAsync("meddler");
        await IngestAsync(user.Id, S("Four Tet", "Baby", 10));
        var baby = await TrackIdAsync("Baby");

        var meddler = await _factory.SignInAsync("meddler");
        var page = $"/u/picky/track/{baby}";
        Assert.DoesNotContain("Set as obsession", await meddler.GetStringAsync(page));
        Assert.Equal(HttpStatusCode.NotFound, (await SetAsync(meddler, "picky", baby, "1w")).StatusCode);

        var owner = await _factory.SignInAsync("picky");
        Assert.Equal(HttpStatusCode.BadRequest, (await SetAsync(owner, "picky", baby, "forever-and-ever")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SetAsync(owner, "picky", 999_999, "1w")).StatusCode);
        Assert.Empty(await ObsessionsAsync(user.Id));
    }

    [Fact]
    public async Task MergingTracks_KeepsTheObsession()
    {
        var (user, _) = await _factory.CreateUserAsync("obsessed-merge");
        await IngestAsync(user.Id, S("Wrong Band", "Unfinished Sympathy", 30), S("Massive Attack", "Unfinished Sympathy", 20));
        var (wrong, right) = await Db(async db => (
            await db.Tracks.Where(t => t.Title == "Unfinished Sympathy" && t.Artists.Any(a => a.Artist.Name == "Wrong Band")).Select(t => t.Id).SingleAsync(),
            await db.Tracks.Where(t => t.Title == "Unfinished Sympathy" && t.Artists.Any(a => a.Artist.Name == "Massive Attack")).Select(t => t.Id).SingleAsync()));
        await SetAsync(await _factory.SignInAsync("obsessed-merge"), "obsessed-merge", wrong, "1m");

        var edit = await _factory.WithScopeAsync(sp => sp.GetRequiredService<CatalogEditor>().MergeAsync(ChartKind.Tracks, wrong, right));
        Assert.True(edit.Ok, edit.Message);
        Assert.Equal(right, Assert.Single(await ObsessionsAsync(user.Id)).TrackId);
        Assert.Contains(Card("obsessed-merge", right, "Unfinished Sympathy"), await _factory.CreatePlainClient().GetStringAsync("/u/obsessed-merge"));
    }
}
