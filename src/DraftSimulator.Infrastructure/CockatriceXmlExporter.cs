using System.Text;
using System.Xml;
using DraftSimulator.Core;

namespace DraftSimulator.Infrastructure;

public sealed class CockatriceExportException(string message) : Exception(message);

public sealed class CockatriceXmlExporter
{
    public void Export(Stream output, IEnumerable<CardDefinition> definitions, ICardMetadataProvider? metadataProvider = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(definitions);
        var cards = definitions.Select(definition =>
        {
            var metadata = metadataProvider?.GetMetadata(definition) ?? definition.Metadata;
            var name = string.IsNullOrWhiteSpace(metadata?.Name) ? definition.FallbackName.Trim() : metadata.Name.Trim();
            return (Definition: definition, Name: name);
        }).ToArray();

        if (cards.Any(x => string.IsNullOrWhiteSpace(x.Name)))
            throw new CockatriceExportException("Final card names must not be empty.");
        if (cards.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
            throw new CockatriceExportException("Final card names must be globally unique.");

        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, CloseOutput = false };
        using var writer = XmlWriter.Create(output, settings);
        writer.WriteStartDocument();
        writer.WriteStartElement("cockatrice_carddatabase");
        writer.WriteAttributeString("version", "4");
        writer.WriteStartElement("sets");
        writer.WriteStartElement("set");
        writer.WriteElementString("name", "DRAFT");
        writer.WriteElementString("longname", "Draft Simulator Export");
        writer.WriteElementString("settype", "Custom");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteStartElement("cards");
        foreach (var card in cards)
        {
            writer.WriteStartElement("card");
            writer.WriteElementString("name", card.Name);
            writer.WriteElementString("text", string.Empty);
            writer.WriteStartElement("prop");
            writer.WriteElementString("type", "Unknown");
            writer.WriteElementString("maintype", "Unknown");
            writer.WriteEndElement();
            writer.WriteStartElement("set");
            writer.WriteAttributeString("rarity", RarityName(card.Definition.Rarity));
            writer.WriteAttributeString("uuid", card.Definition.Id.Value.ToString("D"));
            writer.WriteString("DRAFT");
            writer.WriteEndElement();
            writer.WriteElementString("tablerow", "3");
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static string RarityName(Rarity rarity) => rarity switch
    {
        Rarity.Common => "common",
        Rarity.Uncommon => "uncommon",
        Rarity.Rare => "rare",
        Rarity.SuperRare => "super rare",
        Rarity.UltraRare => "ultra rare",
        Rarity.MythicRare => "mythic rare",
        Rarity.Special => "special",
        Rarity.Bonus => "bonus",
        _ => throw new ArgumentOutOfRangeException(nameof(rarity)),
    };
}
