using System.Net;
using System.Text;
using Connector.Kit.Errors;
using Connector.Kit.Hosting.Endpoints;
using Connector.Kit.Jobs;
using ShopConnector.Adapters.Mock;
using ShopConnector.Api.Tests.Infrastructure;

namespace ShopConnector.Api.Tests;

/// <summary>
/// api-spec §2.2 and §8 steps 2-3: a login that stops to ask a human, and
/// everything that follows from it.
///
/// This is the case the platform exists for. A connector that can only serve
/// providers which never ask a question cannot serve the providers people
/// actually use.
/// </summary>
[Collection(ShopApiCollection.Name)]
public sealed class InteractiveLoginTests(ShopApiFactory factory)
{
    private const string Provider = MockStoreAdapters.Captcha;
    private const string CorrectAnswer = "MOCK1";

    /// <summary>PNG's magic number. Bytes that survive the relay are bytes a consumer can render.</summary>
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The closed progress vocabulary of api-spec §2.2. Typed, never prose.</summary>
    private static readonly HashSet<string> LegalSteps =
        [.. Enum.GetNames<JobStep>().Select(SnakeCase)];

    private static readonly Dictionary<string, string> Credentials = new(StringComparer.Ordinal)
    {
        ["username"] = "shopper",
        ["password"] = "hunter2",
    };

    [Fact]
    public async Task A_captcha_login_relays_the_challenge_answers_it_and_ends_with_a_bundle()
    {
        using var http = factory.CreateAuthorizedClient();
        var subject = Flows.NewSubject("captcha");

        // 1. The run stopped to ask something, and says so with a 202 rather
        //    than holding a socket open for as long as a human takes.
        using var request = Wire.Post($"/v1/{Provider}/login", new { Subject = subject, Inputs = Credentials });
        using var login = await http.SendAsync(request);
        var accepted = await login.JsonAsync();

        Assert.Equal(HttpStatusCode.Accepted, login.StatusCode);
        Assert.Equal("awaiting_input", accepted.Text("state"));

        // Every response that touched a provider says which contract answered.
        Assert.NotEmpty(Assert.Single(login.RawHeader(RequestContext.ManifestVersionHeader)));

        var sessionId = accepted.Text("session_id");
        var challenge = accepted.GetProperty("challenge");
        var challengeId = challenge.Text("id");

        Assert.StartsWith("ses_", sessionId, StringComparison.Ordinal);
        Assert.StartsWith("chl_", challengeId, StringComparison.Ordinal);
        Assert.Equal("image", challenge.Text("type"));
        Assert.Equal("connect.challenge.captcha", challenge.Text("prompt_key"));

        // Mandatory: a challenge holds a live run hostage, so a consumer has to
        // know when the question stops being answerable.
        Assert.True(challenge.GetProperty("expires_at").TryGetDateTimeOffset(out var expiresAt));
        Assert.True(expiresAt > DateTimeOffset.UtcNow, "a challenge is raised with a future deadline");

        // Progress is a member of a closed enum, so it can be translated and
        // rendered instead of string-matched.
        Assert.Contains(accepted.GetProperty("progress").Text("step"), LegalSteps);

        // 2. The image is a separate authenticated fetch; it never rides the JSON.
        var imageUrl = challenge.Text("image_url");
        Assert.Equal($"/v1/{Provider}/login/{sessionId}/challenges/{challengeId}/image", imageUrl);

        using (var image = await http.GetAsync(imageUrl))
        {
            Assert.Equal(HttpStatusCode.OK, image.StatusCode);
            Assert.Equal("image/png", image.ContentType()?.MediaType);

            var bytes = await image.Content.ReadAsByteArrayAsync();
            Assert.Equal(PngSignature, bytes.Take(PngSignature.Length).ToArray());
        }

        // 3. An answer naming a challenge this run never raised is refused
        //    outright, rather than being handed to a waiting adapter.
        using (var stray = await AnswerAsync(http, sessionId, "chl_0123456789abcdef0123456789abcdef", CorrectAnswer))
        {
            await ErrorEnvelope.AssertAsync(stray, HttpStatusCode.BadRequest, "unsupported_resource");
        }

        // 4. The human answers, and the run continues from where it stopped.
        using (var answer = await AnswerAsync(http, sessionId, challengeId, CorrectAnswer))
        {
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            Assert.Equal(sessionId, (await answer.JsonAsync()).Text("session_id"));
        }

        var bundle = await Flows.AwaitBundleAsync(http, Provider, sessionId);
        Assert.StartsWith("sb_v1.", bundle, StringComparison.Ordinal);

        var settled = await Flows.AwaitStateAsync(http, Provider, sessionId, "active");
        Assert.Equal("Mock Store (captcha)", settled.GetProperty("provider_account").Text("display_name"));

        // The bundle is handed over exactly once: a copy kept here would be a
        // credential at rest with no reason to exist.
        Assert.Null(settled.TextOrNull("bundle"));

        // 5. A second answer for the same challenge conflicts rather than
        //    confusing a run that already acted on the first.
        using (var replay = await AnswerAsync(http, sessionId, challengeId, CorrectAnswer))
        {
            await ErrorEnvelope.AssertAsync(replay, HttpStatusCode.Conflict);
        }

        // 6. The picture served its purpose the instant it was answered.
        using (var image = await http.GetAsync(imageUrl))
        {
            await ErrorEnvelope.AssertAsync(image, HttpStatusCode.Gone, "challenge_expired");
        }
    }

    [Fact]
    public async Task A_wrong_answer_fails_the_login_and_is_never_retried()
    {
        using var http = factory.CreateAuthorizedClient();
        var subject = Flows.NewSubject("captcha-wrong");

        using var request = Wire.Post($"/v1/{Provider}/login", new { Subject = subject, Inputs = Credentials });
        using var login = await http.SendAsync(request);
        var accepted = await login.JsonAsync();

        Assert.Equal(HttpStatusCode.Accepted, login.StatusCode);
        var sessionId = accepted.Text("session_id");

        using (var answer = await AnswerAsync(http, sessionId, accepted.GetProperty("challenge").Text("id"), "not-it"))
        {
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        }

        await Flows.AwaitStateAsync(http, Provider, sessionId, "failed");

        // No wire surface says this, and it is the rule that matters most: a
        // credential that has already gone upstream is never submitted again.
        var job = Db.LatestJob(factory, sessionId);
        Assert.Equal(ErrorCode.MfaFailed, job.ErrorCode);
        Assert.Equal(1, job.Attempts);
        Assert.True(job.CredentialSubmitted, "the adapter latched the credential before the provider saw it");
        Assert.Null(job.InputsJson);
    }

    private static async Task<HttpResponseMessage> AnswerAsync(
        HttpClient http, string sessionId, string challengeId, string value)
    {
        using var request = Wire.Post($"/v1/{Provider}/login/{sessionId}/answer",
            new { ChallengeId = challengeId, Value = value });

        return await http.SendAsync(request);
    }

    private static string SnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) builder.Append('_');
            builder.Append(char.ToLowerInvariant(name[i]));
        }

        return builder.ToString();
    }
}
