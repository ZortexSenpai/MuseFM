using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using MuseFM.Data.Entities;
using MuseFM.Services;

namespace MuseFM.Tests;

/// <summary>Uploading, showing, replacing and removing profile pictures and banners.</summary>
public class ProfileImagesTests : IClassFixture<MuseFmFactory>
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    private static readonly byte[] Gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

    private readonly MuseFmFactory _factory;

    public ProfileImagesTests(MuseFmFactory factory) => _factory = factory;

    private Task<AppUser> FindAsync(string name) =>
        _factory.WithScopeAsync(async sp => (await sp.GetRequiredService<UserManager<AppUser>>().FindByNameAsync(name))!);

    private string FileOnDisk(string file) => Path.Combine(_factory.DataDirectory, "profiles", file);

    /// <summary>Uploads through Settings → Profile and returns the message the page shows afterwards.</summary>
    private static async Task<string> UploadAsync(HttpClient client, ProfileImageKind kind, byte[] bytes, string fileName = "me.png", string contentType = "image/png")
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(await MuseFmFactory.GetAntiforgeryTokenAsync(client, "/settings")), "__RequestVerificationToken");
        form.Add(new StringContent(kind.ToString()), "kind");
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        var response = await client.PostAsync("/settings?handler=Image", form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return Alert(await client.GetStringAsync("/settings"));
    }

    private static async Task<string> RemoveAsync(HttpClient client, ProfileImageKind kind)
    {
        var response = await MuseFmFactory.PostFormAsync(client, "/settings", "/settings?handler=RemoveImage", new() { ["kind"] = kind.ToString() });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return Alert(await client.GetStringAsync("/settings"));
    }

    private static string Alert(string html)
    {
        var start = html.IndexOf("<div class=\"alert", StringComparison.Ordinal);
        Assert.True(start >= 0, "No message on the page");
        var end = html.IndexOf("</div>", start, StringComparison.Ordinal);
        return WebUtility.HtmlDecode(html[(html.IndexOf('>', start) + 1)..end]);
    }

    [Fact]
    public async Task UploadedImages_AppearOnTheProfileAndInLists()
    {
        await _factory.CreateUserAsync("pictured");
        var client = await _factory.SignInAsync("pictured");

        Assert.Equal("Profile picture saved.", await UploadAsync(client, ProfileImageKind.Avatar, Png));
        Assert.Equal("Banner saved.", await UploadAsync(client, ProfileImageKind.Banner, Gif, "wide.gif", "image/gif"));
        var user = await FindAsync("pictured");
        Assert.Matches("^[0-9a-f]{32}\\.png$", user.AvatarFile);
        Assert.Matches("^[0-9a-f]{32}\\.gif$", user.BannerFile);

        var profile = await _factory.CreatePlainClient().GetStringAsync("/u/pictured");
        Assert.Contains($"<img src=\"/media/profile/{user.AvatarFile}\"", profile);
        Assert.Contains($"<div class=\"profile-banner\"><img src=\"/media/profile/{user.BannerFile}\"", profile);
        Assert.Contains("profile-head with-banner", profile);
        Assert.Contains($"/media/profile/{user.AvatarFile}", await client.GetStringAsync("/search?q=pictured"));

        // Served as the type its content shows, and cacheable for good: a new upload gets a new name.
        var image = await _factory.CreatePlainClient().GetAsync($"/media/profile/{user.AvatarFile}");
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType!.MediaType);
        Assert.Contains("immutable", image.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff", image.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(Png, await image.Content.ReadAsByteArrayAsync());
        var banner = await _factory.CreatePlainClient().GetAsync($"/media/profile/{user.BannerFile}");
        Assert.Equal("image/gif", banner.Content.Headers.ContentType!.MediaType);

        // Without images, the header goes back to the initial and no banner.
        await RemoveAsync(client, ProfileImageKind.Banner);
        await RemoveAsync(client, ProfileImageKind.Avatar);
        profile = await _factory.CreatePlainClient().GetStringAsync("/u/pictured");
        Assert.DoesNotContain("/media/profile/", profile);
        Assert.DoesNotContain("profile-banner", profile);
        Assert.Matches("<span class=\"avatar tone-[1-6]\" aria-hidden=\"true\">P</span>", profile);
    }

    [Fact]
    public async Task ReplacingOrRemovingAnImage_DeletesTheOldOne()
    {
        await _factory.CreateUserAsync("replacer");
        var client = await _factory.SignInAsync("replacer");
        await UploadAsync(client, ProfileImageKind.Avatar, Png);
        var first = (await FindAsync("replacer")).AvatarFile!;

        await UploadAsync(client, ProfileImageKind.Avatar, Gif, "next.gif", "image/gif");
        var second = (await FindAsync("replacer")).AvatarFile!;
        Assert.NotEqual(first, second);
        Assert.False(File.Exists(FileOnDisk(first)));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/media/profile/{first}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/media/profile/{second}")).StatusCode);

        Assert.Equal("Profile picture removed.", await RemoveAsync(client, ProfileImageKind.Avatar));
        Assert.Null((await FindAsync("replacer")).AvatarFile);
        Assert.False(File.Exists(FileOnDisk(second)));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/media/profile/{second}")).StatusCode);
    }

    [Fact]
    public async Task OnlyImagesAreAccepted()
    {
        await _factory.CreateUserAsync("uploader");
        var client = await _factory.SignInAsync("uploader");

        // Judged by content: an SVG (which can carry scripts) named .png is refused, as is anything that isn't an image.
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray();
        Assert.Equal("Upload a JPEG, PNG, GIF or WebP image.", await UploadAsync(client, ProfileImageKind.Avatar, svg));
        Assert.Equal("Upload a JPEG, PNG, GIF or WebP image.", await UploadAsync(client, ProfileImageKind.Banner, Encoding.UTF8.GetBytes("just some text here"), "notes.txt", "text/plain"));
        Assert.Equal("Choose an image to upload.", await UploadAsync(client, ProfileImageKind.Avatar, []));

        var tooLarge = new byte[ProfileImages.MaxBytes + 1];
        Png.CopyTo(tooLarge, 0);
        Assert.Equal("That image is too large. Upload one of at most 10 MB.", await UploadAsync(client, ProfileImageKind.Banner, tooLarge));

        var user = await FindAsync("uploader");
        Assert.Equal((null, null), (user.AvatarFile, user.BannerFile));
    }

    [Fact]
    public async Task OnlyCurrentImagesAreServed()
    {
        var client = _factory.CreatePlainClient();
        // A name in the right shape whose file exists, but which no user has.
        var orphan = Guid.NewGuid().ToString("N") + ".png";
        await File.WriteAllBytesAsync(FileOnDisk(orphan), Png);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/media/profile/{orphan}")).StatusCode);

        foreach (var name in new[] { "..%2Fmusefm.db", "musefm.db", "ABCDEF0123456789ABCDEF0123456789.png", Guid.NewGuid().ToString("N") + ".svg" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/media/profile/{name}")).StatusCode);
    }

    [Fact]
    public async Task Admins_CanRemoveImages_AndDeletingAUserDeletesThem()
    {
        await _factory.CreateUserAsync("images-admin", admin: true);
        var admin = await _factory.SignInAsync("images-admin");
        await _factory.CreateUserAsync("images-user");
        var client = await _factory.SignInAsync("images-user");
        await UploadAsync(client, ProfileImageKind.Avatar, Png);
        await UploadAsync(client, ProfileImageKind.Banner, Gif, "wide.gif", "image/gif");
        var user = await FindAsync("images-user");
        var (avatar, banner) = (user.AvatarFile!, user.BannerFile!);

        var page = $"/admin/users/{user.Id}";
        Assert.Contains($"/media/profile/{banner}", await admin.GetStringAsync(page));
        var removed = await MuseFmFactory.PostFormAsync(admin, page, $"{page}?handler=RemoveImage", new() { ["kind"] = "Banner" });
        Assert.Equal(HttpStatusCode.Redirect, removed.StatusCode);
        Assert.Contains("Banner removed.", await admin.GetStringAsync(page));
        user = await FindAsync("images-user");
        Assert.Equal((avatar, null), (user.AvatarFile, user.BannerFile));
        Assert.False(File.Exists(FileOnDisk(banner)));

        var deleted = await MuseFmFactory.PostFormAsync(admin, page, $"{page}?handler=Delete", new() { ["confirmName"] = "images-user" });
        Assert.Equal(HttpStatusCode.Redirect, deleted.StatusCode);
        Assert.False(File.Exists(FileOnDisk(avatar)));
    }

    [Fact]
    public async Task LastFmFriends_IncludeTheirPictures()
    {
        var (reader, _) = await _factory.CreateUserAsync("lfm-viewer");
        var (friend, _) = await _factory.CreateUserAsync("lfm-pictured");
        await _factory.WithScopeAsync(async sp =>
        {
            await sp.GetRequiredService<SocialService>().FollowAsync(reader.Id, friend);
            return 0;
        });
        await UploadAsync(await _factory.SignInAsync("lfm-pictured"), ProfileImageKind.Avatar, Png);
        var file = (await FindAsync("lfm-pictured")).AvatarFile;

        var json = await _factory.CreatePlainClient().GetFromJsonAsync<JsonElement>("/apis/audioscrobbler/2.0/?format=json&method=user.getfriends&user=lfm-viewer");
        var images = Assert.Single(json.GetProperty("friends").GetProperty("user").EnumerateArray()).GetProperty("image");
        Assert.Equal(4, images.GetArrayLength());
        Assert.All(images.EnumerateArray(), image => Assert.EndsWith($"/media/profile/{file}", image.GetProperty("#text").GetString()));
    }
}
