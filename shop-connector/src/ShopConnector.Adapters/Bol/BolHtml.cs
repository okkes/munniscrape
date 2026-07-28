using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Connector.Kit.Errors;

namespace ShopConnector.Adapters.Bol;

/// <summary>One element the scanner matched: its tag, its opening tag verbatim, and its content.</summary>
internal readonly record struct HtmlElement(string Tag, string OpenTag, string Inner);

/// <summary>
/// The smallest HTML reader that can survive a redesign.
///
/// bol's order page has never been seen by anyone here - it needs an account
/// - so every hook the parser reaches for is a guess with a shelf life, and
/// the whole point of this file is that those guesses are strings in
/// <see cref="BolOptions"/> rather than code. The subset understood is
/// deliberately tiny and CSS-shaped, because an operator fixing a broken
/// adapter at 3am should be able to read the selector off devtools and paste
/// it into configuration:
///
/// <code>
///   [data-test=order-container]     an attribute with an exact value
///   [data-test*=order]              an attribute containing a value
///   li[data-test=order-item]        the same, narrowed to a tag
///   div.order-row                   a class token
///   time                            a bare tag
/// </code>
///
/// There is no HTML parser on this platform's allowed package list, so this
/// is hand-rolled. It is a scanner, not a DOM: it balances the matched tag,
/// respects quoted attribute values and skips comments, and it does not try
/// to repair malformed markup. That is enough to read a list of order blocks
/// and their line items, and being unable to do so is reported as
/// <see cref="ErrorCode.ProviderChanged"/> rather than guessed around.
/// </summary>
internal static partial class BolHtml
{
    /// <summary>Elements HTML gives no closing tag, so they never open a scope.</summary>
    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input",
        "link", "meta", "param", "source", "track", "wbr",
    };

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle { get; }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex CommentBlock { get; }

    [GeneratedRegex(@"<[^>]*>", RegexOptions.Singleline)]
    private static partial Regex AnyTag { get; }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Runs { get; }

    /// <summary>
    /// One attribute of an opening tag, quoted or bare. Applied only to an
    /// opening tag, which is short, so the backtracking cost is bounded.
    /// </summary>
    [GeneratedRegex("""([A-Za-z_:][-A-Za-z0-9_:.]*)\s*=\s*("([^"]*)"|'([^']*)'|([^\s"'>]+))""")]
    private static partial Regex AttributePair { get; }

    /// <summary>
    /// The selector subset. Everything is optional except that at least one
    /// part must be present, which the caller checks.
    /// </summary>
    [GeneratedRegex(@"^(?<tag>[A-Za-z][A-Za-z0-9]*)?(?:\.(?<class>[-\w]+))?(?:\[(?<attr>[-\w:]+)(?:(?<op>[*]?=)(?<val>[^\]]*))?\])?$")]
    private static partial Regex SelectorPattern { get; }

    /// <summary>
    /// A number, without letting a space join two of them.
    ///
    /// "12,50 3" must not read as 12503. Digit grouping by space is therefore
    /// not supported, which costs nothing on a page that writes thousands
    /// with a dot and is the difference between a wrong total and no total.
    /// </summary>
    [GeneratedRegex(@"-?\d+(?:[.,]\d+)*")]
    private static partial Regex NumberToken { get; }

    /// <summary>A price sitting at the end of a label, for <see cref="WithoutPrice"/>.</summary>
    [GeneratedRegex(@"\s*-?\s*\u20AC?\s*\d+(?:[.,]\d+)*\s*-?\s*$")]
    private static partial Regex TrailingPrice { get; }

    /// <summary>
    /// The first selector in the candidate list that matches anything.
    ///
    /// Ordered most-specific first, exactly like the Playwright candidate
    /// lists elsewhere in this assembly, and for the same reason: the page
    /// has no published contract, so the list is the unit of repair.
    /// </summary>
    public static IReadOnlyList<HtmlElement> All(string html, IReadOnlyList<string> selectors)
    {
        foreach (var selector in selectors)
        {
            var found = All(html, selector);
            if (found.Count > 0) return found;
        }

        return [];
    }

    public static HtmlElement? First(string html, IReadOnlyList<string> selectors)
    {
        foreach (var selector in selectors)
        {
            var found = All(html, selector);
            if (found.Count > 0) return found[0];
        }

        return null;
    }

    /// <summary>
    /// The text of the first matching element, or null when none matched.
    /// Null and empty are kept apart: an element that exists and is blank is
    /// a different finding from an element that is not there at all.
    /// </summary>
    public static string? TextOf(string html, IReadOnlyList<string> selectors) =>
        First(html, selectors) is { } element ? Text(element.Inner) : null;

    public static IReadOnlyList<HtmlElement> All(string html, string selector)
    {
        var simple = Parse(selector);
        var results = new List<HtmlElement>();
        var cursor = 0;

        while (cursor < html.Length)
        {
            var open = html.IndexOf('<', cursor);
            if (open < 0 || open + 1 >= html.Length) break;

            if (html.AsSpan(open).StartsWith("<!--", StringComparison.Ordinal))
            {
                var closed = html.IndexOf("-->", open, StringComparison.Ordinal);
                cursor = closed < 0 ? html.Length : closed + 3;
                continue;
            }

            if (!char.IsAsciiLetter(html[open + 1]))
            {
                cursor = open + 1;
                continue;
            }

            var end = TagEnd(html, open);
            if (end < 0) break;

            var openTag = html[open..(end + 1)];
            var name = TagName(openTag);

            if (Matches(simple, name, openTag))
            {
                var (inner, after) = Content(html, name, openTag, end);
                results.Add(new HtmlElement(name, openTag, inner));

                // Past the whole element, so a nested element matching the
                // same selector is not reported twice. Line items are read
                // by scanning their order block's own content, so nothing
                // needs the inner match at this level.
                cursor = after;
            }
            else
            {
                cursor = end + 1;
            }
        }

        return results;
    }

    /// <summary>An attribute's value, or null. Case-insensitive on the name, as HTML is.</summary>
    public static string? Attribute(string openTag, string name)
    {
        foreach (var pair in AttributePair.Matches(openTag).Cast<Match>())
        {
            if (!string.Equals(pair.Groups[1].Value, name, StringComparison.OrdinalIgnoreCase)) continue;

            var raw = pair.Groups[3].Success ? pair.Groups[3].Value
                : pair.Groups[4].Success ? pair.Groups[4].Value
                : pair.Groups[5].Value;

            return WebUtility.HtmlDecode(raw);
        }

        return null;
    }

    /// <summary>The first of <paramref name="names"/> the tag carries.</summary>
    public static string? Attribute(string openTag, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            if (Attribute(openTag, name) is { Length: > 0 } value) return value;
        }

        return null;
    }

    /// <summary>
    /// The first of <paramref name="names"/> carried by any tag inside the
    /// fragment.
    ///
    /// A date is the case this exists for. Pages wrap the machine-readable
    /// value one level down - <c>&lt;span data-test="order-date"&gt;&lt;time
    /// datetime="..."&gt;14 juni 2026&lt;/time&gt;&lt;/span&gt;</c> - and
    /// reading only the labelled element's own attributes throws away a
    /// timestamp that states its own offset in favour of Dutch prose that has
    /// to be interpreted. That is the wrong trade every time.
    /// </summary>
    public static string? AttributeWithin(string html, IReadOnlyList<string> names)
    {
        var cursor = 0;

        while (cursor < html.Length)
        {
            var open = html.IndexOf('<', cursor);
            if (open < 0 || open + 1 >= html.Length) break;

            if (html.AsSpan(open).StartsWith("<!--", StringComparison.Ordinal))
            {
                var closed = html.IndexOf("-->", open, StringComparison.Ordinal);
                cursor = closed < 0 ? html.Length : closed + 3;
                continue;
            }

            if (!char.IsAsciiLetter(html[open + 1]))
            {
                cursor = open + 1;
                continue;
            }

            var end = TagEnd(html, open);
            if (end < 0) break;

            if (Attribute(html[open..(end + 1)], names) is { Length: > 0 } value) return value;
            cursor = end + 1;
        }

        return null;
    }

    /// <summary>
    /// Readable text: script and style content dropped, comments dropped,
    /// tags stripped, entities decoded, whitespace collapsed.
    ///
    /// Decoding happens after the tags are stripped, never before - a page
    /// containing <c>&amp;lt;div&amp;gt;</c> would otherwise grow a tag that
    /// the stripper had already gone past.
    /// </summary>
    public static string Text(string fragment)
    {
        var stripped = AnyTag.Replace(
            CommentBlock.Replace(ScriptOrStyle.Replace(fragment, " "), " "), " ");

        var decoded = WebUtility.HtmlDecode(stripped)
            .Replace('\u00A0', ' ')     // non-breaking space
            .Replace('\u2212', '-')     // minus sign
            .Replace('\u2013', '-');    // en dash, which price ranges and refunds both use

        return Runs.Replace(decoded, " ").Trim();
    }

    /// <summary>
    /// The first number in a run of text, as a decimal. A quantity, never an
    /// amount: money goes through <see cref="MoneyText"/> and a declared
    /// <see cref="Connector.Kit.Normalization.MoneyUnit"/> instead, because a
    /// misread quantity fails reconciliation loudly while a misread amount
    /// corrupts a total silently.
    /// </summary>
    public static decimal? Number(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var match = NumberToken.Match(text);
        if (!match.Success) return null;

        var cleaned = match.Value.Replace(',', '.');

        // "1.234" is a thousand, not 1.234 - but only when the groups are
        // three digits long, which is what tells the two apart.
        var dots = cleaned.Count(c => c == '.');
        if (dots > 1) cleaned = cleaned.Replace(".", string.Empty, StringComparison.Ordinal);

        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// The money-looking part of a rendered price, with its sign resolved,
    /// ready for <see cref="Connector.Kit.Normalization.MoneyParser"/> under a
    /// unit the caller has already declared. Returns null when the text holds
    /// no number at all, which the caller reports rather than guesses around.
    ///
    /// Which number, when there is more than one, is the interesting part. A
    /// discount reads "2e halve prijs - &#8364; 3,00", and taking the first
    /// number in that string charges the customer two euros for a promotion
    /// worth three. So the choice is, in order: the number a currency symbol
    /// introduces, then the first one with a cents part, then the first one at
    /// all. Each rule is a fact about how prices are typeset, not a guess
    /// about magnitude - the UNIT is still declared by the caller and never
    /// inferred here.
    ///
    /// Both Dutch sign conventions are honoured: a leading minus that may be
    /// separated from the digits by a currency symbol ("- &#8364; 4,00"), and
    /// the receipt-style trailing minus, which must be touching ("4,00-") so
    /// that a range - "&#8364; 10,00 - &#8364; 12,00" - does not read as a
    /// negative ten.
    /// </summary>
    public static string? MoneyText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var normalized = text.Replace('\u2212', '-').Replace('\u2013', '-');
        var matches = NumberToken.Matches(normalized);
        if (matches.Count == 0) return null;

        var chosen = matches.FirstOrDefault(m => AfterCurrency(normalized, m.Index))
                     ?? matches.FirstOrDefault(m => HasCents(m.Value))
                     ?? matches[0];

        var value = chosen.Value;
        if (value.StartsWith('-')) return value;

        return Negative(normalized, chosen.Index, chosen.Index + chosen.Length) ? "-" + value : value;
    }

    /// <summary>
    /// The text with its price taken out - "2e halve prijs", "kortingscode
    /// LENTE" - or null when the element was nothing but a price. A label of
    /// "- 3,00" is worse than no label at all.
    /// </summary>
    public static string? WithoutPrice(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var normalized = text.Replace('\u2212', '-').Replace('\u2013', '-');

        // Everything from the currency symbol on is the price and whatever
        // trails it; before it is the human's description of what happened.
        var symbol = normalized.IndexOf('\u20ac');
        var head = symbol >= 0 ? normalized[..symbol] : TrailingPrice.Replace(normalized, string.Empty);

        var trimmed = head.Trim().Trim('-', ':', '\u2013').Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>Only whitespace and a currency symbol between a '-' and the digits.</summary>
    private static bool Negative(string text, int start, int end)
    {
        var i = start - 1;
        while (i >= 0 && (char.IsWhiteSpace(text[i]) || text[i] == '\u20ac')) i--;
        if (i >= 0 && text[i] == '-') return true;

        // Touching, and only touching: a spaced '-' after a price is a range.
        return end < text.Length && text[end] == '-';
    }

    private static bool AfterCurrency(string text, int start)
    {
        var i = start - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
        return i >= 0 && text[i] == '\u20ac';
    }

    /// <summary>
    /// A separator followed by one or two digits, which is a cents part -
    /// where three digits after a dot is a thousands group, and "1.249" must
    /// not be mistaken for one euro twenty-five.
    /// </summary>
    private static bool HasCents(string value)
    {
        var separator = value.LastIndexOfAny([',', '.']);
        if (separator < 0) return false;

        var tail = value.Length - separator - 1;
        return tail is 1 or 2;
    }

    // ---- scanning -----------------------------------------------------------

    /// <summary>The index of the '>' that closes an opening tag, ignoring quoted values.</summary>
    private static int TagEnd(string html, int open)
    {
        var quote = '\0';

        for (var i = open + 1; i < html.Length; i++)
        {
            var c = html[i];

            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }

            if (c is '"' or '\'') quote = c;
            else if (c == '>') return i;
        }

        return -1;
    }

    private static string TagName(string openTag)
    {
        var length = 0;
        while (1 + length < openTag.Length && char.IsAsciiLetterOrDigit(openTag[1 + length])) length++;
        return openTag.Substring(1, length);
    }

    /// <summary>
    /// The element's content and the index just past its closing tag.
    /// An element that is never closed keeps the rest of the document, which
    /// is what a browser does and is better than losing every order after a
    /// single stray tag.
    /// </summary>
    private static (string Inner, int After) Content(string html, string name, string openTag, int end)
    {
        if (openTag.EndsWith("/>", StringComparison.Ordinal) || VoidElements.Contains(name))
        {
            return (string.Empty, end + 1);
        }

        var depth = 1;
        var cursor = end + 1;

        while (cursor < html.Length)
        {
            var next = html.IndexOf('<', cursor);
            if (next < 0) break;

            if (html.AsSpan(next).StartsWith("<!--", StringComparison.Ordinal))
            {
                var closed = html.IndexOf("-->", next, StringComparison.Ordinal);
                cursor = closed < 0 ? html.Length : closed + 3;
                continue;
            }

            var closing = next + 1 < html.Length && html[next + 1] == '/';
            var nameAt = closing ? next + 2 : next + 1;

            if (!Named(html, nameAt, name))
            {
                cursor = next + 1;
                continue;
            }

            var tagEnd = TagEnd(html, next);
            if (tagEnd < 0) break;

            if (closing)
            {
                if (--depth == 0) return (html[(end + 1)..next], tagEnd + 1);
            }
            else if (!html[next..(tagEnd + 1)].EndsWith("/>", StringComparison.Ordinal))
            {
                depth++;
            }

            cursor = tagEnd + 1;
        }

        return (html[(end + 1)..], html.Length);
    }

    /// <summary>Whether the tag starting at <paramref name="at"/> is exactly this element.</summary>
    private static bool Named(string html, int at, string name)
    {
        if (at + name.Length > html.Length) return false;
        if (!html.AsSpan(at, name.Length).Equals(name, StringComparison.OrdinalIgnoreCase)) return false;

        var following = at + name.Length;
        return following >= html.Length || !char.IsAsciiLetterOrDigit(html[following]);
    }

    // ---- selectors ----------------------------------------------------------

    private readonly record struct Simple(string? Tag, string? Class, string? Attribute, string? Value, bool Contains);

    private static Simple Parse(string selector)
    {
        var trimmed = selector.Trim();
        var match = SelectorPattern.Match(trimmed);

        if (!match.Success || trimmed.Length == 0)
        {
            // An operator's typo, not a provider's redesign: saying so with
            // the wrong code would degrade bol.com for every other user.
            throw ConnectorException.InvalidRequest(
                $"bol: '{selector}' is not a supported selector; use tag, .class, [attr], [attr=value] or [attr*=value]");
        }

        var tag = match.Groups["tag"];
        var css = match.Groups["class"];
        var attribute = match.Groups["attr"];

        if (!tag.Success && !css.Success && !attribute.Success)
        {
            throw ConnectorException.InvalidRequest($"bol: selector '{selector}' selects nothing");
        }

        var value = match.Groups["val"].Success ? match.Groups["val"].Value.Trim().Trim('"', '\'') : null;

        return new Simple(
            tag.Success ? tag.Value : null,
            css.Success ? css.Value : null,
            attribute.Success ? attribute.Value : null,
            value,
            match.Groups["op"].Value == "*=");
    }

    private static bool Matches(Simple simple, string tag, string openTag)
    {
        if (simple.Tag is { } wanted && !string.Equals(tag, wanted, StringComparison.OrdinalIgnoreCase)) return false;

        if (simple.Class is { } css)
        {
            var classes = Attribute(openTag, "class");
            if (classes is null) return false;

            var present = classes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Any(token => string.Equals(token, css, StringComparison.Ordinal));

            if (!present) return false;
        }

        if (simple.Attribute is not { } attribute) return true;

        var actual = Attribute(openTag, attribute);
        if (actual is null) return false;
        if (simple.Value is not { } expected) return true;

        return simple.Contains
            ? actual.Contains(expected, StringComparison.OrdinalIgnoreCase)
            : string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }
}
