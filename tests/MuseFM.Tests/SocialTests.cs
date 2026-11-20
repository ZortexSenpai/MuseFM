using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MuseFM.Data;
using MuseFM.Data.Entities;
using MuseFM.Services;

namespace MuseFM.Tests;

/// <summary>Following, the feed, taste comparison and the Last.fm read methods.</summary>
public class SocialTests : IClassFixture<MuseFmFactory>
{
    private const string Endpoint = "/apis/audioscrobbler/2.0/";
    private readonly MuseFmFactory _factory;

    public SocialTests(MuseFmFactory factory) => _factory = factory;

    private static long Ago(int minutes) => DateTimeOffset.UtcNow.AddMinutes(-minutes).ToUnixTimeSeconds();

    private static ScrobbleInput S(string artist, string title, int minutesAgo, string? album = null) =>
        new() { Title = title, Artists = [artist], Album = album, ListenedAt = Ago(minutesAgo) };

    private Task IngestAsync(string userId, params ScrobbleInput[] scrobbles) =>
        _factory.WithScopeAsync(sp => sp.GetRequiredService<ScrobbleService>().IngestAsync(userId, scrobbles));

    private Task<T> Social<T>(Func<SocialService, Task<T>> action) => _factory.WithScopeAsync(sp => action(sp.GetRequiredService<SocialService>()));

    private Task FollowAsync(AppUser follower, AppUser followee) => Social(s => s.FollowAsync(follower.Id, followee));

    private async Task MakePrivateAsync(AppUser user) =>
        await _factory.WithScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Users.Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsPublic, false)));

    [Fact]
    public async Task FollowButton_FollowsAndUnfollowsPublicProfiles()
    {
        var (fan, _) = await _factory.CreateUserAsync("follow-fan");
        var (star, _) = await _factory.CreateUserAsync("follow-star");
        await _factory.CreateUserAsync("follow-hidden", isPublic: false);
        await IngestAsync(star.Id, S("Caribou", "Odessa", 5));

        var client = await _factory.SignInAsync("follow-fan");
        var profile = await client.GetStringAsync("/u/follow-star");
        Assert.Contains("name=\"follow\" value=\"true\"", profile);
        var follow = await MuseFmFactory.PostFormAsync(client, "/u/follow-star", "/u/follow-star/friends?handler=Follow",
            new() { ["follow"] = "true", ["returnUrl"] = "/u/follow-star" });
        Assert.Equal("/u/follow-star", follow.Headers.Location!.OriginalString);
        Assert.Contains("aria-pressed=\"true\"", await client.GetStringAsync("/u/follow-star"));
        Assert.Contains("1 follower<", await client.GetStringAsync("/u/follow-star"));

        var friends = await client.GetStringAsync("/u/follow-fan/friends");
        Assert.Contains("/u/follow-star\"", friends);
        Assert.Contains("Odessa", friends);
        Assert.Contains("/u/follow-fan\"", await client.GetStringAsync("/u/follow-star/friends"));

        // Private profiles can't be followed, and nobody follows themselves.
        await MuseFmFactory.PostFormAsync(client, "/u/follow-star", "/u/follow-hidden/friends?handler=Follow", new() { ["follow"] = "true" });
        await MuseFmFactory.PostFormAsync(client, "/u/follow-star", "/u/follow-fan/friends?handler=Follow", new() { ["follow"] = "true" });
        Assert.Equal(1, await _factory.WithScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Follows.CountAsync(f => f.FollowerId == fan.Id)));

        // Signed out there is no button.
        Assert.DoesNotContain("handler=Follow", await _factory.CreatePlainClient().GetStringAsync("/u/follow-star"));

        // Unfollowing still works after the profile went private.
        await MakePrivateAsync(star);
        var unfollow = await MuseFmFactory.PostFormAsync(client, "/u/follow-fan", "/u/follow-star/friends?handler=Follow", new() { ["follow"] = "false" });
        Assert.Equal(HttpStatusCode.Redirect, unfollow.StatusCode);
        Assert.False(await Social(s => s.FollowsAsync(fan.Id, star.Id)));

        // Otherwise a private profile answers like a name nobody has, so the button can't be used to find out who exists.
        foreach (var name in new[] { "follow-hidden", "follow-star", "follow-nobody" })
        {
            var probe = await MuseFmFactory.PostFormAsync(client, "/u/follow-fan", $"/u/{name}/friends?handler=Follow", new() { ["follow"] = "false" });
            Assert.Equal(HttpStatusCode.NotFound, probe.StatusCode);
        }
    }

    [Fact]
    public async Task Feed_ShowsFollowedUsersNewestFirst_WithoutPrivateOnes()
    {
        var (reader, _) = await _factory.CreateUserAsync("feed-reader");
        var (one, _) = await _factory.CreateUserAsync("feed-one");
        var (two, _) = await _factory.CreateUserAsync("feed-two");
        var (gone, _) = await _factory.CreateUserAsync("feed-gone");
        await IngestAsync(one.Id, S("Bonobo", "Kerala", 30), S("Bonobo", "Cirrus", 10));
        await IngestAsync(two.Id, S("Four Tet", "Baby", 20));
        await IngestAsync(gone.Id, S("Burial", "Archangel", 5));
        foreach (var user in new[] { one, two, gone }) await FollowAsync(reader, user);
        await MakePrivateAsync(gone);

        var first = await Social(s => s.FeedAsync(reader.Id, false, null, 2));
        Assert.Equal([("feed-one", "Cirrus"), ("feed-two", "Baby")], first.Rows.Select(r => (r.UserName, r.Scrobble.Title)));
        var second = await Social(s => s.FeedAsync(reader.Id, false, first.Next, 2));
        Assert.Equal(["Kerala"], second.Rows.Select(r => r.Scrobble.Title));
        Assert.Null(second.Next);

        _factory.Services.GetRequiredService<NowPlayingStore>().Set(two.Id, new ScrobbleInput { Title = "Planet", Artists = ["Four Tet"], ListenedAt = Ago(0) });
        var client = await _factory.SignInAsync("feed-reader");
        var page = await client.GetStringAsync("/feed");
        Assert.Contains("is listening to", page);
        Assert.Contains("Planet", page);
        Assert.Contains("Cirrus", page);
        Assert.DoesNotContain("Archangel", page);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/feed?before={first.Next}")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await _factory.CreatePlainClient().GetAsync("/feed")).StatusCode);
    }

    [Fact]
    public async Task Taste_ScoresTheOverlapOfTwoUsers()
    {
        var (a, _) = await _factory.CreateUserAsync("taste-a");
        var (b, _) = await _factory.CreateUserAsync("taste-b");
        var (c, _) = await _factory.CreateUserAsync("taste-c");
        var (d, _) = await _factory.CreateUserAsync("taste-d");
        await IngestAsync(a.Id, S("Taste Alpha", "1", 50), S("Taste Alpha", "2", 40), S("Taste Beta", "3", 30), S("Taste Gamma", "4", 20));
        await IngestAsync(b.Id, S("Taste Alpha", "1", 50), S("Taste Alpha", "2", 40), S("Taste Beta", "3", 30), S("Taste Gamma", "4", 20));
        await IngestAsync(c.Id, S("Taste Delta", "5", 50));
        await IngestAsync(d.Id, S("Taste Alpha", "1", 50), S("Taste Epsilon", "6", 40), S("Taste Zeta", "7", 30));

        var year = StatPeriod.Create(StatRange.Year, TimeZoneInfo.Utc).Window;
        var same = await Social(s => s.CompareAsync(a.Id, b.Id, year));
        Assert.Equal((1.0, "Super", 3), (Math.Round(same.Score, 6), same.Level, same.SharedCount));
        Assert.Empty(same.OnlyOther);

        var none = await Social(s => s.CompareAsync(a.Id, c.Id, year));
        Assert.Equal((0.0, "Very low", 0), (none.Score, none.Level, none.SharedCount));

        var some = await Social(s => s.CompareAsync(a.Id, d.Id, year));
        Assert.InRange(some.Score, 0.2, 0.8);
        Assert.Equal(["Taste Alpha"], some.Shared.Select(x => x.Name));
        Assert.Equal((2, 1), (some.Shared[0].Plays, some.Shared[0].OtherPlays));
        Assert.Equal(["Taste Epsilon", "Taste Zeta"], some.OnlyOther.Select(x => x.Title).Order());
        Assert.Equal(["Taste Beta", "Taste Gamma"], some.OnlyUser.Select(x => x.Title).Order());

        var client = await _factory.SignInAsync("taste-b");
        var page = WebUtility.HtmlDecode(await client.GetStringAsync("/u/taste-a/taste"));
        Assert.Contains("taste-a and taste-b", page);
        Assert.Contains("100%", page);
        Assert.Contains("There is no user called “nobody”.", WebUtility.HtmlDecode(await client.GetStringAsync("/u/taste-a/taste?with=nobody")));
        await MakePrivateAsync(c);
        Assert.Contains("There is no user called “taste-c”.", WebUtility.HtmlDecode(await client.GetStringAsync("/u/taste-a/taste?with=taste-c&range=2024")));
    }

    [Fact]
    public async Task LastFmReadMethods_ReturnLastFmShapedJsonAndXml()
    {
        var (user, token) = await _factory.CreateUserAsync("lfm-reader");
        var (friend, _) = await _factory.CreateUserAsync("lfm-friend");
        await IngestAsync(user.Id,
            S("Röyksopp", "Eple", 400, "Melody A.M."), S("Röyksopp", "Eple", 300, "Melody A.M."),
            S("Röyksopp", "Poor Leno", 200, "Melody A.M."), S("Air", "La femme d'argent", 60 * 24 * 10, "Moon Safari"));
        await FollowAsync(user, friend);
        var eple = await _factory.WithScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Tracks.Where(t => t.Title == "Eple").Select(t => t.Id).SingleAsync());
        await _factory.WithScopeAsync(async sp =>
        {
            await sp.GetRequiredService<FeedbackService>().SetAsync(user.Id, eple, TrackFeedback.Love);
            return 0;
        });
        _factory.Services.GetRequiredService<NowPlayingStore>().Set(user.Id, new ScrobbleInput { Title = "Sparks", Artists = ["Röyksopp"], ListenedAt = Ago(0) });
        var client = _factory.CreatePlainClient();

        Task<JsonElement> Json(string query) => client.GetFromJsonAsync<JsonElement>($"{Endpoint}?format=json&{query}");

        var recent = (await Json("method=user.getrecenttracks&user=lfm-reader&limit=2&extended=1")).GetProperty("recenttracks");
        var tracks = recent.GetProperty("track");
        Assert.Equal(3, tracks.GetArrayLength()); // now playing + 2
        Assert.Equal("true", tracks[0].GetProperty("@attr").GetProperty("nowplaying").GetString());
        Assert.Equal("Poor Leno", tracks[1].GetProperty("name").GetString());
        Assert.Equal("Röyksopp", tracks[1].GetProperty("artist").GetProperty("name").GetString());
        Assert.Equal("1", tracks[2].GetProperty("loved").GetString());
        Assert.Equal("Melody A.M.", tracks[1].GetProperty("album").GetProperty("#text").GetString());
        Assert.Matches("^[0-9]+$", tracks[1].GetProperty("date").GetProperty("uts").GetString()!);
        Assert.Equal(4, tracks[1].GetProperty("image").GetArrayLength());
        Assert.Equal(("4", "2", "1"), (recent.GetProperty("@attr").GetProperty("total").GetString(), recent.GetProperty("@attr").GetProperty("totalPages").GetString(), recent.GetProperty("@attr").GetProperty("page").GetString()));

        var fromNow = (await Json($"method=user.getrecenttracks&user=lfm-reader&from={Ago(250)}")).GetProperty("recenttracks");
        Assert.Equal("1", fromNow.GetProperty("@attr").GetProperty("total").GetString());

        var top = (await Json("method=user.gettopartists&user=lfm-reader&period=7day")).GetProperty("topartists");
        var artist = Assert.Single(top.GetProperty("artist").EnumerateArray());
        Assert.Equal(("Röyksopp", "3", "1"), (artist.GetProperty("name").GetString(), artist.GetProperty("playcount").GetString(), artist.GetProperty("@attr").GetProperty("rank").GetString()));
        Assert.Equal(2, (await Json("method=user.gettopartists&user=lfm-reader&period=overall")).GetProperty("topartists").GetProperty("artist").GetArrayLength());

        var albums = (await Json("method=user.gettopalbums&user=lfm-reader")).GetProperty("topalbums").GetProperty("album");
        Assert.Equal(("Melody A.M.", "Röyksopp"), (albums[0].GetProperty("name").GetString(), albums[0].GetProperty("artist").GetProperty("name").GetString()));
        var toptracks = (await Json("method=user.gettoptracks&user=lfm-reader&limit=1")).GetProperty("toptracks");
        Assert.Equal(("Eple", "2"), (toptracks.GetProperty("track")[0].GetProperty("name").GetString(), toptracks.GetProperty("track")[0].GetProperty("playcount").GetString()));

        var loved = (await Json("method=user.getlovedtracks&user=lfm-reader")).GetProperty("lovedtracks").GetProperty("track");
        Assert.Equal("Eple", Assert.Single(loved.EnumerateArray()).GetProperty("name").GetString());
        var friends = (await Json("method=user.getfriends&user=lfm-reader")).GetProperty("friends").GetProperty("user");
        Assert.Equal("lfm-friend", Assert.Single(friends.EnumerateArray()).GetProperty("name").GetString());
        var noFriends = (await Json("method=user.getfriends&user=lfm-friend")).GetProperty("friends");
        Assert.Equal(0, noFriends.GetProperty("user").GetArrayLength());

        var info = (await Json("method=track.getinfo&artist=r%C3%B6yksopp&track=EPLE&username=lfm-reader")).GetProperty("track");
        Assert.Equal(("Eple", "2", "1"), (info.GetProperty("name").GetString(), info.GetProperty("userplaycount").GetString(), info.GetProperty("userloved").GetString()));
        Assert.Equal("Melody A.M.", info.GetProperty("album").GetProperty("title").GetString());
        var missing = await client.GetAsync($"{Endpoint}?format=json&method=track.getinfo&artist=Nobody&track=Nothing");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // XML is the default.
        var xml = XDocument.Parse(await client.GetStringAsync($"{Endpoint}?method=user.getrecenttracks&user=lfm-reader&limit=1"));
        var list = xml.Root!.Element("recenttracks")!;
        Assert.Equal("lfm-reader", list.Attribute("user")!.Value);
        Assert.Equal("true", list.Elements("track").First().Attribute("nowplaying")!.Value);
        Assert.NotNull(list.Elements("track").Last().Element("date")!.Attribute("uts"));

        // Private users only with their own session.
        await MakePrivateAsync(user);
        var hidden = await client.GetAsync($"{Endpoint}?format=json&method=user.gettopartists&user=lfm-reader");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        var login = await client.PostAsync(Endpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["method"] = "auth.getMobileSession", ["username"] = "lfm-reader", ["password"] = token,
        }));
        var sk = XDocument.Parse(await login.Content.ReadAsStringAsync()).Root!.Element("session")!.Element("key")!.Value;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Endpoint}?format=json&method=user.gettopartists&user=lfm-reader&sk={sk}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Endpoint}?format=json&method=user.getrecenttracks&sk={sk}")).StatusCode);
    }
}
