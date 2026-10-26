using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

[assembly: Goldpath.GoldpathMessagingEngine(typeof(Goldpath.MassTransitMessagingEngine))]

namespace Goldpath;

/// <summary>
/// The contract an engine adapter implements (RFC goldpath-messaging-engines §4.1 row 5): it takes what a
/// <see cref="GoldpathMessagingBuilder"/> recorded and composes it on its engine — the handlers' endpoints,
/// the corporate pipeline on every one of them, the outbox and inbox, the transport, and the
/// <see cref="IIntegrationEventPublisher"/> the application publishes through. An application never names
/// an engine: the adapter package declares its engine with <see cref="GoldpathMessagingEngineAttribute"/>, and
/// <see cref="GoldpathMessagingExtensions.AddGoldpathMessaging{TBuilder}(TBuilder, Action{GoldpathMessagingBuilder})"/>
/// finds it.
/// </summary>
public interface IGoldpathMessagingEngine
{
    /// <summary>The engine's manifest value (<c>providers.messagingEngine</c>), lower case: <c>masstransit</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Registers the engine for <paramref name="composition"/> into <paramref name="services"/>. The floor
    /// has already registered the options and the message-scoped tenant holder; the engine registers the bus
    /// and the <see cref="IIntegrationEventPublisher"/>. Refuses, in words, a composition it cannot honour.
    /// </summary>
    void Compose(GoldpathMessagingComposition composition, IServiceCollection services, IConfiguration configuration);

    /// <summary>
    /// Publishes <paramref name="body"/> under <paramref name="contract"/>'s message type straight to the
    /// broker, as an upstream service would, shaped by <paramref name="message"/>: what
    /// <see cref="GoldpathMessagingTestHost"/> publishes with. <paramref name="body"/> may be an instance of
    /// <paramref name="contract"/> or any object whose properties stand for it (a wider or an older shape).
    /// </summary>
    Task PublishAsync(IServiceProvider services, Type contract, object body, GoldpathOutgoingMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Declares the <see cref="IGoldpathMessagingEngine"/> an adapter assembly carries. The type needs a
/// parameterless constructor, which may be internal. Exactly one adapter may be referenced; with none, the MassTransit engine
/// <c>Goldpath.Messaging</c> carries until its adapter package is extracted is the default.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class GoldpathMessagingEngineAttribute(Type engineType) : Attribute
{
    /// <summary>The engine type, an <see cref="IGoldpathMessagingEngine"/>.</summary>
    public Type EngineType { get; } = engineType ?? throw new ArgumentNullException(nameof(engineType));
}

/// <summary>Which transport a message path runs on.</summary>
public enum GoldpathMessagingTransport
{
    /// <summary>None selected yet; <c>AddGoldpathMessaging</c> refuses it.</summary>
    None,

    /// <summary>RabbitMQ, at the connection string <see cref="GoldpathMessagingComposition.RabbitMqConnectionName"/> names.</summary>
    RabbitMq,

    /// <summary>The engine's in-process transport, for tests.</summary>
    InMemory,
}

/// <summary>A single-message handler a builder recorded, with the endpoint (queue) it drains.</summary>
/// <param name="Event">The integration event type.</param>
/// <param name="Handler">The <see cref="IIntegrationEventHandler{TEvent}"/> implementation.</param>
/// <param name="EndpointName">The queue it drains.</param>
public sealed record GoldpathHandlerRegistration(Type Event, Type Handler, string EndpointName);

/// <summary>A batch handler a builder recorded, with its grouping and the endpoint (queue) it drains.</summary>
/// <param name="Event">The integration event type.</param>
/// <param name="Handler">The <see cref="IIntegrationEventBatchHandler{TEvent}"/> implementation.</param>
/// <param name="Options">How deliveries are grouped into batches.</param>
/// <param name="EndpointName">The queue it drains.</param>
public sealed record GoldpathBatchHandlerRegistration(Type Event, Type Handler, GoldpathBatchOptions Options, string EndpointName);

/// <summary>
/// What a <see cref="GoldpathMessagingBuilder"/> recorded, as an engine receives it in
/// <see cref="IGoldpathMessagingEngine.Compose"/>. The handler lists are snapshots taken when the messaging
/// block returned; <see cref="Options"/> and <see cref="Outbox"/> are the application's own objects, which an
/// engine reads and does not change.
/// </summary>
public sealed class GoldpathMessagingComposition
{
    internal GoldpathMessagingComposition(GoldpathMessagingBuilder builder, bool waitUntilStarted)
    {
        Builder = builder;
        Options = builder.Options;
        Handlers = [.. builder.Handlers.Select(h => new GoldpathHandlerRegistration(h.Event, h.Handler, builder.EndpointOf(h.Event, h.Handler)))];
        BatchHandlers = [.. builder.BatchHandlers.Select(b => new GoldpathBatchHandlerRegistration(b.Event, b.Handler, b.Options, b.EndpointName))];
        OutboxContext = builder.OutboxContext;
        Outbox = builder.Outbox;
        Transport = builder.Transport;
        RabbitMqConnectionName = builder.RabbitMqConnectionName;
        WaitUntilStarted = waitUntilStarted;
    }

    /// <summary>The message-path tuning, bound from <c>Goldpath:Messaging</c> and changed by the application.</summary>
    public GoldpathMessagingOptions Options { get; }

    /// <summary>The single-message handlers, each with its endpoint.</summary>
    public IReadOnlyList<GoldpathHandlerRegistration> Handlers { get; }

    /// <summary>The batch handlers, each with its grouping and endpoint.</summary>
    public IReadOnlyList<GoldpathBatchHandlerRegistration> BatchHandlers { get; }

    /// <summary>The application's context the outbox and inbox ride on, or null when no outbox was composed.</summary>
    public Type? OutboxContext { get; }

    /// <summary>The outbox options, or null when no outbox was composed.</summary>
    public GoldpathOutboxOptions? Outbox { get; }

    /// <summary>The transport selected.</summary>
    public GoldpathMessagingTransport Transport { get; }

    /// <summary>The connection-string name RabbitMQ is reached at; absent from configuration means the engine's local default.</summary>
    public string RabbitMqConnectionName { get; }

    /// <summary>
    /// Whether the host's start must return only once the bus has started: what a test host needs, so a
    /// publish right after the start is not lost to a binding that does not exist yet.
    /// </summary>
    public bool WaitUntilStarted { get; }

    /// <summary>The builder this was read from: the key of the engine's test hooks.</summary>
    internal GoldpathMessagingBuilder Builder { get; }
}

/// <summary>
/// Finds the engine an application references (RFC goldpath-messaging-engines §4.1 row 5, §9 Q2): every
/// loaded assembly and every <c>Goldpath.Messaging.*.dll</c> beside the application is read for
/// <see cref="GoldpathMessagingEngineAttribute"/>. An adapter package's engine wins over the MassTransit
/// engine <c>Goldpath.Messaging</c> still carries (open-threads T44: until Part 4/4 extracts it, it is the
/// default rather than an adapter); two adapter packages are refused, naming both.
/// </summary>
internal static class GoldpathMessagingEngines
{
    private static readonly Assembly s_floor = typeof(GoldpathMessagingEngines).Assembly;

    /// <summary>The engine for this process, or the one named <paramref name="named"/> among those found.</summary>
    public static IGoldpathMessagingEngine Resolve(string? named = null) => Choose(Declared(), named);

    /// <summary>
    /// The decision, apart from where the declarations came from. <paramref name="declared"/> pairs each
    /// declaring assembly's name with its engine type.
    /// </summary>
    internal static IGoldpathMessagingEngine Choose(IEnumerable<(string Assembly, Type Engine)> declared, string? named = null)
    {
        // Ordered by assembly name, so the decision and its refusal read the same however the declarations were found.
        var all = declared.Distinct().OrderBy(d => d.Assembly, StringComparer.Ordinal).ToList();
        var engines = all.Select(d => (d.Assembly, Engine: Create(d.Engine, d.Assembly))).ToList();
        if (named is not null)
        {
            var match = engines.Where(e => string.Equals(e.Engine.Name, named, StringComparison.Ordinal)).ToList();
            return match.Count == 1
                ? match[0].Engine
                : throw new InvalidOperationException(
                    $"No single messaging engine named '{named}' is referenced; found: {Describe(engines)}.");
        }

        var adapters = engines.Where(e => e.Assembly != s_floor.GetName().Name).ToList();
        return adapters.Count switch
        {
            1 => adapters[0].Engine,
            0 => engines.FirstOrDefault(e => e.Assembly == s_floor.GetName().Name).Engine
                ?? throw new InvalidOperationException(
                    "No messaging engine is referenced. Reference the adapter package your manifest selects (providers.messagingEngine)."),
            _ => throw new InvalidOperationException(
                $"Several messaging engine adapters are referenced: {Describe(adapters)}. Reference only the one your manifest " +
                "selects (providers.messagingEngine); one message path runs on one engine."),
        };
    }

    private static string Describe(IEnumerable<(string Assembly, IGoldpathMessagingEngine Engine)> engines)
        => string.Join(", ", engines.Select(e => $"'{e.Engine.Name}' ({e.Assembly})"));

    private static IGoldpathMessagingEngine Create(Type engineType, string assembly)
    {
        if (!typeof(IGoldpathMessagingEngine).IsAssignableFrom(engineType) || engineType.IsAbstract)
        {
            throw new InvalidOperationException(
                $"{assembly} declares '{engineType.FullName}' as its messaging engine, which is not a concrete {nameof(IGoldpathMessagingEngine)}.");
        }

        // Null only for a Nullable<T>, which cannot be an engine: the type check above has refused it.
        return (IGoldpathMessagingEngine)Activator.CreateInstance(engineType, nonPublic: true)!;
    }

    private static IEnumerable<(string Assembly, Type Engine)> Declared()
    {
        // By name: an assembly loaded twice (two load contexts) is one declaration, not two adapters.
        var assemblies = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic))
        {
            assemblies.TryAdd(loaded.GetName().Name ?? "", loaded);
        }

        // An adapter the application never names in code is not loaded yet, and the compiler drops an
        // unused reference: the package's assembly beside the application is what proves it is referenced.
        foreach (var path in SafeFiles(AppContext.BaseDirectory, "Goldpath.Messaging.*.dll"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!assemblies.ContainsKey(name))
            {
                try
                {
                    assemblies[name] = Assembly.Load(AssemblyName.GetAssemblyName(path));
                }
                catch (Exception e) when (e is BadImageFormatException or FileLoadException or FileNotFoundException)
                {
                    // Not a loadable assembly (a resource satellite, a native file): not an adapter.
                }
            }
        }

        foreach (var (name, assembly) in assemblies)
        {
            if (DeclarationOf(assembly) is { } declaration)
            {
                yield return (name, declaration.EngineType);
            }
        }
    }

    private static GoldpathMessagingEngineAttribute? DeclarationOf(Assembly assembly)
    {
        try
        {
            return assembly.GetCustomAttribute<GoldpathMessagingEngineAttribute>();
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException)
        {
            // An assembly whose own attributes cannot be resolved here carries no engine this process can run.
            return null;
        }
    }

    private static IEnumerable<string> SafeFiles(string directory, string pattern)
    {
        try
        {
            return Directory.GetFiles(directory, pattern);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)   // a missing directory is an IOException too
        {
            return [];
        }
    }
}
