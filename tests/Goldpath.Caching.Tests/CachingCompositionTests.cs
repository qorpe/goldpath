using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Goldpath.Tests;

/// <summary>
/// What <c>AddGoldpathCaching()</c> composes, observed from the container an app resolves
/// from (caching RFC §3 manifest surface, §4 API surface). The tiers, the TTL and the key
/// helper are the module's whole contract — each is asserted where an app would meet it.
/// </summary>
public sealed class CachingCompositionTests
{
    private sealed class FixedTenant(string tenant) : ITenantContext
    {
        public TenantId? Current { get; } = TenantId.Create(tenant);
    }

    private static IHost Build(Action<GoldpathCachingOptions>? configure = null, Dictionary<string, string?>? config = null)
    {
        var builder = Host.CreateApplicationBuilder();
        if (config is not null)
        {
            builder.Configuration.AddInMemoryCollection(config);
        }

        builder.AddGoldpathCaching(configure);
        return builder.Build();
    }

    private static bool IsRedis(IDistributedCache cache)
        => cache.GetType().FullName!.Contains("StackExchangeRedis", StringComparison.Ordinal);

    /// <summary>RFC §3: <c>levels</c> absent means BOTH tiers; the level names are not case-sensitive.</summary>
    [Fact]
    public void L2_redis_is_the_default_and_the_level_name_is_case_insensitive()
    {
        using var byDefault = Build();
        Assert.True(IsRedis(byDefault.Services.GetRequiredService<IDistributedCache>()),
            "levels unset = [l1, l2]: the L2 store must be Redis");

        using var upperCase = Build(o => o.Levels = ["L1", "L2"]);
        Assert.True(IsRedis(upperCase.Services.GetRequiredService<IDistributedCache>()),
            "\"L2\" names the Redis tier exactly like \"l2\"");
    }

    /// <summary>
    /// RFC §3 <c>levels: [l1]</c>: no Redis, yet an <see cref="IDistributedCache"/> still exists
    /// in-process — the idempotency store and Mediant's query path both talk to one.
    /// </summary>
    [Fact]
    public void L1_only_still_supplies_an_in_process_distributed_cache()
    {
        using var host = Build(o => o.Levels = ["l1"]);

        Assert.IsType<MemoryDistributedCache>(host.Services.GetService<IDistributedCache>());
    }

    /// <summary>
    /// RFC §3: the manifest surface arrives as <c>Goldpath:DistributedCaching</c> configuration,
    /// and the bound options are the ones the app can resolve.
    /// </summary>
    [Fact]
    public void The_configuration_section_drives_the_tiers_and_the_ttl()
    {
        using var host = Build(config: new()
        {
            ["Goldpath:DistributedCaching:Levels:0"] = "l1",
            ["Goldpath:DistributedCaching:DefaultTtlSeconds"] = "42",
            ["Goldpath:DistributedCaching:RedisConnectionName"] = "cache",
        });

        var options = host.Services.GetRequiredService<GoldpathCachingOptions>();
        Assert.Equal(["l1"], options.Levels!);
        Assert.Equal(42, options.DefaultTtlSeconds);
        Assert.Equal("cache", options.RedisConnectionName);
        Assert.IsType<MemoryDistributedCache>(host.Services.GetRequiredService<IDistributedCache>());
    }

    /// <summary>RFC §3 <c>defaultTtl</c> (300 s unless configured) is HybridCache's default entry TTL, on both tiers.</summary>
    [Fact]
    public void The_default_ttl_is_hybrid_caches_default_entry_expiration()
    {
        using var configured = Build(o => { o.Levels = ["l1"]; o.DefaultTtlSeconds = 42; });
        var entry = configured.Services.GetRequiredService<IOptions<HybridCacheOptions>>().Value.DefaultEntryOptions;
        Assert.NotNull(entry);
        Assert.Equal(TimeSpan.FromSeconds(42), entry.Expiration);
        Assert.Equal(TimeSpan.FromSeconds(42), entry.LocalCacheExpiration);

        using var byDefault = Build(o => o.Levels = ["l1"]);
        Assert.Equal(TimeSpan.FromSeconds(300),
            byDefault.Services.GetRequiredService<IOptions<HybridCacheOptions>>().Value.DefaultEntryOptions!.Expiration);
    }

    /// <summary>
    /// RFC D3 / §4: the injected <see cref="GoldpathCacheKeys"/> scopes every key by the ambient
    /// tenant — the injection path an app uses, not just the static helper.
    /// </summary>
    [Fact]
    public void The_injected_key_helper_scopes_keys_by_the_ambient_tenant()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITenantContext>(new FixedTenant("acme"));
        builder.AddGoldpathCaching(o => o.Levels = ["l1"]);
        using var host = builder.Build();

        using var scope = host.Services.CreateScope();
        var keys = scope.ServiceProvider.GetRequiredService<GoldpathCacheKeys>();
        Assert.Equal("goldpath:acme:rates:current", keys.For("rates", "current"));
    }
}
