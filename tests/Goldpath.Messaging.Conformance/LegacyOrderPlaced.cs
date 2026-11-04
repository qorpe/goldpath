namespace Goldpath.Messaging.Conformance.Legacy;

/// <summary>
/// The event as an older producer or consumer still knows it (S2.17): its wire identity derived from the type,
/// <c>Goldpath.Messaging.Conformance.Legacy:OrderPlaced</c>, which <see cref="OrderSubmitted"/> keeps.
/// </summary>
public sealed class OrderPlaced : IIntegrationEvent
{
    public Guid Id { get; set; }
}
