using System.Globalization;
using System.Reflection;
using MassTransit;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Goldpath.Campaign.Tests;

/// <summary>
/// G1 Part 3/4 (RFC goldpath-messaging-engines §4.1 rows 11–13, §4.2): campaign consumes through the seam,
/// keeps the queue its fleet already drains, and leaves no engine type on its public surface.
/// </summary>
public class SeamTests
{
    /// <summary>The shape of the consumers this replaced: generic over the context. The oracle names it.</summary>
    public sealed class LegacyShape<TContext> : IConsumer<GoldpathCampaignItemMessage>
    {
        public Task Consume(ConsumeContext<GoldpathCampaignItemMessage> context) => Task.CompletedTask;
    }

    // Context names an adopter could have: the template's, an acronym the seam's NameOf splits differently,
    // a leading acronym, a digit boundary, an underscore, a non-ASCII capital, a capital I under tr-TR.
    public sealed class OrdersDbContext;

    public sealed class V2DB;

    public sealed class ABCDbContext;

    public sealed class Db2Context;

    public sealed class Orders_Db;

    public sealed class ŞirketDb;

    public sealed class IletisimDbContext;

    public static TheoryData<Type> Contexts =>
    [
        typeof(OrdersDbContext), typeof(V2DB), typeof(ABCDbContext), typeof(Db2Context),
        typeof(Orders_Db), typeof(ŞirketDb), typeof(IletisimDbContext), typeof(CampaignTestContext),
    ];

    [Theory]
    [MemberData(nameof(Contexts))]
    public void Both_handlers_drain_the_queue_the_replaced_consumers_drained(Type context)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        try
        {
            var oracle = (string)typeof(IEndpointNameFormatter).GetMethod(nameof(IEndpointNameFormatter.Consumer))!
                .MakeGenericMethod(typeof(LegacyShape<>).MakeGenericType(context))
                .Invoke(KebabCaseEndpointNameFormatter.Instance, null)!;

            Assert.Equal(oracle, GoldpathCampaignEndpoints.QueueOf(context));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void The_queue_is_not_the_seams_derived_name_where_the_two_disagree()
    {
        // Why the queue is computed and not NameOf: the two split an acronym differently, and a moved
        // queue strands what is in it.
        Assert.Equal("v2-db", GoldpathCampaignEndpoints.QueueOf(typeof(V2DB)));
        Assert.Equal("v2db", GoldpathHandlerEndpoints.NameOf(typeof(V2DB)));
        Assert.Equal("orders-db-context", GoldpathCampaignEndpoints.QueueOf<OrdersDbContext>());
    }

    [Fact]
    public void The_sink_is_grouped_as_it_always_was()
    {
        var grouping = GoldpathCampaignEndpoints.OutcomeBatch;

        Assert.Equal(200, grouping.MessageLimit);
        Assert.Equal(TimeSpan.FromMilliseconds(500), grouping.TimeLimit);
        Assert.Equal(1, grouping.ConcurrencyLimit);
        Assert.NotSame(grouping, GoldpathCampaignEndpoints.OutcomeBatch);   // a fresh copy each time: no caller can retune the sink
    }

    [Fact]
    public void Campaign_registered_twice_on_one_message_path_is_refused_in_words()
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => Host.CreateApplicationBuilder().AddGoldpathMessaging(messaging => messaging
            .AddGoldpathCampaign<CampaignTestContext>()
            .AddGoldpathCampaign<CampaignTestContext>()
            .UseRabbitMq()));

        Assert.Contains("GoldpathCampaignOutcomeHandler", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The check the PublicAPI text gate cannot make (RFC §4.2): a PublicAPI line records a type's name, not its
    /// base type or interfaces, so a public type deriving from an engine type survives a clean engine-gate.
    /// </summary>
    [Fact]
    public void No_public_campaign_type_or_signature_names_an_engine()
    {
        var offenders = new List<string>();
        foreach (var type in typeof(GoldpathCampaignExtensions).Assembly.GetExportedTypes())
        {
            for (var basis = type.BaseType; basis is not null; basis = basis.BaseType)
            {
                Check($"{type.Name} derives from", basis);
            }

            foreach (var contract in type.GetInterfaces())
            {
                Check($"{type.Name} implements", contract);
            }

            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                switch (member)
                {
                    case MethodBase method:
                        foreach (var parameter in method.GetParameters())
                        {
                            Check($"{type.Name}.{method.Name}({parameter.Name})", parameter.ParameterType);
                        }

                        if (method is MethodInfo info)
                        {
                            Check($"{type.Name}.{method.Name} returns", info.ReturnType);
                        }

                        break;
                    case PropertyInfo property:
                        Check($"{type.Name}.{property.Name}", property.PropertyType);
                        break;
                    case FieldInfo field:
                        Check($"{type.Name}.{field.Name}", field.FieldType);
                        break;
                }
            }
        }

        Assert.True(offenders.Count == 0, "campaign's public surface names an engine: " + string.Join("; ", offenders));

        void Check(string where, Type candidate)
        {
            if (Names(candidate))
            {
                offenders.Add($"{where} {candidate}");
            }
        }

        static bool Names(Type candidate)
        {
            if (candidate.HasElementType)
            {
                return Names(candidate.GetElementType()!);
            }

            var space = candidate.Namespace ?? "";
            return space.StartsWith("MassTransit", StringComparison.Ordinal)
                || space.StartsWith("Wolverine", StringComparison.Ordinal)
                || (candidate.IsGenericType && candidate.GetGenericArguments().Any(Names));
        }
    }
}
