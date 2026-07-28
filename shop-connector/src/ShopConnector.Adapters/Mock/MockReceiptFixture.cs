using System.Text.Json;
using Connector.Kit.Errors;
using Connector.Kit.Normalization;
using ShopConnector.Adapters.Fixtures;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.Mock;

/// <summary>
/// Reads a mock provider's receipts from a fixture file.
///
/// The fixture states every amount in minor units, named <c>*_minor</c>, and
/// this reader declares <see cref="MoneyUnit.Minor"/> to match. That is the
/// same rule the real adapters follow, applied where it costs nothing -
/// a fixture whose unit had to be inferred would teach the wrong habit to
/// everyone who copied it.
/// </summary>
internal static class MockReceiptFixture
{
    public static IReadOnlyList<Receipt> Read(string fixtureName, string providerId, string sessionId, bool withItems)
    {
        using var document = JsonDocument.Parse(FixtureCatalog.Read(fixtureName));
        var root = document.RootElement;

        var currency = JsonAccess.StrOf(root, "currency") ?? "EUR";
        var merchantName = JsonAccess.StrOf(root, "merchant") ?? "Mock Store";

        var rows = JsonAccess.Array(root, "receipts");
        if (rows.Count == 0)
        {
            throw ConnectorException.ProviderChanged($"{providerId}: fixture '{fixtureName}' holds no receipts");
        }

        var receipts = new List<Receipt>(rows.Count);
        foreach (var row in rows)
        {
            var externalId = JsonAccess.StrOf(row, "external_id")
                             ?? throw ConnectorException.ProviderChanged(
                                 $"{providerId}: fixture row without an external_id");

            var payment = JsonAccess.TryProp(row, out var paymentNode, "payment")
                ? ReceiptFactory.Payment(
                    JsonAccess.StrOf(paymentNode, "method"),
                    JsonAccess.StrOf(paymentNode, "card_last4"),
                    JsonAccess.StrOf(paymentNode, "iban_tail"))
                : ReceiptFactory.Payment();

            receipts.Add(ReceiptFactory.Build(
                sessionId,
                externalId,
                new Merchant
                {
                    Id = providerId,
                    Name = merchantName,
                    StoreName = JsonAccess.StrOf(row, "store_name"),
                },
                // Fixtures carry a real offset, so the mocks prove the rule
                // the real adapters have to enforce by hand.
                ReceiptTime.Parse(JsonAccess.StrOf(row, "purchased_at"), RetailZones.Dutch, providerId, "purchased_at"),
                MoneyReader.Require(row, MoneyUnit.Minor, currency, "receipt.total", "total_minor"),
                payment,
                withItems ? Items(row, currency) : []));
        }

        return receipts;
    }

    private static IReadOnlyList<ReceiptItem> Items(JsonElement row, string currency)
    {
        var lines = JsonAccess.Array(row, "items");
        var items = new List<ReceiptItem>(lines.Count);

        foreach (var line in lines)
        {
            // A nameless line is a broken fixture, and a broken fixture must
            // fail the suite rather than quietly emit fewer items than the
            // file lists.
            var name = JsonAccess.StrOf(line, "name")
                       ?? throw ConnectorException.ProviderChanged("mock: fixture item without a name");

            items.Add(new ReceiptItem
            {
                Name = name,
                Quantity = JsonAccess.Quantity(line, "quantity"),
                UnitPrice = MoneyReader.Optional(line, MoneyUnit.Minor, currency, "item.unit_price", "unit_price_minor"),
                Total = MoneyReader.Require(line, MoneyUnit.Minor, currency, "item.total", "total_minor"),
                Discount = JsonAccess.TryProp(line, out var discount, "discount")
                    ? new ReceiptDiscount
                    {
                        Amount = MoneyReader.Require(discount, MoneyUnit.Minor, currency,
                            "item.discount", "amount_minor"),
                        Label = JsonAccess.StrOf(discount, "label"),
                    }
                    : null,
            });
        }

        return items;
    }
}
