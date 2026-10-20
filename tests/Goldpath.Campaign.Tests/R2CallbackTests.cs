using System.Globalization;
using System.Net;
using System.Text;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goldpath.Campaign.Tests;

/// <summary>
/// The callback surface after the R2 review (2026-10-20): finding 1 — the route names the
/// campaign, and a callback that outruns its acceptance is parked, never lost; finding 7 /
/// RFC R2.8 — every callback is authenticated by its HMAC signature, and an unsigned one is
/// not possible.
/// </summary>
public class R2CallbackTests
{
    // ---- finding 1: scope and the early callback (R2.1 "wait for it, but not forever") ----

    /// <summary>
    /// Finding 1 / R2.1: the device answers before the sink has written the acceptance. The
    /// callback is parked (not 404), the acceptance settles it on arrival, the item succeeds
    /// on its first attempt, and nothing is executed twice.
    /// </summary>
    [Fact]
    public async Task ACallbackThatOutrunsItsAcceptance_IsParked_AndSettlesWhenTheAcceptanceLands()
    {
        using var fixture = R2Tests.ActionFixture(o => { o.RetryJitter = 0; o.RetryBackoff = [TimeSpan.Zero]; }, sourceSize: 1);
        fixture.Script[1] = GoldpathCampaignActionResult.Accepted("dev-1", TimeSpan.FromMilliseconds(1));
        var campaign = await fixture.CreateAsync(R2Tests.Generous(maxAttempts: 3));
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
        await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, fixture.Reload(campaign.Id), 1, CancellationToken.None);

        // The consumer: claim, the handler says Accepted — the outcome is published, not yet flushed.
        var item = await fixture.Engine.ClaimAsync(db, campaign.Id, 1, CancellationToken.None);
        var result = await fixture.Engine.ExecuteItemAsync(scope.ServiceProvider, "winback", campaign.Id, 1, item!.TargetJson, null, 1, false, CancellationToken.None);
        var acceptance = fixture.Engine.OutcomeFor(campaign.Id, 1, 1, result);

        // The device answers at once.
        var early = await fixture.Engine.ResolveCallbackAsync(db, "winback", campaign.Id, "dev-1", new GoldpathCampaignCallbackRequest(true), CancellationToken.None);
        Assert.Equal(GoldpathCampaignCallbackStatus.Parked, early.Status);

        // The sink flushes the acceptance — and the parked answer with it.
        await fixture.Engine.ApplyOutcomesAsync(db, campaign.Id, [acceptance], CancellationToken.None);
        var settled = fixture.Query(d => d.Set<GoldpathCampaignItem>().AsNoTracking().Single());
        Assert.Equal(GoldpathCampaignItemState.Succeeded, settled.State);
        Assert.Equal(0, settled.Attempts);
        Assert.Equal(1, fixture.Reload(campaign.Id).SucceededCount);
        Assert.Empty(fixture.Query(d => d.Set<GoldpathCampaignParkedCallback>().ToList()));

        // Nothing is left to time out, so nothing is ever executed a second time.
        await Task.Delay(20);
        Assert.Empty(await fixture.Engine.SweepAckTimeoutsAsync(db, fixture.Reload(campaign.Id), CancellationToken.None));
        Assert.Single(fixture.Executed);
    }

    /// <summary>
    /// Finding 1 / R2.1: the other interleaving — the acceptance lands between the callback's
    /// first look and its park. The park-then-look-again order sees it, and the callback
    /// resolves to the outcome instead of a park nobody would ever settle.
    /// </summary>
    [Fact]
    public async Task AnAcceptanceThatLandsWhileTheCallbackParks_IsSeenOnTheSecondLook()
    {
        using var fixture = R2Tests.ActionFixture(sourceSize: 1);
        fixture.Script[1] = GoldpathCampaignActionResult.Accepted("dev-2", TimeSpan.FromMinutes(5));
        var campaign = await fixture.CreateAsync(R2Tests.Generous(maxAttempts: 3));
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
        await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, fixture.Reload(campaign.Id), 1, CancellationToken.None);
        var item = await fixture.Engine.ClaimAsync(db, campaign.Id, 1, CancellationToken.None);
        var result = await fixture.Engine.ExecuteItemAsync(scope.ServiceProvider, "winback", campaign.Id, 1, item!.TargetJson, null, 1, false, CancellationToken.None);
        var acceptance = fixture.Engine.OutcomeFor(campaign.Id, 1, 1, result);

        // The sink's flush runs at the exact moment the callback writes its park.
        var racing = new SinkRacesThePark(() => fixture.Engine.ApplyOutcomesAsync(
            fixture.Services.CreateScope().ServiceProvider.GetRequiredService<CampaignTestContext>(), campaign.Id, [acceptance], CancellationToken.None));
        await using var callbackDb = new CampaignTestContext(new DbContextOptionsBuilder<CampaignTestContext>()
            .UseSqlite(fixture.Connection).AddInterceptors(racing).Options);
        var resolution = await fixture.Engine.ResolveCallbackAsync(callbackDb, "winback", campaign.Id, "dev-2", new GoldpathCampaignCallbackRequest(true), CancellationToken.None);

        Assert.True(racing.Fired);
        Assert.Equal(GoldpathCampaignCallbackStatus.Pending, resolution.Status);
        await fixture.Engine.ApplyOutcomesAsync(db, campaign.Id, [resolution.Outcome!], CancellationToken.None);
        Assert.Equal(GoldpathCampaignItemState.Succeeded, fixture.Query(d => d.Set<GoldpathCampaignItem>().AsNoTracking().Single()).State);
        Assert.Equal(1, fixture.Reload(campaign.Id).SucceededCount);
        Assert.Empty(fixture.Query(d => d.Set<GoldpathCampaignParkedCallback>().ToList()));
    }

    /// <summary>
    /// Finding 1 / R2.1: the route names the campaign, so one correlation id held by two
    /// campaigns of one type settles the campaign the callback names — never the other.
    /// </summary>
    [Fact]
    public async Task ACallbackSettlesOnlyTheCampaignItNames()
    {
        using var fixture = R2Tests.ActionFixture(sourceSize: 1);
        fixture.Script[1] = GoldpathCampaignActionResult.Accepted("device-7", TimeSpan.FromMinutes(5));
        var first = await AcceptAsync(fixture);
        var second = await AcceptAsync(fixture);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();

        // The first campaign's device answered long ago.
        var answered = await fixture.Engine.ResolveCallbackAsync(db, "winback", first, "device-7", new GoldpathCampaignCallbackRequest(true), CancellationToken.None);
        await fixture.Engine.ApplyOutcomesAsync(db, first, [answered.Outcome!], CancellationToken.None);

        // Its device repeats the webhook: the first campaign's item already settled — the second's must not be touched.
        var repeat = await fixture.Engine.ResolveCallbackAsync(db, "winback", first, "device-7", new GoldpathCampaignCallbackRequest(true), CancellationToken.None);
        Assert.Equal(GoldpathCampaignCallbackStatus.AlreadySettled, repeat.Status);

        var resolution = await fixture.Engine.ResolveCallbackAsync(db, "winback", second, "device-7", new GoldpathCampaignCallbackRequest(true), CancellationToken.None);
        Assert.Equal(GoldpathCampaignCallbackStatus.Pending, resolution.Status);
        Assert.Equal(second, resolution.Outcome!.CampaignId);

        // A route that names a campaign of ANOTHER type knows nothing.
        Assert.Equal(GoldpathCampaignCallbackStatus.Unknown,
            (await fixture.Engine.ResolveCallbackAsync(db, "other-type", second, "device-7", new GoldpathCampaignCallbackRequest(true), CancellationToken.None)).Status);
    }

    /// <summary>Finding 1: a parked answer nobody's acceptance claims expires with the claim it waited for.</summary>
    [Fact]
    public async Task AParkedCallbackNobodyClaims_Expires()
    {
        using var fixture = new CampaignFixture(o => o.StaleClaimAfter = TimeSpan.Zero, sourceSize: 1);
        fixture.Mutate(db => db.Set<GoldpathCampaignParkedCallback>().Add(new GoldpathCampaignParkedCallback
        {
            CampaignId = Guid.NewGuid(),
            CorrelationId = "orphan",
            Succeeded = true,
            ReceivedAt = DateTimeOffset.UtcNow.AddSeconds(-1),
        }));
        using var scope = fixture.Services.CreateScope();
        Assert.Equal(1, await fixture.Engine.ExpireParkedCallbacksAsync(scope.ServiceProvider.GetRequiredService<CampaignTestContext>(), CancellationToken.None));
    }

    // ---- finding 7 / R2.8: signed callbacks ----

    /// <summary>R2.8: a signed callback settles the item (202), and a signed repeat is an idempotent 200.</summary>
    [Fact]
    public async Task ASignedCallbackSettles_AndASignedRepeatIsIdempotent()
    {
        using var fixture = R2Tests.ActionFixture(sourceSize: 1);
        fixture.Script[1] = GoldpathCampaignActionResult.Accepted("fota-1", TimeSpan.FromMinutes(5));
        var campaign = await AcceptAsync(fixture);
        await using var host = await CallbackHost.StartAsync(fixture);

        var first = await host.PostAsync($"/goldpath/campaign/callbacks/winback/{campaign:N}/fota-1", """{"succeeded":true}""");
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        await fixture.Engine.ApplyOutcomesAsync(fixture.Services.CreateScope().ServiceProvider.GetRequiredService<CampaignTestContext>(),
            campaign, [Assert.Single(fixture.Publisher.Published.OfType<GoldpathCampaignOutcomeMessage>())], CancellationToken.None);
        Assert.Equal(1, fixture.Reload(campaign).SucceededCount);

        var repeat = await host.PostAsync($"/goldpath/campaign/callbacks/winback/{campaign:N}/fota-1", """{"succeeded":true}""");
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Single(fixture.Publisher.Published.OfType<GoldpathCampaignOutcomeMessage>());
    }

    /// <summary>
    /// R2.8: no signature, a signature by another secret, a body changed after signing, a
    /// path changed after signing, and a timestamp outside the window are each 401 — and none
    /// of them reaches the engine.
    /// </summary>
    [Theory]
    [InlineData("unsigned")]
    [InlineData("wrong-secret")]
    [InlineData("tampered-body")]
    [InlineData("tampered-path")]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("garbage-signature")]
    public async Task AnUnauthenticCallbackIs401_AndChangesNothing(string attack)
    {
        using var fixture = R2Tests.ActionFixture(sourceSize: 1);
        fixture.Script[1] = GoldpathCampaignActionResult.Accepted("fota-1", TimeSpan.FromMinutes(5));
        var campaign = await AcceptAsync(fixture);
        await using var host = await CallbackHost.StartAsync(fixture);
        var path = $"/goldpath/campaign/callbacks/winback/{campaign:N}/fota-1";
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var response = attack switch
        {
            "unsigned" => await host.PostRawAsync(path, """{"succeeded":true}""", timestamp: null, signature: null),
            "wrong-secret" => await host.PostAsync(path, """{"succeeded":true}""", secret: "another-secret-that-is-at-least-32-bytes-long"),
            "tampered-body" => await host.PostRawAsync(path, """{"succeeded":true}""", now,
                GoldpathCampaignCallbackSignature.Sign(CampaignFixture.CallbackSecret, "POST", path, now, Encoding.UTF8.GetBytes("""{"succeeded":false}"""))),
            "tampered-path" => await host.PostRawAsync(path, """{"succeeded":true}""", now,
                GoldpathCampaignCallbackSignature.Sign(CampaignFixture.CallbackSecret, "POST", path.Replace("fota-1", "fota-2", StringComparison.Ordinal), now, Encoding.UTF8.GetBytes("""{"succeeded":true}"""))),
            "stale" => await host.PostAsync(path, """{"succeeded":true}""", timestamp: now - 301),
            "future" => await host.PostAsync(path, """{"succeeded":true}""", timestamp: now + 301),
            _ => await host.PostRawAsync(path, """{"succeeded":true}""", now, "v1=not-hex"),
        };

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(fixture.Publisher.Published.OfType<GoldpathCampaignOutcomeMessage>());
        Assert.Equal(GoldpathCampaignItemState.AwaitingAck, fixture.Query(d => d.Set<GoldpathCampaignItem>().AsNoTracking().Single()).State);
    }

    /// <summary>R2.8 rotation: while the outgoing secret is passed as the previous one, BOTH verify; after it is dropped, only the new one.</summary>
    [Fact]
    public async Task DuringARotationBothSecretsVerify()
    {
        const string next = "the-next-callback-secret-rotated-in-for-the-test";
        using var fixture = R2Tests.ActionFixture(o => o.AddCampaign<TestTarget>("rotating", c => c
            .MaxTargets(10).SignedCallbacks(next, previousSecret: CampaignFixture.CallbackSecret).Targets((_, _) => AsyncEnumerable.Empty<TestTarget>())));
        await using var host = await CallbackHost.StartAsync(fixture);
        var campaign = Guid.NewGuid();   // unknown campaign: a VERIFIED callback answers 404, an unverified one 401

        Assert.Equal(HttpStatusCode.NotFound, (await host.PostAsync($"/goldpath/campaign/callbacks/rotating/{campaign:N}/x", "{\"succeeded\":true}", secret: next)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.PostAsync($"/goldpath/campaign/callbacks/rotating/{campaign:N}/x", "{\"succeeded\":true}", secret: CampaignFixture.CallbackSecret)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostAsync($"/goldpath/campaign/callbacks/rotating/{campaign:N}/x", "{\"succeeded\":true}", secret: "a-third-secret-nobody-configured-32bytes")).StatusCode);
    }

    /// <summary>Finding 1 over HTTP: an early callback is 202 (parked), never 404.</summary>
    [Fact]
    public async Task AnEarlyCallbackOverHttpIs202Parked()
    {
        using var fixture = R2Tests.ActionFixture(sourceSize: 1);
        var campaign = await fixture.CreateAsync(R2Tests.Generous());
        await fixture.EnumerateAllAsync(campaign);
        using (var scope = fixture.Services.CreateScope())
        {
            await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, fixture.Reload(campaign.Id), 1, CancellationToken.None);
            Assert.NotNull(await fixture.Engine.ClaimAsync(scope.ServiceProvider.GetRequiredService<CampaignTestContext>(), campaign.Id, 1, CancellationToken.None));
        }

        await using var host = await CallbackHost.StartAsync(fixture);
        var response = await host.PostAsync($"/goldpath/campaign/callbacks/winback/{campaign.Id:N}/dev-9", """{"succeeded":false,"errorCode":"DEVICE_BUSY","retryable":true}""");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var parked = Assert.Single(fixture.Query(d => d.Set<GoldpathCampaignParkedCallback>().AsNoTracking().ToList()));
        Assert.Equal("DEVICE_BUSY", parked.ErrorCode);
        Assert.False(parked.Succeeded);
    }

    /// <summary>R2.8: a type that declares no secret has no callback surface — a signed request for it is still 404.</summary>
    [Fact]
    public async Task ATypeWithoutASecretHasNoSurface()
    {
        using var fixture = R2Tests.ActionFixture(o => o.AddCampaign<TestTarget>("unsigned", c => c
            .MaxTargets(10).Targets((_, _) => AsyncEnumerable.Empty<TestTarget>())));
        await using var host = await CallbackHost.StartAsync(fixture);
        var response = await host.PostAsync($"/goldpath/campaign/callbacks/unsigned/{Guid.NewGuid():N}/x", "{\"succeeded\":true}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>R2.8 / ADR-0005: mapping the surface when no type signs refuses to start, with the rule's words.</summary>
    [Fact]
    public void MappingCallbacksWithNoSigningType_RefusesToStart()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddDbContext<CampaignTestContext>(o => o.UseSqlite("DataSource=:memory:"));
        builder.Services.AddSingleton(new GoldpathCampaignOptions().AddCampaign<TestTarget>("unsigned", c => c
            .MaxTargets(10).Targets((_, _) => AsyncEnumerable.Empty<TestTarget>())));
        using var app = builder.Build();
        var error = Assert.Throws<InvalidOperationException>(() => app.MapGoldpathCampaignCallbacks<CampaignTestContext>());
        Assert.Contains("SignedCallbacks", error.Message, StringComparison.Ordinal);
        Assert.Contains("GP1704", error.Message, StringComparison.Ordinal);
    }

    /// <summary>R2.8: a secret shorter than an HMAC-SHA256 key, or a "rotation" to the same secret, is refused at registration.</summary>
    [Fact]
    public void AShortSecretOrASameSecretRotationIsRefused()
    {
        var options = new GoldpathCampaignOptions();
        Assert.Throws<ArgumentException>(() => options.AddCampaign<TestTarget>("a", c => c.MaxTargets(1).SignedCallbacks("short").Targets((_, _) => AsyncEnumerable.Empty<TestTarget>())));
        Assert.Throws<ArgumentException>(() => options.AddCampaign<TestTarget>("b", c => c.MaxTargets(1)
            .SignedCallbacks(CampaignFixture.CallbackSecret, previousSecret: CampaignFixture.CallbackSecret).Targets((_, _) => AsyncEnumerable.Empty<TestTarget>())));
    }

    /// <summary>
    /// R2.8: by default the signature alone authenticates (the app's fallback policy must not
    /// demand a user); the ops policy applies only when the adopter passes it explicitly.
    /// </summary>
    [Fact]
    public void TheSignatureAuthenticates_AndAPolicyIsOnlyAnExplicitOptIn()
    {
        static RouteEndpoint Map(string? policy)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Services.AddDbContext<CampaignTestContext>(o => o.UseSqlite("DataSource=:memory:"));
            builder.Services.AddSingleton(new GoldpathCampaignOptions().AddCampaign<TestTarget>("device", c => c
                .MaxTargets(10).SignedCallbacks(CampaignFixture.CallbackSecret).Targets((_, _) => AsyncEnumerable.Empty<TestTarget>())));
            var app = builder.Build();
            app.MapGoldpathCampaignCallbacks<CampaignTestContext>(policy: policy);
            return ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().Single();
        }

        var byDefault = Map(null);
        Assert.NotNull(byDefault.Metadata.GetMetadata<IAllowAnonymous>());
        Assert.Empty(byDefault.Metadata.GetOrderedMetadata<IAuthorizeData>());

        var optedIn = Map(GoldpathPolicies.Ops);
        Assert.Null(optedIn.Metadata.GetMetadata<IAllowAnonymous>());
        Assert.Contains(optedIn.Metadata.GetOrderedMetadata<IAuthorizeData>(), a => a.Policy == GoldpathPolicies.Ops);
    }

    /// <summary>R2.8: the signature is a published recipe — pinned by a known vector so a second implementation can check itself.</summary>
    [Fact]
    public void TheSignatureRecipeIsPinned()
    {
        var signature = GoldpathCampaignCallbackSignature.Sign(
            "0123456789abcdef0123456789abcdef", "post", "/goldpath/campaign/callbacks/device/00000000000000000000000000000001/c-1",
            1_760_000_000, Encoding.UTF8.GetBytes("""{"succeeded":true}"""));
        using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef"));
        var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(
            "1760000000\nPOST\n/goldpath/campaign/callbacks/device/00000000000000000000000000000001/c-1\n{\"succeeded\":true}"))).ToLowerInvariant();
        Assert.Equal("v1=" + expected, signature);
    }

    // ---- what each answer says (the target system reads these; R2.1 / R2.8) ----

    /// <summary>R2.1: every answer carries ok/false and words that say what happened — the target system's operator reads them.</summary>
    [Fact]
    public async Task EveryAnswerSaysWhatHappened()
    {
        using var fixture = R2Tests.ActionFixture(sourceSize: 1);
        fixture.Script[1] = GoldpathCampaignActionResult.Accepted("fota-1", TimeSpan.FromMinutes(5));
        var campaign = await AcceptAsync(fixture);
        await using var host = await CallbackHost.StartAsync(fixture);
        var path = $"/goldpath/campaign/callbacks/winback/{campaign:N}/fota-1";

        var published = await Body(await host.PostAsync(path, """{"succeeded":false,"errorCode":"BUSY"}"""));
        Assert.True(published.Ok);
        Assert.Contains("published (failed)", published.Message, StringComparison.Ordinal);
        await fixture.Engine.ApplyOutcomesAsync(fixture.Services.CreateScope().ServiceProvider.GetRequiredService<CampaignTestContext>(),
            campaign, [Assert.Single(fixture.Publisher.Published.OfType<GoldpathCampaignOutcomeMessage>())], CancellationToken.None);

        var repeat = await Body(await host.PostAsync(path, """{"succeeded":false,"errorCode":"BUSY"}"""));
        Assert.True(repeat.Ok);
        Assert.Contains("already settled", repeat.Message, StringComparison.Ordinal);

        var unknown = await Body(await host.PostAsync($"/goldpath/campaign/callbacks/winback/{campaign:N}/nobody", """{"succeeded":true}"""));
        Assert.False(unknown.Ok);
        Assert.Contains("'nobody'", unknown.Message, StringComparison.Ordinal);

        var noSurface = await Body(await host.PostAsync($"/goldpath/campaign/callbacks/nosuch/{campaign:N}/fota-1", """{"succeeded":true}"""));
        Assert.False(noSurface.Ok);
        Assert.Contains("'nosuch'", noSurface.Message, StringComparison.Ordinal);

        var refused = await Body(await host.PostRawAsync(path, """{"succeeded":true}""", timestamp: null, signature: null));
        Assert.False(refused.Ok);
        Assert.Contains("signature", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>R2.1: a successful answer says so in its words, and an early one says it was parked.</summary>
    [Fact]
    public async Task ASuccessSaysSucceeded_AndAParkSaysParked()
    {
        using var fixture = R2Tests.ActionFixture(sourceSize: 1);
        fixture.Script[1] = GoldpathCampaignActionResult.Accepted("fota-1", TimeSpan.FromMinutes(5));
        var campaign = await AcceptAsync(fixture);
        await using var host = await CallbackHost.StartAsync(fixture);
        var ok = await Body(await host.PostAsync($"/goldpath/campaign/callbacks/winback/{campaign:N}/fota-1", """{"succeeded":true}"""));
        Assert.Contains("published (succeeded)", ok.Message, StringComparison.Ordinal);

        // A claim in flight, an id nobody carries yet: parked.
        var second = await fixture.CreateAsync(R2Tests.Generous());
        await fixture.EnumerateAllAsync(second);
        using var scope = fixture.Services.CreateScope();
        await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, fixture.Reload(second.Id), 1, CancellationToken.None);
        Assert.NotNull(await fixture.Engine.ClaimAsync(scope.ServiceProvider.GetRequiredService<CampaignTestContext>(), second.Id, 1, CancellationToken.None));
        var parked = await Body(await host.PostAsync($"/goldpath/campaign/callbacks/winback/{second.Id:N}/early", """{"succeeded":true}"""));
        Assert.True(parked.Ok);
        Assert.Contains("parked", parked.Message, StringComparison.Ordinal);
    }

    /// <summary>R2.8: a body over 16 KiB is 413 whether it declares its length or streams it; exactly 16 KiB is read.</summary>
    [Fact]
    public async Task ABodyOverTheLimitIs413_AndOneAtTheLimitIsRead()
    {
        using var fixture = R2Tests.ActionFixture(sourceSize: 1);
        await using var host = await CallbackHost.StartAsync(fixture);
        var path = $"/goldpath/campaign/callbacks/winback/{Guid.NewGuid():N}/x";
        static string Sized(int bytes)
        {
            const string head = "{\"succeeded\":true,\"error\":\"";
            const string tail = "\"}";
            return head + new string('x', bytes - head.Length - tail.Length) + tail;
        }

        Assert.Equal(HttpStatusCode.NotFound, (await host.PostAsync(path, Sized(16 * 1024))).StatusCode);   // read, verified, unknown campaign
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await host.PostAsync(path, Sized((16 * 1024) + 1))).StatusCode);

        // Streamed without a Content-Length: the reader stops at the limit.
        var streamed = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StreamContent(new UnseekableStream(Encoding.UTF8.GetBytes(Sized((16 * 1024) + 1)))),
        };
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await host.Client.SendAsync(streamed)).StatusCode);
    }

    /// <summary>R2.8: a signed body that is not a callback is 400 — after the signature, so nobody unsigned learns the shape.</summary>
    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    public async Task ASignedBodyThatIsNotACallbackIs400(string body)
    {
        using var fixture = R2Tests.ActionFixture(sourceSize: 1);
        await using var host = await CallbackHost.StartAsync(fixture);
        var response = await host.PostAsync($"/goldpath/campaign/callbacks/winback/{Guid.NewGuid():N}/x", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False((await Body(response)).Ok);
    }

    /// <summary>R2.8: the signature covers the path without its query string, so a tracking parameter does not break it.</summary>
    [Fact]
    public async Task TheQueryStringIsNotPartOfTheSignedPath()
    {
        using var fixture = R2Tests.ActionFixture(sourceSize: 1);
        await using var host = await CallbackHost.StartAsync(fixture);
        var path = $"/goldpath/campaign/callbacks/winback/{Guid.NewGuid():N}/x";
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = GoldpathCampaignCallbackSignature.Sign(CampaignFixture.CallbackSecret, "POST", path, now, Encoding.UTF8.GetBytes("""{"succeeded":true}"""));
        Assert.Equal(HttpStatusCode.NotFound, (await host.PostRawAsync(path + "?trace=abc", """{"succeeded":true}""", now, signature)).StatusCode);
    }

    /// <summary>R2.8: the window is inclusive — exactly the tolerance away verifies, one second more does not; the host's clock decides.</summary>
    [Fact]
    public async Task TheTimestampWindowIsExactlyTheTolerance_OnTheHostsClock()
    {
        using var fixture = R2Tests.ActionFixture(sourceSize: 1);
        var clock = new ManualClock(DateTimeOffset.UtcNow.AddDays(-3));   // the host's clock, not the machine's
        await using var host = await CallbackHost.StartAsync(fixture, clock);
        var path = $"/goldpath/campaign/callbacks/winback/{Guid.NewGuid():N}/x";
        var at = clock.Now.ToUnixTimeSeconds();
        Assert.Equal(HttpStatusCode.NotFound, (await host.PostAsync(path, "{\"succeeded\":true}", timestamp: at - 300)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.PostAsync(path, "{\"succeeded\":true}", timestamp: at + 300)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostAsync(path, "{\"succeeded\":true}", timestamp: at - 301)).StatusCode);
    }

    /// <summary>R2.8: only the v1 scheme verifies — the right MAC under another scheme name, or a truncated one, is refused.</summary>
    [Fact]
    public async Task OnlyTheV1SchemeVerifies()
    {
        using var fixture = R2Tests.ActionFixture(sourceSize: 1);
        await using var host = await CallbackHost.StartAsync(fixture);
        var path = $"/goldpath/campaign/callbacks/winback/{Guid.NewGuid():N}/x";
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var v1 = GoldpathCampaignCallbackSignature.Sign(CampaignFixture.CallbackSecret, "POST", path, now, Encoding.UTF8.GetBytes("{\"succeeded\":true}"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostRawAsync(path, "{\"succeeded\":true}", now, "v2=" + v1[3..])).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostRawAsync(path, "{\"succeeded\":true}", now, v1[..^2])).StatusCode);
        Assert.Throws<ArgumentException>(() => GoldpathCampaignCallbackSignature.Sign("", "POST", path, now, []));
    }

    /// <summary>R2.8 / runbook §8: a refused callback is counted — the number that says a rotation is half done.</summary>
    [Fact]
    public async Task ARefusedCallbackIsCounted()
    {
        long rejected = 0;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "goldpath_campaign_callbacks_rejected_total")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref rejected, value));
        listener.Start();

        using var fixture = R2Tests.ActionFixture(sourceSize: 1);
        await using var host = await CallbackHost.StartAsync(fixture);
        var before = Interlocked.Read(ref rejected);
        await host.PostRawAsync($"/goldpath/campaign/callbacks/winback/{Guid.NewGuid():N}/x", "{\"succeeded\":true}", timestamp: null, signature: null);
        Assert.True(Interlocked.Read(ref rejected) > before);
    }

    // ---- helpers ----

    private static async Task<GoldpathAdminResult> Body(HttpResponseMessage response)
        => (await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<GoldpathAdminResult>(response.Content))!;

    /// <summary>A body with no length to declare: the server has to read it to know.</summary>
    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();
    }

    private static async Task<Guid> AcceptAsync(CampaignFixture fixture)
    {
        var campaign = await fixture.CreateAsync(R2Tests.Generous(maxAttempts: 3));
        await fixture.EnumerateAllAsync(campaign);
        var before = fixture.Publisher.Published.Count;
        using (var scope = fixture.Services.CreateScope())
        {
            await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, fixture.Reload(campaign.Id), 1, CancellationToken.None);
        }

        // Consume only this campaign's message (the fixture's helper replays everything published).
        var message = fixture.Publisher.Published.Skip(before).OfType<GoldpathCampaignItemMessage>().Single();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
            var item = await fixture.Engine.ClaimAsync(db, message.CampaignId, message.Seq, CancellationToken.None);
            var result = await fixture.Engine.ExecuteItemAsync(scope.ServiceProvider, "winback", message.CampaignId, message.Seq, item!.TargetJson, null, 1, false, CancellationToken.None);
            await fixture.Engine.ApplyOutcomesAsync(db, message.CampaignId, [fixture.Engine.OutcomeFor(message.CampaignId, message.Seq, 1, result)], CancellationToken.None);
        }

        fixture.Publisher.Published.Clear();
        return campaign.Id;
    }

    /// <summary>Runs the sink's flush the moment the callback's context saves its park — the race, made deterministic.</summary>
    private sealed class SinkRacesThePark(Func<Task> flush) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired && eventData.Context!.ChangeTracker.Entries<GoldpathCampaignParkedCallback>().Any())
            {
                Fired = true;
                await flush();
            }

            return result;
        }
    }

    /// <summary>The app's callback surface on a test server, over the fixture's database, engine and publisher.</summary>
    private sealed class CallbackHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private CallbackHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<CallbackHost> StartAsync(CampaignFixture fixture, TimeProvider? clock = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            if (clock is not null)
            {
                builder.Services.AddSingleton(clock);
            }

            builder.Services.AddDbContext<CampaignTestContext>(o => o.UseSqlite(fixture.Connection));
            builder.Services.AddSingleton(fixture.Options);
            builder.Services.AddSingleton(fixture.Engine);
            builder.Services.AddSingleton<IPublishEndpoint>(fixture.Publisher);
            var app = builder.Build();
            app.MapGoldpathCampaignCallbacks<CampaignTestContext>();
            await app.StartAsync();
            return new CallbackHost(app, app.GetTestClient());
        }

        public Task<HttpResponseMessage> PostAsync(string path, string json, string secret = CampaignFixture.CallbackSecret, long? timestamp = null)
        {
            var at = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return PostRawAsync(path, json, at, GoldpathCampaignCallbackSignature.Sign(secret, "POST", path, at, Encoding.UTF8.GetBytes(json)));
        }

        public Task<HttpResponseMessage> PostRawAsync(string path, string json, long? timestamp, string? signature)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            if (timestamp is { } at)
            {
                request.Headers.Add(GoldpathCampaignCallbackSignature.TimestampHeader, at.ToString(CultureInfo.InvariantCulture));
            }

            if (signature is not null)
            {
                request.Headers.Add(GoldpathCampaignCallbackSignature.SignatureHeader, signature);
            }

            return Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
