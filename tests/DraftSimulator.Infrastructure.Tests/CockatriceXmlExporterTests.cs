using System.Xml.Linq;
using DraftSimulator.Core;
using DraftSimulator.Infrastructure;

namespace DraftSimulator.Infrastructure.Tests;

public sealed class CockatriceXmlExporterTests
{
    [Fact]
    public void WritesVersionFourFallbackShapeAndEscapesNames()
    {
        var id = CardDefinitionId.New();
        var definition = new CardDefinition(id, "host-only.png", "Fire & <Ice>", Rarity.MythicRare);
        using var stream = new MemoryStream();

        new CockatriceXmlExporter().Export(stream, [definition]);

        var raw = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("Fire &amp; &lt;Ice&gt;", raw);
        stream.Position = 0;
        var document = XDocument.Load(stream);
        var root = Assert.IsType<XElement>(document.Root);
        Assert.Equal("cockatrice_carddatabase", root.Name.LocalName);
        Assert.Equal("4", root.Attribute("version")?.Value);
        var setDefinition = Assert.Single(root.Element("sets")!.Elements("set"));
        Assert.Equal("DRAFT", setDefinition.Element("name")?.Value);
        Assert.Equal("Draft Simulator Export", setDefinition.Element("longname")?.Value);
        Assert.Equal("Custom", setDefinition.Element("settype")?.Value);
        var card = Assert.Single(root.Element("cards")!.Elements("card"));
        Assert.Equal("Fire & <Ice>", card.Element("name")?.Value);
        Assert.Equal(string.Empty, card.Element("text")?.Value);
        Assert.Equal("Unknown", card.Element("prop")?.Element("type")?.Value);
        Assert.Equal("Unknown", card.Element("prop")?.Element("maintype")?.Value);
        Assert.Equal("3", card.Element("tablerow")?.Value);
        Assert.Equal("DRAFT", card.Element("set")?.Value);
        Assert.Equal("mythic rare", card.Element("set")?.Attribute("rarity")?.Value);
        Assert.Equal(id.Value.ToString("D"), card.Element("set")?.Attribute("uuid")?.Value);
        Assert.Null(setDefinition.Element("releasedate"));
    }

    [Fact]
    public void BlocksDuplicateFinalNamesCaseInsensitively()
    {
        CardDefinition[] definitions =
        [
            new(CardDefinitionId.New(), "one.png", "Card", Rarity.Common),
            new(CardDefinitionId.New(), "two.png", "card", Rarity.Rare),
        ];
        Assert.Throws<CockatriceExportException>(() =>
            new CockatriceXmlExporter().Export(new MemoryStream(), definitions));
    }

    [Theory]
    [InlineData(Rarity.Special, "special")]
    [InlineData(Rarity.Bonus, "bonus")]
    public void ExportsAddedScryfallRarities(Rarity rarity, string expected)
    {
        var definition = new CardDefinition(CardDefinitionId.New(), "card.png", "Card", rarity);
        using var output = new MemoryStream();

        new CockatriceXmlExporter().Export(output, [definition]);

        output.Position = 0;
        var card = XDocument.Load(output).Root!.Element("cards")!.Element("card")!;
        Assert.Equal(expected, card.Element("set")!.Attribute("rarity")!.Value);
    }
}
