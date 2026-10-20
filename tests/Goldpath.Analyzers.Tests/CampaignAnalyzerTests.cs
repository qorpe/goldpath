using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace Goldpath.Analyzers.Tests;

public class CampaignAnalyzerTests
{
    // Hermetic stubs: rules match Goldpath.GoldpathCampaignOptions / Goldpath.IGoldpathCampaignItemHandler`1
    // (and the SMTP client types for GP1703) by metadata name.
    private const string Stubs = """
        namespace Goldpath
        {
            public sealed class GoldpathCampaignOptions
            {
                public GoldpathCampaignOptions AddCampaign<TTarget>(string key, System.Action<GoldpathCampaignTypeBuilder<TTarget>> configure) where TTarget : class => this;
            }
            public sealed class GoldpathCampaignTypeBuilder<TTarget>
            {
                public GoldpathCampaignTypeBuilder<TTarget> MaxTargets(long maxTargets) => this;
                public GoldpathCampaignTypeBuilder<TTarget> SignedCallbacks(string secret, string previousSecret = null) => this;
                public GoldpathCampaignTypeBuilder<TTarget> Targets(System.Func<System.IServiceProvider, System.Collections.Generic.IReadOnlyDictionary<string, string>, System.Collections.Generic.IAsyncEnumerable<TTarget>> targets) => this;
            }
            public sealed class GoldpathCampaignItemContext { }
            public static class GoldpathCampaignCallbackEndpoints
            {
                public static object MapGoldpathCampaignCallbacks<TContext>(this object endpoints, string prefix = "/goldpath/campaign/callbacks", string policy = null) => endpoints;
            }
            public interface IGoldpathCampaignItemHandler<in TTarget> where TTarget : class
            {
                System.Threading.Tasks.Task ExecuteAsync(TTarget target, GoldpathCampaignItemContext context, System.Threading.CancellationToken cancellationToken);
            }
        }
        namespace MailKit.Net.Smtp
        {
            public sealed class SmtpClient { }
        }
        public sealed class WinbackTarget { public int Id { get; set; } }
        public sealed class FakeDb
        {
            public int SaveChanges() => 0;
            public System.Threading.Tasks.Task<int> SaveChangesAsync(System.Threading.CancellationToken ct) => System.Threading.Tasks.Task.FromResult(0);
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
    public Task GOLDPATH1701_flags_a_type_without_a_ceiling()
        => Verify<CampaignCeilingAnalyzer>("""
            public static class Composition
            {
                public static void Wire(Goldpath.GoldpathCampaignOptions options)
                    => {|#0:options.AddCampaign<WinbackTarget>("winback", c => c.Targets((sp, p) => null!))|};
            }
            """,
            new DiagnosticResult(Descriptors.CampaignWithoutCeiling).WithLocation(0).WithArguments("WinbackTarget"));

    [Fact]
    public Task GOLDPATH1701_is_silent_when_the_ceiling_is_declared()
        => Verify<CampaignCeilingAnalyzer>("""
            public static class Composition
            {
                public static void Wire(Goldpath.GoldpathCampaignOptions options)
                    => options.AddCampaign<WinbackTarget>("winback", c => c.MaxTargets(2000000).Targets((sp, p) => null!));
            }
            """);

    [Fact]
    public Task GOLDPATH1702_flags_SaveChanges_inside_an_item_handler()
        => Verify<CampaignHandlerSaveAnalyzer>("""
            public sealed class WinbackHandler : Goldpath.IGoldpathCampaignItemHandler<WinbackTarget>
            {
                private readonly FakeDb _db = new();
                public async System.Threading.Tasks.Task ExecuteAsync(WinbackTarget target, Goldpath.GoldpathCampaignItemContext context, System.Threading.CancellationToken cancellationToken)
                    => await {|#0:_db.SaveChangesAsync(cancellationToken)|};
            }
            """,
            new DiagnosticResult(Descriptors.CampaignHandlerSavesPerItem).WithLocation(0).WithArguments("WinbackHandler"));

    [Fact]
    public Task GOLDPATH1702_is_silent_outside_a_handler()
        => Verify<CampaignHandlerSaveAnalyzer>("""
            public sealed class RegularService
            {
                private readonly FakeDb _db = new();
                public System.Threading.Tasks.Task<int> FlushAsync(System.Threading.CancellationToken ct)
                    => _db.SaveChangesAsync(ct);
            }
            """);

    [Fact]
    public Task GOLDPATH1703_flags_an_smtp_client_inside_an_item_handler()
        => Verify<CampaignNotificationBypassAnalyzer>("""
            public sealed class MailingHandler : Goldpath.IGoldpathCampaignItemHandler<WinbackTarget>
            {
                public System.Threading.Tasks.Task ExecuteAsync(WinbackTarget target, Goldpath.GoldpathCampaignItemContext context, System.Threading.CancellationToken cancellationToken)
                {
                    var client = {|#0:new MailKit.Net.Smtp.SmtpClient()|};
                    return System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """,
            new DiagnosticResult(Descriptors.CampaignHandlerBypassesNotification).WithLocation(0).WithArguments("MailingHandler"));

    [Fact]
    public Task GOLDPATH1703_is_silent_outside_a_handler()
        => Verify<CampaignNotificationBypassAnalyzer>("""
            public sealed class MailService
            {
                public object Make() => new MailKit.Net.Smtp.SmtpClient();
            }
            """);
    // ── GP1704: callbacks nobody can sign ─────────────────────────────────────────────
    [Fact]
    public Task GOLDPATH1704_flags_callbacks_mapped_with_no_signing_type()
        => Verify<CampaignCallbackSigningAnalyzer>("""
            public static class Composition
            {
                public static void Wire(Goldpath.GoldpathCampaignOptions options, object app)
                {
                    options.AddCampaign<WinbackTarget>("winback", c => c.MaxTargets(2000000).Targets((sp, p) => null!));
                    {|#0:Goldpath.GoldpathCampaignCallbackEndpoints.MapGoldpathCampaignCallbacks<FakeDb>(app)|};
                }
            }
            """,
            new DiagnosticResult(Descriptors.CampaignCallbacksWithoutSigning).WithLocation(0));

    [Fact]
    public Task GOLDPATH1704_is_silent_once_a_type_signs()
        => Verify<CampaignCallbackSigningAnalyzer>("""
            public static class Composition
            {
                public static void Wire(Goldpath.GoldpathCampaignOptions options, object app, string secret)
                {
                    options.AddCampaign<WinbackTarget>("winback", c => c.MaxTargets(2000000).SignedCallbacks(secret).Targets((sp, p) => null!));
                    Goldpath.GoldpathCampaignCallbackEndpoints.MapGoldpathCampaignCallbacks<FakeDb>(app);
                }
            }
            """);

    [Fact]
    public Task GOLDPATH1704_is_silent_when_callbacks_are_not_mapped()
        => Verify<CampaignCallbackSigningAnalyzer>("""
            public static class Composition
            {
                public static void Wire(Goldpath.GoldpathCampaignOptions options)
                    => options.AddCampaign<WinbackTarget>("winback", c => c.MaxTargets(2000000).Targets((sp, p) => null!));
            }
            """);
}
