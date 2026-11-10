using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Goldpath.Tests;

public sealed record CustomerLogModel
{
    [GoldpathPersonalData]
    public string? NationalId { get; init; }

    public string? Segment { get; init; }
}

internal static partial class TestLog
{
    [LoggerMessage(1, LogLevel.Information, "customer event")]
    public static partial void CustomerSeen(ILogger logger, [LogProperties] CustomerLogModel customer);
}

public sealed class LogRedactionTests
{
    [Fact]
    public void Classified_properties_arrive_redacted_through_the_mel_path()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddGoldpathDataProtection();
        builder.Logging.EnableRedaction();
        builder.Logging.AddFakeLogging();
        using var host = builder.Build();

        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("test");
        TestLog.CustomerSeen(logger, new CustomerLogModel { NationalId = "12345678901", Segment = "gold" });

        var record = Assert.Single(
            host.Services.GetRequiredService<FakeLogCollector>().GetSnapshot(),
            r => r.Message == "customer event");
        var state = record.StructuredState!;

        Assert.Contains(state, kv => kv.Key == "customer.NationalId" && kv.Value == GoldpathErasingRedactor.Token);
        Assert.Contains(state, kv => kv.Key == "customer.Segment" && kv.Value == "gold");
    }

    /// <summary>
    /// Threat model T-DPK-02: an application that never calls <c>EnableRedaction()</c> (the generated
    /// templates do not) still never logs a classified property in clear: the logging generator
    /// drops it rather than write it. It is redacted only where redaction is enabled.
    /// </summary>
    [Fact]
    public void Without_EnableRedaction_a_classified_property_is_dropped_never_logged_in_clear()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddGoldpathDataProtection();
        builder.Logging.AddFakeLogging();
        using var host = builder.Build();

        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("test");
        TestLog.CustomerSeen(logger, new CustomerLogModel { NationalId = "12345678901", Segment = "gold" });

        var record = Assert.Single(
            host.Services.GetRequiredService<FakeLogCollector>().GetSnapshot(),
            r => r.Message == "customer event");
        Assert.DoesNotContain(record.StructuredState!, kv => kv.Value is not null && kv.Value.Contains("12345678901", StringComparison.Ordinal));
        Assert.DoesNotContain(record.StructuredState!, kv => kv.Key == "customer.NationalId");
        Assert.Contains(record.StructuredState!, kv => kv.Key == "customer.Segment" && kv.Value == "gold");
    }
}
