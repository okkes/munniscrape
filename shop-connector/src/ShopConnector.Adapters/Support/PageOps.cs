using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using Microsoft.Playwright;

namespace ShopConnector.Adapters.Support;

/// <summary>
/// Selector work, written to survive a redesign.
///
/// Not one of the login pages this service drives has a published contract,
/// so every selector is a guess with a shelf life. Each operation therefore
/// takes a candidate list - ordered most-specific first, operator-editable
/// through options - and a miss on all of them is
/// <see cref="ErrorCode.ProviderChanged"/> with the list in the detail, so
/// the next breakage arrives already diagnosed.
/// </summary>
internal static class PageOps
{
    public static async Task<IElementHandle> RequireAsync(
        IPage page, IReadOnlyList<string> selectors, string providerId, string what, int timeoutMs, CancellationToken ct)
    {
        var handle = await FindAsync(page, selectors, timeoutMs, ct).ConfigureAwait(false);
        return handle ?? throw ConnectorException.ProviderChanged(
            $"{providerId}: no element for {what}; tried [{string.Join(", ", selectors)}]");
    }

    /// <summary>
    /// "This selector did not match", whatever Playwright chose to call it.
    ///
    /// Playwright for .NET raises <see cref="System.TimeoutException"/> - NOT
    /// a <see cref="PlaywrightException"/> - when a wait expires. Catching only
    /// the latter is a silent trap: every helper here is written to return null
    /// on a miss, and a bare <c>catch (PlaywrightException)</c> lets the timeout
    /// escape instead, so a page that simply had no cookie banner takes the
    /// whole login down. That is exactly what happened on the first live
    /// Albert Heijn attempt. One predicate, used everywhere, so the next
    /// candidate list cannot get it wrong again.
    /// </summary>
    internal static bool IsSelectorMiss(Exception ex) =>
        ex is PlaywrightException or System.TimeoutException;

    /// <summary>
    /// First matching selector, or null. The per-selector budget is the
    /// whole budget divided by the candidate count: trying five selectors
    /// at ten seconds each turns a redesigned page into a fifty-second hang
    /// that outlives the job's own timeout.
    /// </summary>
    public static async Task<IElementHandle?> FindAsync(
        IPage page, IReadOnlyList<string> selectors, int timeoutMs, CancellationToken ct)
    {
        if (selectors.Count == 0) return null;

        var perSelector = Math.Max(500, timeoutMs / selectors.Count);
        foreach (var selector in selectors)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var handle = await page.WaitForSelectorAsync(selector, new PageWaitForSelectorOptions
                {
                    Timeout = perSelector,
                    State = WaitForSelectorState.Visible,
                }).ConfigureAwait(false);

                if (handle is not null) return handle;
            }
            catch (Exception ex) when (IsSelectorMiss(ex))
            {
                // A selector that never appeared is the normal case here -
                // that is what a candidate list is for.
            }
        }

        return null;
    }

    public static async Task FillAsync(
        IPage page, IReadOnlyList<string> selectors, string value, string providerId, string what,
        int timeoutMs, CancellationToken ct)
    {
        var handle = await RequireAsync(page, selectors, providerId, what, timeoutMs, ct).ConfigureAwait(false);
        await handle.FillAsync(value).ConfigureAwait(false);
    }

    public static async Task ClickAsync(
        IPage page, IReadOnlyList<string> selectors, string providerId, string what, int timeoutMs, CancellationToken ct)
    {
        var handle = await RequireAsync(page, selectors, providerId, what, timeoutMs, ct).ConfigureAwait(false);
        await handle.ClickAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The element's box, for the challenge redactor: everything outside
    /// the crop is obscured before an image leaves the agent.
    /// </summary>
    public static async Task<CropRegion?> CropAsync(IElementHandle handle)
    {
        var box = await handle.BoundingBoxAsync().ConfigureAwait(false);
        if (box is null) return null;

        return new CropRegion((int)box.X, (int)box.Y, (int)box.Width, (int)box.Height);
    }
}
