using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Goldpath.Campaign.Tests;

/// <summary>
/// Model-contract goldens. Each test builds a FRESH model (no cached service provider) so
/// Stryker's mutation switching actually reaches OnModelCreating — EF's static model cache
/// would otherwise make every mapping mutant invisible.
/// </summary>
public class ModelContractTests
{
    private static IModel FreshModel()
    {
        var options = new DbContextOptionsBuilder<CampaignTestContext>()
            .UseSqlite("DataSource=:memory:")
            .EnableServiceProviderCaching(false)
            .Options;
        using var db = new CampaignTestContext(options);
        return db.Model;
    }

    [Fact]
    public void CampaignTableContract()
    {
        var entity = FreshModel().FindEntityType(typeof(GoldpathCampaign))!;
        Assert.Equal("GoldpathCampaigns", entity.GetTableName());
        Assert.Equal(["Id"], entity.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Equal(ValueGenerated.Never, entity.FindProperty("Id")!.ValueGenerated);
        Assert.True(entity.FindProperty("State")!.IsConcurrencyToken);
    }

    [Fact]
    public void CampaignMaxLengthGoldens()
    {
        var entity = FreshModel().FindEntityType(typeof(GoldpathCampaign))!;
        Assert.Equal(128, entity.FindProperty("Type")!.GetMaxLength());
        Assert.Equal(256, entity.FindProperty("Name")!.GetMaxLength());
        Assert.Equal(64, entity.FindProperty("TimeZoneId")!.GetMaxLength());
        Assert.Equal(256, entity.FindProperty("CreatedBy")!.GetMaxLength());
        Assert.Equal(512, entity.FindProperty("LastVerb")!.GetMaxLength());
        Assert.Equal(128, entity.FindProperty("Tenant")!.GetMaxLength());
        Assert.Equal(256, entity.FindProperty("EnumeratedKey")!.GetMaxLength());
    }

    [Fact]
    public void CampaignIndexGolden()
    {
        var entity = FreshModel().FindEntityType(typeof(GoldpathCampaign))!;
        var index = Assert.Single(entity.GetIndexes());
        Assert.Equal(["State", "CreatedAt"], index.Properties.Select(p => p.Name));
    }

    [Fact]
    public void ItemTableContract()
    {
        var entity = FreshModel().FindEntityType(typeof(GoldpathCampaignItem))!;
        Assert.Equal("GoldpathCampaignItems", entity.GetTableName());
        Assert.Equal(["CampaignId", "Seq"], entity.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Equal(1024, entity.FindProperty("Error")!.GetMaxLength());
        Assert.Equal(64, entity.FindProperty("ErrorCode")!.GetMaxLength());
        Assert.Equal(128, entity.FindProperty("CorrelationId")!.GetMaxLength());
        // R2: the ripeness range and the callback lookup each walk their own index.
        var indexes = entity.GetIndexes().Select(i => string.Join("+", i.Properties.Select(p => p.Name))).Order().ToArray();
        Assert.Equal(["CampaignId+CorrelationId", "CampaignId+State+NextAttemptAt", "CampaignId+State+ReleasedAt"], indexes);
    }

    [Fact]
    public void AuditTableContract()
    {
        var entity = FreshModel().FindEntityType(typeof(GoldpathCampaignAudit))!;
        Assert.Equal("GoldpathCampaignAudit", entity.GetTableName());
        Assert.Equal(["Id"], entity.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Equal(256, entity.FindProperty("Actor")!.GetMaxLength());
        Assert.Equal(64, entity.FindProperty("Action")!.GetMaxLength());
        Assert.Equal(1024, entity.FindProperty("Detail")!.GetMaxLength());
        var index = Assert.Single(entity.GetIndexes());
        Assert.Equal(["CampaignId", "At"], index.Properties.Select(p => p.Name));
    }

    [Fact]
    public void AuditEntityDefaults()
    {
        var audit = new GoldpathCampaignAudit();
        Assert.Equal("", audit.Actor);
        Assert.Equal("", audit.Action);
        Assert.Null(audit.Detail);
        Assert.Equal(0, audit.Id);
    }

    [Fact]
    public void CampaignEntityDefaults()
    {
        var campaign = new GoldpathCampaign();
        Assert.Equal(GoldpathCampaignState.Created, campaign.State);
        Assert.Equal("", campaign.Type);
        Assert.Equal("", campaign.Name);
        Assert.Equal("{}", campaign.ParametersJson);
        Assert.Equal("UTC", campaign.TimeZoneId);
        Assert.Equal("", campaign.CreatedBy);
        Assert.Equal(0, campaign.EnumeratedThrough);
        Assert.False(campaign.EnumerationComplete);
        Assert.Equal(0, campaign.ReleasedThrough);
        Assert.Equal(0, campaign.SucceededCount);
        Assert.Equal(0, campaign.FailedCount);
        Assert.Equal(0, campaign.ReleasedToday);
        Assert.Null(campaign.CompletedAt);
        Assert.Null(campaign.LastVerb);
        Assert.Null(campaign.Tenant);
        Assert.Equal(GoldpathCampaignPriority.Normal, campaign.Priority);
        Assert.Null(campaign.EnumeratedKey);
    }

    [Fact]
    public void ItemEntityDefaults()
    {
        var item = new GoldpathCampaignItem();
        Assert.Equal(GoldpathCampaignItemState.Pending, item.State);
        Assert.Equal("", item.TargetJson);
        Assert.Null(item.ClaimedAt);
        Assert.Null(item.CompletedAt);
        Assert.Null(item.Error);
        Assert.Null(item.ErrorCode);
        Assert.Null(item.NextAttemptAt);
        Assert.Null(item.AckDeadline);
        Assert.Null(item.CorrelationId);
        Assert.Null(item.ReleasedAt);
        Assert.Equal(0, item.Attempts);
    }

    [Fact]
    public void StateEnumValuesArePersistenceContracts()
    {
        Assert.Equal(0, (int)GoldpathCampaignState.Created);
        Assert.Equal(1, (int)GoldpathCampaignState.Enumerating);
        Assert.Equal(2, (int)GoldpathCampaignState.Running);
        Assert.Equal(3, (int)GoldpathCampaignState.Paused);
        Assert.Equal(4, (int)GoldpathCampaignState.Completed);
        Assert.Equal(5, (int)GoldpathCampaignState.CompletedWithFailures);
        Assert.Equal(6, (int)GoldpathCampaignState.Aborted);
        Assert.Equal(0, (int)GoldpathCampaignItemState.Pending);
        Assert.Equal(1, (int)GoldpathCampaignItemState.Released);
        Assert.Equal(2, (int)GoldpathCampaignItemState.Processing);
        Assert.Equal(3, (int)GoldpathCampaignItemState.Succeeded);
        Assert.Equal(4, (int)GoldpathCampaignItemState.Failed);
        Assert.Equal(5, (int)GoldpathCampaignItemState.Aborted);
        Assert.Equal(6, (int)GoldpathCampaignItemState.AwaitingRetry);
        Assert.Equal(7, (int)GoldpathCampaignItemState.AwaitingAck);
        Assert.Equal(7, (int)GoldpathCampaignState.ExpiredIncomplete);
        // R2.5: Normal must stay 0 — every pre-R2 row reads as Normal through the new column.
        Assert.Equal(0, (int)GoldpathCampaignPriority.Normal);
        Assert.Equal(1, (int)GoldpathCampaignPriority.High);
        Assert.Equal(2, (int)GoldpathCampaignPriority.Low);
    }

    [Fact]
    public void MessagesAreCoordinatesNotPayloads()
    {
        var item = new GoldpathCampaignItemMessage(Guid.Empty, 7, "winback");
        Assert.Equal(7, item.Seq);
        Assert.Equal("winback", item.Type);
        var outcome = new GoldpathCampaignOutcomeMessage(Guid.Empty, 7, false, "refused");
        Assert.False(outcome.Succeeded);
        Assert.Equal("refused", outcome.Error);
        // R2 widened the outcome without moving the positional shape: the defaults are R1's behaviour.
        Assert.True(outcome.Retryable);
        Assert.False(outcome.Accepted);
        Assert.Null(outcome.ErrorCode);
        Assert.Null(outcome.CorrelationId);
        Assert.Null(outcome.AckDeadline);
    }
}
