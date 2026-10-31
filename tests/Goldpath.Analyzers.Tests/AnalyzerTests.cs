using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace Goldpath.Analyzers.Tests;

public class AnalyzerTests
{
    // Hermetic stubs: rules match types by metadata name, so minimal stand-ins are enough —
    // no package graph inside the test compilations.
    private const string Stubs = """
        namespace Goldpath
        {
            public interface IIntegrationEvent { }
            public interface IAuditedEntity { }
        }
        namespace Mediant
        {
            public interface INotification { }
        }
        namespace MassTransit
        {
            public interface IPublishEndpoint
            {
                System.Threading.Tasks.Task Publish<T>(T message) where T : class;
            }
            public interface IConsumer<in T> where T : class { }
        }
        namespace Goldpath
        {
            public interface IIntegrationEventHandler<in T> where T : class, IIntegrationEvent { }
        }
        namespace Microsoft.EntityFrameworkCore
        {
            public static class Ef
            {
                public static void Migrate(this object database) { }
                public static void FromSqlRaw(this object set, string sql) { }
            }
        }
        namespace System.Linq
        {
            public static class Queryable
            {
                public static IQueryable<T> Skip<T>(this IQueryable<T> q, int n) => q;
                public static IQueryable<T> Take<T>(this IQueryable<T> q, int n) => q;
            }
            public interface IQueryable<T> { }
        }
        """;

    private static Task Verify<TAnalyzer>(string source, params DiagnosticResult[] expected)
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        var test = new CSharpAnalyzerTest<TAnalyzer, DefaultVerifier>
        {
            TestCode = source + "\n" + Stubs,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        };
        test.ExpectedDiagnostics.AddRange(expected);
        return test.RunAsync();
    }

    [Fact]
    public Task GOLDPATH0102_flags_new_httpclient()
        => Verify<NewHttpClientAnalyzer>("""
            public class C
            {
                public object M() => {|#0:new System.Net.Http.HttpClient()|};
            }
            """,
            new DiagnosticResult(Descriptors.NewHttpClient).WithLocation(0));

    [Fact]
    public Task GOLDPATH0102_ignores_factory_created_clients()
        => Verify<NewHttpClientAnalyzer>("""
            public class C
            {
                public object M(System.Net.Http.HttpClient injected) => injected;
            }
            """);

    [Fact]
    public Task GOLDPATH0202_flags_skip_take()
        => Verify<OffsetPaginationAnalyzer>("""
            using System.Linq;
            public class C
            {
                public object M(IQueryable<int> q) => {|#0:q.Skip(10).Take(10)|};
            }
            """,
            new DiagnosticResult(Descriptors.OffsetPagination).WithLocation(0));

    [Fact]
    public Task GOLDPATH0202_ignores_take_alone()
        => Verify<OffsetPaginationAnalyzer>("""
            using System.Linq;
            public class C
            {
                public object M(IQueryable<int> q) => q.Take(10);
            }
            """);

    [Fact]
    public Task GOLDPATH0301_flags_datetime_on_marked_entity()
        => Verify<DateTimeOnEntityAnalyzer>("""
            public class Order : Goldpath.IAuditedEntity
            {
                public System.DateTime {|#0:CreatedOn|} { get; set; }
            }
            """,
            new DiagnosticResult(Descriptors.DateTimeOnEntity).WithLocation(0).WithArguments("CreatedOn"));

    [Fact]
    public Task GOLDPATH0301_ignores_datetimeoffset_and_unmarked_types()
        => Verify<DateTimeOnEntityAnalyzer>("""
            public class Order : Goldpath.IAuditedEntity
            {
                public System.DateTimeOffset CreatedOn { get; set; }
            }
            public class Plain
            {
                public System.DateTime Whatever { get; set; }
            }
            """);

    [Fact]
    public Task GOLDPATH0302_flags_unguarded_migrate()
        => Verify<RuntimeMigrateAnalyzer>("""
            using Microsoft.EntityFrameworkCore;
            public class C
            {
                public void M(object db) => {|#0:db.Migrate()|};
            }
            """,
            new DiagnosticResult(Descriptors.RuntimeMigrate).WithLocation(0).WithArguments("Migrate"));

    [Theory]
    [InlineData("ISoftDeletable")]
    [InlineData("IMultiTenant")]
    public Task GOLDPATH0301_flags_datetime_under_every_goldpath_marker(string marker)
        => Verify<DateTimeOnEntityAnalyzer>($$"""
            public class Order : Goldpath.{{marker}}
            {
                public System.DateTime {|#0:CreatedOn|} { get; set; }
            }
            namespace Goldpath { public interface ISoftDeletable { } public interface IMultiTenant { } }
            """,
            new DiagnosticResult(Descriptors.DateTimeOnEntity).WithLocation(0).WithArguments("CreatedOn"));

    [Fact]
    public Task GOLDPATH0302_flags_every_runtime_schema_call_and_only_EF_Cores()
        => Verify<RuntimeMigrateAnalyzer>("""
            using Microsoft.EntityFrameworkCore;
            public class C
            {
                public async System.Threading.Tasks.Task M(object db)
                {
                    await {|#0:db.MigrateAsync()|};
                    {|#1:db.EnsureCreated()|};
                    await {|#2:db.EnsureCreatedAsync()|};
                    Other.Tools.Migrate(db);   // quiet: not EF Core's
                }
            }
            namespace Microsoft.EntityFrameworkCore
            {
                public static class EfSchema
                {
                    public static System.Threading.Tasks.Task MigrateAsync(this object database) => System.Threading.Tasks.Task.CompletedTask;
                    public static bool EnsureCreated(this object database) => true;
                    public static System.Threading.Tasks.Task<bool> EnsureCreatedAsync(this object database) => System.Threading.Tasks.Task.FromResult(true);
                }
            }
            namespace Other { public static class Tools { public static void Migrate(object database) { } } }
            """,
            new DiagnosticResult(Descriptors.RuntimeMigrate).WithLocation(0).WithArguments("MigrateAsync"),
            new DiagnosticResult(Descriptors.RuntimeMigrate).WithLocation(1).WithArguments("EnsureCreated"),
            new DiagnosticResult(Descriptors.RuntimeMigrate).WithLocation(2).WithArguments("EnsureCreatedAsync"));

    [Fact]
    public Task GOLDPATH0302_allows_development_guarded_migrate()
        => Verify<RuntimeMigrateAnalyzer>("""
            using Microsoft.EntityFrameworkCore;
            public class C
            {
                public bool IsDevelopment() => true;
                public void M(object db)
                {
                    if (IsDevelopment())
                    {
                        db.Migrate();
                    }
                }
            }
            """);

    [Fact]
    public Task GOLDPATH0303_flags_interpolated_raw_sql()
        => Verify<RawSqlInterpolationAnalyzer>("""
            using Microsoft.EntityFrameworkCore;
            public class C
            {
                public void M(object set, string name) => {|#0:set.FromSqlRaw($"select * from t where n = '{name}'")|};
            }
            """,
            new DiagnosticResult(Descriptors.RawSqlInterpolation).WithLocation(0).WithArguments("FromSqlRaw"));

    [Fact]
    public Task GOLDPATH0303_allows_constant_sql()
        => Verify<RawSqlInterpolationAnalyzer>("""
            using Microsoft.EntityFrameworkCore;
            public class C
            {
                public void M(object set) => set.FromSqlRaw("select 1");
            }
            """);

    [Fact]
    public Task GOLDPATH0401_flags_publishing_unmarked_type()
        => Verify<PublishUnmarkedAnalyzer>("""
            public record PlainEvent(string Value);
            public class C
            {
                public System.Threading.Tasks.Task M(MassTransit.IPublishEndpoint bus)
                    => {|#0:bus.Publish(new PlainEvent("x"))|};
            }
            """,
            new DiagnosticResult(Descriptors.PublishUnmarked).WithLocation(0).WithArguments("PlainEvent"));

    [Fact]
    public Task GOLDPATH0401_allows_marked_type()
        => Verify<PublishUnmarkedAnalyzer>("""
            public record OrderConfirmed(string Id) : Goldpath.IIntegrationEvent;
            public class C
            {
                public System.Threading.Tasks.Task M(MassTransit.IPublishEndpoint bus)
                    => bus.Publish(new OrderConfirmed("x"));
            }
            """);

    [Fact]
    public Task GOLDPATH0402_flags_cross_marked_notification()
        => Verify<NotificationCrossMarkedAnalyzer>("""
            public record {|#0:Confused|}(string Id) : Mediant.INotification, Goldpath.IIntegrationEvent;
            """,
            new DiagnosticResult(Descriptors.NotificationCrossMarked).WithLocation(0).WithArguments("Confused"));

    [Fact]
    public Task GOLDPATH0402_allows_single_world_types()
        => Verify<NotificationCrossMarkedAnalyzer>("""
            public record DomainEvent(string Id) : Mediant.INotification;
            public record IntegrationEvent(string Id) : Goldpath.IIntegrationEvent;
            """);

    [Fact]
    public Task GOLDPATH0405_flags_an_adopter_consumer()
        => Verify<ConsumerImplementedAnalyzer>("""
            namespace Shop.Orders
            {
                public record OrderPlaced(string Id) : Goldpath.IIntegrationEvent;
                public class {|#0:OrderPlacedConsumer|} : MassTransit.IConsumer<OrderPlaced> { }
            }
            """,
            new DiagnosticResult(Descriptors.ConsumerImplemented).WithLocation(0).WithArguments("OrderPlacedConsumer"));

    [Fact]
    public Task GOLDPATH0405_still_fires_when_the_adopter_namespace_merely_STARTS_with_Goldpath()
        => Verify<ConsumerImplementedAnalyzer>("""
            namespace GoldpathTemplate.Api.Orders
            {
                public record OrderPlaced(string Id) : Goldpath.IIntegrationEvent;
                public class {|#0:OrderPlacedConsumer|} : MassTransit.IConsumer<OrderPlaced> { }
            }
            """,
            new DiagnosticResult(Descriptors.ConsumerImplemented).WithLocation(0).WithArguments("OrderPlacedConsumer"));

    [Fact]
    public Task GOLDPATH0405_exempts_the_packages_and_says_nothing_about_the_seam_itself()
        => Verify<ConsumerImplementedAnalyzer>("""
            namespace Goldpath.Campaign
            {
                public record Item(string Id) : Goldpath.IIntegrationEvent;
                public class ItemConsumer : MassTransit.IConsumer<Item> { }
            }
            namespace Shop.Orders
            {
                public record OrderPlaced(string Id) : Goldpath.IIntegrationEvent;
                public class OrderPlacedHandler : Goldpath.IIntegrationEventHandler<OrderPlaced> { }
            }
            """);

    [Fact]
    public Task GOLDPATH0404_flags_application_code_injecting_the_transport_publisher()
        => Verify<PublishEndpointInjectedAnalyzer>("""
            namespace Shop.Orders
            {
                public class CreateOrderHandler
                {
                    public CreateOrderHandler(MassTransit.IPublishEndpoint {|#0:publisher|}) { }
                }
            }
            """,
            new DiagnosticResult(Descriptors.PublishEndpointInjected).WithLocation(0).WithArguments("CreateOrderHandler"));

    [Fact]
    public Task GOLDPATH0404_still_fires_when_the_adopter_namespace_merely_STARTS_with_Goldpath()
        // The template's own namespace is GoldpathTemplate.*; a prefix-based exemption would
        // have silently spared the exact code this rule guards (review #159 R3).
        => Verify<PublishEndpointInjectedAnalyzer>("""
            namespace GoldpathTemplate.Application.Orders.Features
            {
                public class CreateOrderHandler
                {
                    public CreateOrderHandler(MassTransit.IPublishEndpoint {|#0:publisher|}) { }
                }
            }
            """,
            new DiagnosticResult(Descriptors.PublishEndpointInjected).WithLocation(0).WithArguments("CreateOrderHandler"));

    [Fact]
    public Task GOLDPATH0404_allows_the_seam_implementation_inside_Goldpath()
        // The seam has to delegate to SOMETHING: Goldpath's own namespace owns that line.
        => Verify<PublishEndpointInjectedAnalyzer>("""
            namespace Goldpath
            {
                public class MassTransitIntegrationEventPublisher
                {
                    public MassTransitIntegrationEventPublisher(MassTransit.IPublishEndpoint endpoint) { }
                }
            }
            """);
}
