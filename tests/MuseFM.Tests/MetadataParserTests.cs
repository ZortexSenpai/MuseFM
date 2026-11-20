using MuseFM.Services;

namespace MuseFM.Tests;

public class MetadataParserTests
{
    [Theory]
    [InlineData("Daft Punk", "Get Lucky", "Get Lucky", new[] { "Daft Punk" })]
    [InlineData("Daft Punk feat. Pharrell Williams", "Get Lucky", "Get Lucky", new[] { "Daft Punk", "Pharrell Williams" })]
    [InlineData("Daft Punk ft. Pharrell Williams & Nile Rodgers", "Get Lucky", "Get Lucky", new[] { "Daft Punk", "Pharrell Williams", "Nile Rodgers" })]
    [InlineData("Daft Punk", "Get Lucky (feat. Pharrell Williams)", "Get Lucky", new[] { "Daft Punk", "Pharrell Williams" })]
    [InlineData("Daft Punk", "Get Lucky [Featuring Pharrell Williams, Nile Rodgers] (Radio Edit)", "Get Lucky (Radio Edit)", new[] { "Daft Punk", "Pharrell Williams", "Nile Rodgers" })]
    [InlineData("Simon & Garfunkel", "The Boxer", "The Boxer", new[] { "Simon & Garfunkel" })]
    [InlineData("  Röyksopp  ", "  What   Else Is There? ", "What Else Is There?", new[] { "Röyksopp" })]
    [InlineData("A feat. B", "Song (feat. b)", "Song", new[] { "A", "B" })]
    public void Parse_SplitsFeaturedArtists(string artist, string title, string expectedTitle, string[] expectedArtists)
    {
        var (parsedTitle, artists) = MetadataParser.Parse(title, [artist]);

        Assert.Equal(expectedTitle, parsedTitle);
        Assert.Equal(expectedArtists, artists);
    }

    [Fact]
    public void Parse_UsesAllArtistFields()
    {
        var (_, artists) = MetadataParser.Parse("Song", ["A", "B", "a"]);
        Assert.Equal(["A", "B"], artists);
    }

    [Fact]
    public void Credit_JoinsFeaturedArtists()
    {
        Assert.Equal("A", MetadataParser.Credit(["A"]));
        Assert.Equal("A feat. B, C", MetadataParser.Credit(["A", "B", "C"]));
        // The credit must parse back into the same artists.
        Assert.Equal(["A", "B", "C"], MetadataParser.Parse("x", [MetadataParser.Credit(["A", "B", "C"])]).Artists);
    }

    [Fact]
    public void TextKey_IsCaseAndWhitespaceInsensitive()
    {
        Assert.Equal(TextKey.Key(TextKey.Clean("The  Beatles")), TextKey.Key(TextKey.Clean("the beatles ")));
        Assert.Equal(TextKey.Key(TextKey.Clean("ﬁve")), TextKey.Key(TextKey.Clean("five"))); // compatibility ligature
    }
}
