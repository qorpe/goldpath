using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goldpath.Campaign.Tests;

/// <summary>
/// Admin-contract R4.2 and R4.3 (#296, slices R4-S3 and R4-S4): the failures drill-down returns each
/// item's error code and walks the whole set by keyset, and the outcomes aggregate counts a
/// campaign's items by state and its failures by code, in an order fixed by the service rather than
/// by the database.
/// </summary>
public class R4FailuresAndOutcomesTests
{
    // ---- R4.2: failures by code, with a cursor ----

    /// <summary>The codes come from the real path: a handler's Failed(code, …), and HANDLER_EXCEPTION for a throw.</summary>
    [Fact]
    public async Task EveryFailedRow_CarriesTheCodeItsHandlerGave()
    {
        using var fixture = R2Tests.ActionFixture(sourceSize: 3);
        fixture.Script[1] = GoldpathCampaignActionResult.Failed("DEVICE_OFFLINE", "the device is offline", retryable: false);
        fixture.Script[3] = GoldpathCampaignActionResult.Failed("QUOTA", "the core refused: quota", retryable: false);
        var campaign = await fixture.CreateAsync(R2Tests.Generous(maxAttempts: 1));
        await fixture.EnumerateAllAsync(campaign);
        using (var scope = fixture.Services.CreateScope())
        {
            await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, fixture.Reload(campaign.Id), 3, CancellationToken.None);
        }

        await fixture.ConsumeAllPublishedAsync();

        var failed = await fixture.Admin().GetFailedItemsAsync(campaign.Id, afterSeq: 0, take: 10, CancellationToken.None);
        Assert.Equal([1L, 3L], failed.Select(f => f.Seq));
        Assert.Equal(["DEVICE_OFFLINE", "QUOTA"], failed.Select(f => f.ErrorCode));
        Assert.Contains("offline", failed[0].Error, StringComparison.Ordinal);
    }

    /// <summary>A callback's own code reaches the row too (R2.1): the device answers "failed, CERT_EXPIRED" to an acceptance.</summary>
    [Fact]
    public async Task ACallbacksOwnCode_IsTheFailedRowsCode()
    {
        using var fixture = R2Tests.ActionFixture(sourceSize: 1);
        fixture.Script[1] = GoldpathCampaignActionResult.Accepted("dev-9", TimeSpan.FromMinutes(5));
        var campaign = await fixture.CreateAsync(R2Tests.Generous(maxAttempts: 1));
        await fixture.EnumerateAllAsync(campaign);
        using (var scope = fixture.Services.CreateScope())
        {
            await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, fixture.Reload(campaign.Id), 1, CancellationToken.None);
        }

        await fixture.ConsumeAllPublishedAsync();   // the acceptance lands: AwaitingAck
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
            var resolution = await fixture.Engine.ResolveCallbackAsync(db, "winback", campaign.Id, "dev-9",
                new GoldpathCampaignCallbackRequest(false, "CERT_EXPIRED", "the device's certificate expired", Retryable: false), CancellationToken.None);
            await fixture.Engine.ApplyOutcomesAsync(db, campaign.Id, [resolution.Outcome!], CancellationToken.None);   // the sink's flush
        }

        var item = Assert.Single(await fixture.Admin().GetFailedItemsAsync(campaign.Id, 0, 10, CancellationToken.None));
        Assert.Equal("CERT_EXPIRED", item.ErrorCode);
    }

    /// <summary>The RFC's proof: 1,203 failures walked at take=500 in three pages, no gap, no repeat, every code kept.</summary>
    [Fact]
    public async Task TwelveHundredThreeFailures_WalkInThreePages_WithNoGapAndNoRepeat()
    {
        using var fixture = new CampaignFixture();
        var campaign = await fixture.CreateAsync();
        // Failed items interleaved with succeeded ones, so the cursor must skip rows that are not failures.
        Seed(fixture, campaign.Id, Enumerable.Range(1, 2_406).Select(seq => seq % 2 == 0
            ? Item(campaign.Id, seq, GoldpathCampaignItemState.Failed, CodeOf(seq))
            : Item(campaign.Id, seq, GoldpathCampaignItemState.Succeeded, null)));
        var admin = fixture.Admin();

        var pages = new List<IReadOnlyList<GoldpathCampaignFailedItem>>();
        long after = 0;
        for (var guard = 0; guard < 5; guard++)   // a cursor that does not advance fails here, not by exhausting memory
        {
            var page = await admin.GetFailedItemsAsync(campaign.Id, after, 500, CancellationToken.None);
            pages.Add(page);
            if (page.Count < 500)
            {
                break;   // a short page is the end
            }

            after = page[^1].Seq;
        }

        Assert.Equal([500, 500, 203], pages.Select(p => p.Count));
        var walked = pages.SelectMany(p => p).ToList();
        Assert.Equal(Enumerable.Range(1, 1_203).Select(n => (long)n * 2), walked.Select(f => f.Seq));
        Assert.All(walked, f => Assert.Equal(CodeOf(f.Seq), f.ErrorCode));

        Assert.Empty(await admin.GetFailedItemsAsync(campaign.Id, walked[^1].Seq, 500, CancellationToken.None));
    }

    [Fact]
    public async Task TheShippedOverload_IsTheFirstPage_AndTakeKeepsItsClamp()
    {
        using var fixture = new CampaignFixture();
        var campaign = await fixture.CreateAsync();
        Seed(fixture, campaign.Id, Enumerable.Range(1, 600).Select(seq => Item(campaign.Id, seq, GoldpathCampaignItemState.Failed, "X")));
        var admin = fixture.Admin();

        var shipped = await admin.GetFailedItemsAsync(campaign.Id, 1_000, CancellationToken.None);
        Assert.Equal(500, shipped.Count);   // the uniform [1, 500] clamp
        Assert.Equal(1, shipped[0].Seq);
        Assert.Single(await admin.GetFailedItemsAsync(campaign.Id, afterSeq: 0, take: 0, CancellationToken.None));
        Assert.Equal(1, (await admin.GetFailedItemsAsync(campaign.Id, afterSeq: -5, take: 1, CancellationToken.None))[0].Seq);
    }

    [Fact]
    public async Task AFailureUnderR1_HasANullCode_AndAnotherCampaignsFailuresNeverAppear()
    {
        using var fixture = new CampaignFixture();
        var mine = await fixture.CreateAsync();
        var other = await fixture.CreateAsync();
        Seed(fixture, mine.Id, [Item(mine.Id, 1, GoldpathCampaignItemState.Failed, null)]);
        Seed(fixture, other.Id, [Item(other.Id, 1, GoldpathCampaignItemState.Failed, "OTHER"), Item(other.Id, 2, GoldpathCampaignItemState.Failed, "OTHER")]);

        var item = Assert.Single(await fixture.Admin().GetFailedItemsAsync(mine.Id, 0, 100, CancellationToken.None));
        Assert.Null(item.ErrorCode);
    }

    // ---- R4.3: the outcomes aggregate ----

    /// <summary>The RFC's proof: five states, three codes and one failure with no code, counted exactly.</summary>
    [Fact]
    public async Task Outcomes_CountByState_AndFailuresByCode()
    {
        using var fixture = new CampaignFixture();
        var campaign = await fixture.CreateAsync();
        var other = await fixture.CreateAsync();
        var seq = 0;
        IEnumerable<GoldpathCampaignItem> Many(int count, GoldpathCampaignItemState state, string? code)
            => Enumerable.Range(0, count).Select(_ => Item(campaign.Id, ++seq, state, code)).ToList();
        Seed(fixture, campaign.Id, [
            .. Many(7, GoldpathCampaignItemState.Pending, null),
            .. Many(2, GoldpathCampaignItemState.Released, null),
            .. Many(1, GoldpathCampaignItemState.Processing, null),
            .. Many(5, GoldpathCampaignItemState.Succeeded, null),
            .. Many(4, GoldpathCampaignItemState.Failed, "ACK_TIMEOUT"),
            .. Many(2, GoldpathCampaignItemState.Failed, "DEVICE_OFFLINE"),
            .. Many(1, GoldpathCampaignItemState.Failed, "HANDLER_EXCEPTION"),
            .. Many(1, GoldpathCampaignItemState.Failed, null),
            // a code on a non-failed item (a retry that later succeeded) is not a failure's code
            .. Many(1, GoldpathCampaignItemState.AwaitingRetry, "ACK_TIMEOUT"),
        ]);
        Seed(fixture, other.Id, [Item(other.Id, 1, GoldpathCampaignItemState.Failed, "ACK_TIMEOUT")]);

        var outcomes = await fixture.Admin().GetOutcomesAsync(campaign.Id, CancellationToken.None);

        Assert.NotNull(outcomes);
        Assert.Equal(new Dictionary<string, long>
        {
            ["Pending"] = 7,
            ["Released"] = 2,
            ["Processing"] = 1,
            ["Succeeded"] = 5,
            ["Failed"] = 8,
            ["AwaitingRetry"] = 1,
        }, outcomes.ByState);
        Assert.DoesNotContain("Aborted", outcomes.ByState.Keys);   // a state with no items is omitted
        Assert.Equal(["Pending", "Released", "Processing", "Succeeded", "Failed", "AwaitingRetry"], outcomes.ByState.Keys);   // in state order
        Assert.Equal(
            [new("ACK_TIMEOUT", 4), new("DEVICE_OFFLINE", 2), new("HANDLER_EXCEPTION", 1), new GoldpathCampaignCodeCount(null, 1)],
            outcomes.FailedByCode);
        Assert.False(outcomes.FailedCodesTruncated);
    }

    /// <summary>
    /// 25 distinct codes answer 20 entries and the truncation flag. Equal counts order by code
    /// ORDINAL — upper case before lower case, which a database's linguistic collation would not
    /// give — with no code last among them.
    /// </summary>
    [Fact]
    public async Task TwentyFiveCodes_AnswerTwenty_Truncated_InAStableOrdinalOrder()
    {
        using var fixture = new CampaignFixture();
        var campaign = await fixture.CreateAsync();
        var seq = 0;
        var rows = new List<GoldpathCampaignItem>();
        // Three failures under "a-lower" and "B-UPPER", two with no code, one each under 22 more codes.
        foreach (var (code, count) in new (string?, int)[] { ("a-lower", 3), ("B-UPPER", 3), (null, 2) }
                     .Concat(Enumerable.Range(0, 22).Select(n => ((string?)string.Create(CultureInfo.InvariantCulture, $"C{n:00}"), 1))))
        {
            rows.AddRange(Enumerable.Range(0, count).Select(_ => Item(campaign.Id, ++seq, GoldpathCampaignItemState.Failed, code)));
        }

        Seed(fixture, campaign.Id, rows);
        var admin = fixture.Admin();

        var first = await admin.GetOutcomesAsync(campaign.Id, CancellationToken.None);
        var second = await admin.GetOutcomesAsync(campaign.Id, CancellationToken.None);

        Assert.NotNull(first);
        Assert.True(first.FailedCodesTruncated);
        Assert.Equal(20, first.FailedByCode.Count);
        Assert.Equal(["B-UPPER", "a-lower", null, "C00", "C01"], first.FailedByCode.Take(5).Select(c => c.Code));
        Assert.Equal([3L, 3L, 2L, 1L, 1L], first.FailedByCode.Take(5).Select(c => c.Count));
        Assert.Equal("C16", first.FailedByCode[^1].Code);
        Assert.Equal(first.FailedByCode, second!.FailedByCode);
        Assert.Equal(30, first.ByState["Failed"]);   // 3 + 3 + 2 + 22: the truncation hides codes, never counts
    }

    /// <summary>
    /// Equal counts straddling the twentieth place: the contract's ordinal order gives the place to
    /// "B-UPPER", so the query must FETCH it, not only re-sort what it fetched. SQLite is bytewise by
    /// default; the PostgreSQL (ICU) and Oracle (linguistic session) halves are CampaignOutcomesProof.
    /// </summary>
    [Fact]
    public async Task ATieAcrossTheTwentiethPlace_IsBrokenOrdinally()
    {
        using var fixture = new CampaignFixture();
        var campaign = await fixture.CreateAsync();
        var seq = 0;
        var rows = new List<GoldpathCampaignItem>();
        foreach (var (code, count) in Enumerable.Range(0, 19).Select(n => (string.Create(CultureInfo.InvariantCulture, $"C{n:00}"), 10))
                     .Concat([("a-lower", 1), ("a-second", 1), ("B-UPPER", 1)]))
        {
            rows.AddRange(Enumerable.Range(0, count).Select(_ => Item(campaign.Id, ++seq, GoldpathCampaignItemState.Failed, code)));
        }

        Seed(fixture, campaign.Id, rows);

        var outcomes = await fixture.Admin().GetOutcomesAsync(campaign.Id, CancellationToken.None);

        Assert.True(outcomes!.FailedCodesTruncated);
        Assert.Equal(20, outcomes.FailedByCode.Count);
        Assert.Equal(new GoldpathCampaignCodeCount("B-UPPER", 1), outcomes.FailedByCode[^1]);
    }

    /// <summary>The tie-break's collation is the provider's bytewise one, named by provider.</summary>
    [Theory]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL", "C")]
    [InlineData("Oracle.EntityFrameworkCore", "BINARY")]
    [InlineData("Microsoft.EntityFrameworkCore.Sqlite", null)]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer", null)]
    [InlineData(null, null)]
    public void TheTieBreakCollation_IsTheProvidersBytewiseOne(string? provider, string? expected)
        => Assert.Equal(expected, GoldpathCampaignAdminService<CampaignTestContext>.BinaryCollation(provider));

    [Fact]
    public async Task ExactlyTwentyCodes_AreNotTruncated()
    {
        using var fixture = new CampaignFixture();
        var campaign = await fixture.CreateAsync();
        Seed(fixture, campaign.Id, Enumerable.Range(1, 20).Select(seq =>
            Item(campaign.Id, seq, GoldpathCampaignItemState.Failed, string.Create(CultureInfo.InvariantCulture, $"E{seq:00}"))));

        var outcomes = await fixture.Admin().GetOutcomesAsync(campaign.Id, CancellationToken.None);

        Assert.Equal(20, outcomes!.FailedByCode.Count);
        Assert.False(outcomes.FailedCodesTruncated);
    }

    [Fact]
    public async Task ACampaignWithNoItems_AnswersEmptyCounts_AndAnUnknownIdAnswersNull()
    {
        using var fixture = new CampaignFixture();
        var campaign = await fixture.CreateAsync();
        var admin = fixture.Admin();

        var empty = await admin.GetOutcomesAsync(campaign.Id, CancellationToken.None);
        Assert.NotNull(empty);
        Assert.Empty(empty.ByState);
        Assert.Empty(empty.FailedByCode);
        Assert.False(empty.FailedCodesTruncated);

        Assert.Null(await admin.GetOutcomesAsync(Guid.NewGuid(), CancellationToken.None));
    }

    // ---- wired: the admin HTTP surface ----

    [Fact]
    public async Task TheAdminRoutes_WalkFailuresByAfterSeq_AndAnswerOutcomes_And404ForAnUnknownId()
    {
        using var fixture = new CampaignFixture();
        var campaign = await fixture.CreateAsync();
        Seed(fixture, campaign.Id, [
            Item(campaign.Id, 1, GoldpathCampaignItemState.Failed, "ACK_TIMEOUT"),
            Item(campaign.Id, 2, GoldpathCampaignItemState.Succeeded, null),
            Item(campaign.Id, 3, GoldpathCampaignItemState.Failed, null),
            Item(campaign.Id, 4, GoldpathCampaignItemState.Failed, "ACK_TIMEOUT"),
        ]);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<CampaignTestContext>(o => o.UseSqlite(fixture.Connection));
        builder.Services.AddSingleton(fixture.Admin());
        await using var app = builder.Build();
        app.MapGoldpathCampaignAdmin<CampaignTestContext>(exposeUnsecured: true);
        await app.StartAsync();
        using var client = app.GetTestClient();
        var root = string.Create(CultureInfo.InvariantCulture, $"/goldpath/admin/campaign/{campaign.Id}");

        // An R3 client: no afterSeq, the first page, as before — and the new field beside the old ones.
        var all = JsonDocument.Parse(await client.GetStringAsync($"{root}/failures")).RootElement;
        Assert.Equal(3, all.GetArrayLength());
        Assert.Equal("ACK_TIMEOUT", all[0].GetProperty("errorCode").GetString());
        Assert.Equal(JsonValueKind.Null, all[1].GetProperty("errorCode").ValueKind);
        Assert.Equal(1, all[0].GetProperty("seq").GetInt64());

        var page = JsonDocument.Parse(await client.GetStringAsync($"{root}/failures?afterSeq=1&take=1")).RootElement;
        Assert.Equal(3, Assert.Single(page.EnumerateArray()).GetProperty("seq").GetInt64());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{root}/failures?afterSeq=abc")).StatusCode);

        var outcomes = JsonDocument.Parse(await client.GetStringAsync($"{root}/outcomes")).RootElement;
        Assert.Equal(3, outcomes.GetProperty("byState").GetProperty("Failed").GetInt64());
        Assert.Equal(1, outcomes.GetProperty("byState").GetProperty("Succeeded").GetInt64());
        var codes = outcomes.GetProperty("failedByCode");
        Assert.Equal("ACK_TIMEOUT", codes[0].GetProperty("code").GetString());
        Assert.Equal(2, codes[0].GetProperty("count").GetInt64());
        Assert.Equal(JsonValueKind.Null, codes[1].GetProperty("code").ValueKind);
        Assert.False(outcomes.GetProperty("failedCodesTruncated").GetBoolean());

        var unknown = await client.GetAsync(string.Create(CultureInfo.InvariantCulture, $"/goldpath/admin/campaign/{Guid.NewGuid()}/outcomes"));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Empty(await unknown.Content.ReadAsStringAsync());
    }

    private static string CodeOf(long seq) => (seq % 3) switch { 0 => "ACK_TIMEOUT", 1 => "DEVICE_OFFLINE", _ => "QUOTA" };

    private static GoldpathCampaignItem Item(Guid campaignId, long seq, GoldpathCampaignItemState state, string? code)
        => new()
        {
            CampaignId = campaignId,
            Seq = seq,
            TargetJson = "{}",
            State = state,
            ErrorCode = code,
            Error = state == GoldpathCampaignItemState.Failed ? "refused" : null,
            CompletedAt = state is GoldpathCampaignItemState.Failed or GoldpathCampaignItemState.Succeeded ? DateTimeOffset.UnixEpoch : null,
        };

    private static void Seed(CampaignFixture fixture, Guid campaignId, IEnumerable<GoldpathCampaignItem> items)
        => fixture.Mutate(db =>
        {
            db.Set<GoldpathCampaignItem>().RemoveRange(db.Set<GoldpathCampaignItem>().Where(i => i.CampaignId == campaignId));
            db.Set<GoldpathCampaignItem>().AddRange(items);
        });
}
