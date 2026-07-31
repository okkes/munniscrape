using System.Reflection;
using System.Text;
using Connector.Kit.Hosting.Endpoints;
using Connector.Kit.Hosting.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace ShopConnector.Api.Tests;

/// <summary>
/// The funnel's result type, on its own.
///
/// It was introduced to carry ONE thing that <c>Results.Json</c> could not: the
/// response type, into endpoint metadata, so the reference stops documenting
/// every route as a bodyless 200. Everything else about it must be the same
/// bytes on the same status - a change to how the platform answers, made in
/// pursuit of a documentation fix, would be a poor trade.
///
/// So there are two halves here: the wire is unchanged, and the claim it makes
/// about itself is exactly one and no wider.
/// </summary>
public sealed class ConnectorJsonResultTests
{
    [Theory]
    [InlineData(StatusCodes.Status200OK)]
    [InlineData(StatusCodes.Status202Accepted)]
    public async Task The_funnel_writes_the_wire_encoding_on_the_status_it_was_given(int status)
    {
        var value = new AckResponse { Purged = 7 };

        var written = await ExecuteAsync(ConnectorResults.Json(value, status));

        // Byte for byte what ConnectorJson.Serialize produces - which is what
        // Results.Json wrote before this type existed, since both go through
        // the same options.
        Assert.Equal(ConnectorJson.Serialize(value), written.Body);
        Assert.Equal("{\"purged\":7}", written.Body);
        Assert.Equal(status, written.Status);
        Assert.StartsWith("application/json", written.ContentType, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_default_overload_is_a_200()
    {
        var written = await ExecuteAsync(ConnectorResults.Json(new AckResponse { Purged = 0 }));

        Assert.Equal(StatusCodes.Status200OK, written.Status);
        Assert.Equal("{\"purged\":0}", written.Body);
    }

    /// <summary>
    /// The value and status stay readable, because a result type that swallows
    /// them cannot be asserted on by anything but a live request.
    /// </summary>
    [Fact]
    public void The_result_still_says_what_it_is_carrying()
    {
        var value = new AckResponse { Purged = 3 };
        var result = ConnectorResults.Json(value);

        Assert.Same(value, result.Value);
        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
    }

    /// <summary>
    /// The whole reason the type exists, and the reason
    /// <see cref="ConnectorResults.Json{T}(T, int)"/> deliberately does NOT
    /// return it: the claim is static, so it can only ever be a 200. If this
    /// ever grew a second entry, or claimed a status it cannot know, every
    /// route in the reference would inherit the mistake at once.
    /// </summary>
    [Fact]
    public void The_metadata_claim_is_exactly_one_json_200_for_the_carried_type()
    {
        var metadata = MetadataOf<ConnectorJsonResult<AckResponse>>();

        var produces = Assert.IsType<ProducesResponseTypeMetadata>(Assert.Single(metadata));

        Assert.Equal(StatusCodes.Status200OK, produces.StatusCode);
        Assert.Equal(typeof(AckResponse), produces.Type);
        Assert.Equal("application/json", Assert.Single(produces.ContentTypes));
    }

    [Fact]
    public void The_claim_follows_the_carried_type_rather_than_being_fixed()
    {
        var metadata = MetadataOf<ConnectorJsonResult<CatalogResponse>>();

        Assert.Equal(
            typeof(CatalogResponse),
            Assert.IsType<ProducesResponseTypeMetadata>(Assert.Single(metadata)).Type);
    }

    private static IReadOnlyList<object?> MetadataOf<TResult>()
        where TResult : IEndpointMetadataProvider
    {
        var builder = new RouteEndpointBuilder(
            _ => Task.CompletedTask, RoutePatternFactory.Parse("/probe"), order: 0);

        // The MethodInfo is the handler being described. This type ignores it -
        // it describes itself - so any method stands in.
        TResult.PopulateMetadata(Probe, builder);

        return [.. builder.Metadata];
    }

    private static readonly MethodInfo Probe =
        typeof(ConnectorJsonResultTests).GetMethod(
            nameof(The_default_overload_is_a_200), BindingFlags.Public | BindingFlags.Instance)!;

    private static async Task<(int Status, string Body, string? ContentType)> ExecuteAsync(IResult result)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        await using var provider = services.BuildServiceProvider();

        using var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Response.Body = body;

        await result.ExecuteAsync(context);

        return (context.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()), context.Response.ContentType);
    }
}
