using System.Xml.Linq;

namespace BankConnector.Adapters.Parsing;

/// <summary>
/// Namespace-agnostic element lookup.
///
/// ISO 20022 documents carry a namespace that encodes the message VERSION -
/// <c>…xsd:camt.053.001.02</c> versus <c>…001.08</c> - and Dutch banks ship
/// different versions of the same statement. Binding to a namespace would
/// mean a parser per version for a schema whose element names did not
/// change, so every lookup here matches on local name only.
/// </summary>
internal static class Xml
{
    public static XElement? Child(this XElement? parent, string localName)
    {
        if (parent is null) return null;

        foreach (var element in parent.Elements())
        {
            if (string.Equals(element.Name.LocalName, localName, StringComparison.Ordinal)) return element;
        }

        return null;
    }

    public static IEnumerable<XElement> Children(this XElement? parent, string localName) =>
        parent is null
            ? []
            : parent.Elements().Where(e => string.Equals(e.Name.LocalName, localName, StringComparison.Ordinal));

    /// <summary>Walks a chain of local names, stopping at the first miss.</summary>
    public static XElement? Path(this XElement? parent, params string[] localNames)
    {
        var current = parent;
        foreach (var name in localNames)
        {
            current = current.Child(name);
            if (current is null) return null;
        }

        return current;
    }

    /// <summary>
    /// Trimmed text, or null. An element that is present but empty is
    /// treated as absent: a bank that emits <c>&lt;Nm/&gt;</c> is telling us
    /// it has no name, not that the name is the empty string.
    /// </summary>
    public static string? Text(this XElement? element)
    {
        var value = element?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// Named <c>Attr</c> rather than <c>Attribute</c>: an extension method
    /// never wins against an instance method of the same name, so
    /// <c>XElement.Attribute(XName)</c> would silently shadow this one.
    /// </summary>
    public static string? Attr(this XElement? element, string localName)
    {
        if (element is null) return null;

        foreach (var attribute in element.Attributes())
        {
            if (string.Equals(attribute.Name.LocalName, localName, StringComparison.Ordinal))
            {
                var value = attribute.Value.Trim();
                return string.IsNullOrEmpty(value) ? null : value;
            }
        }

        return null;
    }
}
