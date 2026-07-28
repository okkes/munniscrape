using Connector.Kit.Hosting.Data;
using Microsoft.Extensions.DependencyInjection;

namespace ShopConnector.Api.Tests.Infrastructure;

/// <summary>
/// Reads the control plane's own tables.
///
/// Used only for invariants that have no wire surface by design - "the staged
/// rows are really gone", "that job was never leased a second time". Asserting
/// them through the API would mean adding an endpoint whose only caller is a
/// test, which is a worse trade than reaching into the database from one.
/// </summary>
internal static class Db
{
    public static T Read<T>(ShopApiFactory factory, Func<ConnectorDbContext, T> read)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(read);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
        return read(db);
    }

    public static int StagedRows(ShopApiFactory factory, string sessionId) =>
        Read(factory, db => db.Results.Count(r => r.SessionId == sessionId));

    /// <summary>The newest job for a subject's only session. For a failure that never returned a session id.</summary>
    public static JobRow JobForSubject(ShopApiFactory factory, string subject) =>
        Read(factory, db =>
        {
            var sessionId = db.Sessions.Where(s => s.Subject == subject).Select(s => s.Id).Single();
            return db.Jobs
                .Where(j => j.SessionId == sessionId)
                .OrderByDescending(j => j.CreatedAt)
                .ThenByDescending(j => j.Id)
                .First();
        });

    public static JobRow LatestJob(ShopApiFactory factory, string sessionId) =>
        Read(factory, db => db.Jobs
            .Where(j => j.SessionId == sessionId)
            .OrderByDescending(j => j.CreatedAt)
            .ThenByDescending(j => j.Id)
            .First());
}
