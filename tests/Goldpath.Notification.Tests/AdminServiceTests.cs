using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goldpath.Notification.Tests;

public class AdminServiceTests : IDisposable
{
    private readonly NotificationFixture _fixture;
    private readonly GoldpathNotificationAdminService<NotificationTestContext> _admin;

    public AdminServiceTests()
    {
        _fixture = new NotificationFixture(o => o.MaySend((r, _) => Task.FromResult(!r.DedupKey.Contains("supp"))));
        _admin = new GoldpathNotificationAdminService<NotificationTestContext>(
            _fixture.Options, _fixture.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Take_is_clamped_a_zero_or_negative_ask_still_answers_one_row()
    {
        // H8 frozen contract: take rides AdminPaging.Clamp — never an unbounded (or empty-
        // by-accident) query. The floor is the observable edge: 0 and negative answer 1.
        await _fixture.RequestAsync(NotificationFixture.Renewal("renewal:a"));
        await _fixture.RequestAsync(NotificationFixture.Renewal("renewal:b"));

        Assert.Single(await _admin.GetNotificationsAsync(null, null, null, 0, CancellationToken.None));
        Assert.Single(await _admin.GetNotificationsAsync(null, null, null, -7, CancellationToken.None));
    }

    [Fact]
    public async Task Templates_view_reports_states_hash_retention_and_queue_age()
    {
        await _fixture.RequestAsync(NotificationFixture.Renewal("renewal:1"));
        await _fixture.RequestAsync(NotificationFixture.Renewal("renewal:supp"));
        await _fixture.RequestAsync(NotificationFixture.Renewal("renewal:2", name: "FAIL"));
        await _fixture.RunSendPassAsync();   // 1 → Sent, supp stays Suppressed, FAIL → Failed
        await _fixture.RequestAsync(NotificationFixture.Renewal("renewal:3"));   // fresh Requested

        var status = Assert.Single(await _admin.GetTemplatesAsync(CancellationToken.None));
        Assert.Equal("policy-renewal", status.Key);
        Assert.Equal(_fixture.Options.Template("policy-renewal").Hash, status.Hash);
        Assert.Equal(TimeSpan.FromDays(90), status.DeleteBodyAfter);
        Assert.Equal(1, status.ByState["Sent"]);
        Assert.Equal(1, status.ByState["Suppressed"]);
        Assert.Equal(1, status.ByState["Failed"]);
        Assert.Equal(1, status.ByState["Requested"]);
        Assert.NotNull(status.OldestRequestedSeconds);
        Assert.True(status.OldestRequestedSeconds >= 0);
    }

    [Fact]
    public async Task Notification_queries_filter_and_fail_closed_on_tenant()
    {
        var mineReq = NotificationFixture.Renewal("renewal:mine");
        await _fixture.RequestAsync(new GoldpathNotificationRequest(
            mineReq.Template, mineReq.Channel, mineReq.Recipient, mineReq.Culture, mineReq.Tokens, mineReq.DedupKey)
        { Tenant = "agency-1" });
        await _fixture.RequestAsync(NotificationFixture.Renewal("renewal:other"));

        Assert.Equal(2, (await _admin.GetNotificationsAsync(null, null, null, 10, CancellationToken.None)).Count);
        Assert.Single(await _admin.GetNotificationsAsync(["requested"], ["policy-renewal"], "agency-1", 10, CancellationToken.None));
        Assert.Empty(await _admin.GetNotificationsAsync(null, ["no-such-template"], null, 10, CancellationToken.None));
        Assert.Equal(2, (await _admin.GetNotificationsAsync(null, [], null, 10, CancellationToken.None)).Count);   // an empty filter is no filter

        var mine = Assert.Single(await _admin.GetNotificationsAsync(null, null, "agency-1", 10, CancellationToken.None));
        Assert.NotNull(await _admin.GetNotificationAsync(mine.Id, "agency-1", CancellationToken.None));
        Assert.Null(await _admin.GetNotificationAsync(mine.Id, "agency-2", CancellationToken.None));   // fail-closed
        Assert.Null(await _admin.GetNotificationAsync(Guid.NewGuid(), null, CancellationToken.None));
    }

    [Fact]
    public async Task Recipients_are_masked_on_every_surface()
    {
        await _fixture.RequestAsync(NotificationFixture.Renewal("renewal:mask"));
        var info = Assert.Single(await _admin.GetNotificationsAsync(null, null, null, 10, CancellationToken.None));
        Assert.Equal("o***@e***", info.MaskedRecipient);   // omer@example.test → first chars only
        Assert.DoesNotContain("example.test", info.MaskedRecipient, StringComparison.Ordinal);

        var detail = await _admin.GetNotificationAsync(info.Id, null, CancellationToken.None);
        Assert.Equal("o***@e***", detail!.MaskedRecipient);   // the detail view masks too
    }

    [Fact]
    public void The_mask_is_a_golden_shape()
    {
        Assert.Equal("o***@e***", GoldpathNotificationAdminService<NotificationTestContext>.MaskRecipient("omer@example.test"));
        Assert.Equal("+***", GoldpathNotificationAdminService<NotificationTestContext>.MaskRecipient("+905551112233"));
        Assert.Equal("x***", GoldpathNotificationAdminService<NotificationTestContext>.MaskRecipient("x@"));   // degenerate address
        Assert.Equal("", GoldpathNotificationAdminService<NotificationTestContext>.MaskRecipient(""));
    }

    [Fact]
    public async Task Suppression_and_failure_reports_carry_the_evidence()
    {
        await _fixture.RequestAsync(NotificationFixture.Renewal("renewal:supp-1"));
        await _fixture.RequestAsync(NotificationFixture.Renewal("renewal:dead", name: "FAIL"));
        await _fixture.RunSendPassAsync();

        var suppression = Assert.Single(await _admin.GetSuppressionsAsync(null, 10, CancellationToken.None));
        Assert.Equal("Suppressed", suppression.State);
        Assert.Contains("MaySend", suppression.Detail, StringComparison.Ordinal);

        var failure = Assert.Single(await _admin.GetFailuresAsync(null, 10, CancellationToken.None));
        Assert.Equal("Failed", failure.State);
        Assert.Equal(3, failure.Attempts);
        Assert.Contains("gateway refused", failure.Detail, StringComparison.Ordinal);
    }
}
