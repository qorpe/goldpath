using Microsoft.Extensions.Logging;

namespace Goldpath;

/// <summary>Where approval state lives. The module ships an in-memory store for tests and
/// single-node hosts; a database-backed store composes through this seam.</summary>
public interface IGoldpathApprovalStore
{
    /// <summary>Adds a new request.</summary>
    Task AddAsync(GoldpathApprovalRequest request, CancellationToken cancellationToken = default);

    /// <summary>Loads one request, or null.</summary>
    Task<GoldpathApprovalRequest?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Persists a mutated request.</summary>
    Task UpdateAsync(GoldpathApprovalRequest request, CancellationToken cancellationToken = default);

    /// <summary>All PENDING requests (the worklist and the escalation sweep read this).</summary>
    Task<IReadOnlyList<GoldpathApprovalRequest>> GetPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>The most recent requests in ANY state, newest first (the console's history view).</summary>
    Task<IReadOnlyList<GoldpathApprovalRequest>> GetRecentAsync(int take, CancellationToken cancellationToken = default);

    /// <summary>Adds a delegation.</summary>
    Task AddDelegationAsync(GoldpathApprovalDelegation delegation, CancellationToken cancellationToken = default);

    /// <summary>Active delegations (unexpired).</summary>
    Task<IReadOnlyList<GoldpathApprovalDelegation>> GetDelegationsAsync(DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Adds one collected grant toward a rung's quorum.</summary>
    Task AddSignatureAsync(GoldpathApprovalSignature signature, CancellationToken cancellationToken = default);

    /// <summary>All signatures collected on one request, oldest first.</summary>
    Task<IReadOnlyList<GoldpathApprovalSignature>> GetSignaturesAsync(Guid requestId, CancellationToken cancellationToken = default);
}

/// <summary>The outcome of a decision attempt — refusals are values, not exceptions.</summary>
public enum GoldpathApprovalDecisionOutcome
{
    /// <summary>The decision was applied.</summary>
    Applied,

    /// <summary>Unknown request id.</summary>
    NotFound,

    /// <summary>The request is not pending.</summary>
    NotPending,

    /// <summary>Four-eyes: the requester may never decide their own request.</summary>
    FourEyesViolation,

    /// <summary>The decider does not hold the pending rung's role (nor a valid delegation).</summary>
    WrongRole,

    /// <summary>Distinct-eyes: this identity already signed this request — one signature per person, across rungs.</summary>
    AlreadySigned,

    /// <summary>Rejection without a reason teaches the requester nothing; the reason is mandatory.</summary>
    ReasonRequired,

    /// <summary>Only the requester may withdraw their own pending request.</summary>
    NotRequester,
}

/// <summary>
/// A store write lost an optimistic-concurrency race (#480): the request changed after it was read, or the same
/// identity's signature landed first. The engine answers it by reading again; a caller of the store sees an
/// <see cref="InvalidOperationException"/>.
/// </summary>
internal sealed class GoldpathApprovalConflictException(Guid requestId, Exception? inner = null)
    : InvalidOperationException($"Approval request '{requestId}' changed after it was read; read it again and decide on what it is now.", inner)
{
    /// <summary>The request whose write lost.</summary>
    public Guid RequestId { get; } = requestId;
}

/// <summary>
/// A second resubmission of one rejected request (#521): the request was already reworked, so the call is refused and
/// names the rework a decider should act on instead. The engine throws it when the rejected request's trail already
/// carries its <c>superseded</c> step; the built-in stores throw it when a second request with the same
/// <see cref="GoldpathApprovalRequest.SupersedesId"/> is added, which is what holds two resubmissions at once. A caller
/// sees an <see cref="InvalidOperationException"/>, as for any other request that may not be resubmitted.
/// </summary>
internal sealed class GoldpathApprovalAlreadyResubmittedException(Guid rejectedId, Guid? reworkId, Exception? inner = null)
    : InvalidOperationException(reworkId is { } id
        ? $"Request '{rejectedId}' was already resubmitted as '{id}' — a rejected request is resubmitted once; decide on the rework instead."
        : $"Request '{rejectedId}' was already resubmitted — a rejected request is resubmitted once; decide on the rework instead.", inner)
{
    /// <summary>The rejected request.</summary>
    public Guid RejectedId { get; } = rejectedId;

    /// <summary>The request that already reworks it, when it is known.</summary>
    public Guid? ReworkId { get; } = reworkId;
}

/// <summary>
/// What the built-in stores add to <see cref="IGoldpathApprovalStore"/>: a write the engine needs as ONE unit, checked
/// against the version the request was read at — a quorum signature and the request's new state (#480), and a rework
/// and the rejected request's <c>superseded</c> step (#521). A store without it keeps the separate calls, so its own
/// guarantees decide what a concurrent or a failed write can leave behind.
/// </summary>
internal interface IGoldpathApprovalAtomicStore
{
    /// <summary>Adds the signature and persists the request together, or neither; a lost race throws
    /// <see cref="GoldpathApprovalConflictException"/>.</summary>
    Task SignAsync(GoldpathApprovalSignature signature, GoldpathApprovalRequest request, CancellationToken cancellationToken);

    /// <summary>Adds <paramref name="rework"/> and persists <paramref name="rejected"/> (carrying its new
    /// <c>superseded</c> step) together, or neither (#521). A rework of the same rejection already stored throws
    /// <see cref="GoldpathApprovalAlreadyResubmittedException"/>; a rejected request changed since it was read throws
    /// <see cref="GoldpathApprovalConflictException"/>.</summary>
    Task ResubmitAsync(GoldpathApprovalRequest rework, GoldpathApprovalRequest rejected, CancellationToken cancellationToken);
}

/// <summary>
/// The approvals engine: routes by amount, enforces four-eyes, applies decisions,
/// honors bounded delegation, and escalates on rung deadlines. Every step lands in the
/// request's trail — the audit value the e-mail thread never had.
/// </summary>
public sealed class GoldpathApprovalEngine
{
    private readonly GoldpathApprovalsOptions _options;
    private readonly IGoldpathApprovalStore _store;
    private readonly TimeProvider _time;
    private readonly IIntegrationEventPublisher? _publisher;
    private readonly ILogger<GoldpathApprovalEngine> _logger;

    /// <summary>Creates the engine (the publisher is optional — no broker, no events).</summary>
    public GoldpathApprovalEngine(
        GoldpathApprovalsOptions options,
        IGoldpathApprovalStore store,
        TimeProvider time,
        ILogger<GoldpathApprovalEngine> logger,
        IIntegrationEventPublisher? publisher = null)
    {
        _options = options;
        _store = store;
        _time = time;
        _logger = logger;
        _publisher = publisher;
    }

    /// <summary>Requests an approval; the amount routes the rung.</summary>
    public async Task<GoldpathApprovalRequest> RequestAsync(string ladderName, string subject, decimal amount, string requestedBy, CancellationToken cancellationToken = default)
    {
        if (!_options.Ladders.TryGetValue(ladderName, out var ladder))
        {
            throw new InvalidOperationException($"Ladder '{ladderName}' is not declared — approvals run on declared ladders only.");
        }

        var now = _time.GetUtcNow();
        var rung = ladder.Route(amount);
        var request = new GoldpathApprovalRequest
        {
            Id = Guid.NewGuid(),
            Ladder = ladder.Name,
            Subject = subject,
            Amount = amount,
            RequestedBy = requestedBy,
            RequestedAt = now,
            PendingRole = rung.Role,
            PendingSince = now,
            Status = GoldpathApprovalStatus.Pending,
        };
        request.Trail.Add(new GoldpathApprovalTrailEntry(now, requestedBy, "requested", $"routed to {rung.Role} for {amount}"));
        await _store.AddAsync(request, cancellationToken);
        GoldpathApprovalsMetrics.CountRequested(ladder.Name);
        await PublishAsync(new GoldpathApprovalRequested(request.Id, ladder.Name, subject, amount, rung.Role), cancellationToken);
        return request;
    }

    /// <summary>
    /// Applies a grant/reject decision under four-eyes, role and distinct-eyes checks. A
    /// grant on a quorum rung collects a SIGNATURE; the request completes when the rung's
    /// quorum is met. A rejection is terminal at any point and its reason is mandatory.
    /// </summary>
    /// <remarks>
    /// A decision that loses a write race to another one (#480) reads the request again and decides on what it is now:
    /// the same identity a second time is <see cref="GoldpathApprovalDecisionOutcome.AlreadySigned"/>, a decision on a
    /// request another one closed is <see cref="GoldpathApprovalDecisionOutcome.NotPending"/>, and a distinct signer
    /// signs on top of the winner. That holds on the built-in stores; a custom store's own guarantees decide it there.
    /// <para>Take <c>deciderRole</c> from the authenticated decider's claims, never from a request body — the engine
    /// cannot tell who holds a role (#478). A blank role is no role: only a delegation held for <c>decidedBy</c> lets it
    /// decide.</para>
    /// </remarks>
    public Task<GoldpathApprovalDecisionOutcome> DecideAsync(Guid id, string decidedBy, string deciderRole, bool granted, string reason, CancellationToken cancellationToken = default)
        => OnFreshReadAsync(id, () => DecideOnceAsync(id, decidedBy, deciderRole, assertedRole: null, granted, reason, cancellationToken));

    /// <summary>
    /// The admin surface's decision (#478): <paramref name="assertedRole"/> is the caller's word and
    /// <paramref name="principalHoldsAsserted"/> whether the authenticated principal holds it. One rule on every path:
    /// the assertion must name the rung pending AT THIS DECISION — judged on the same read the decision is made on, and
    /// again on every fresh read after a lost write race — or the answer is <see cref="GoldpathApprovalDecisionOutcome.WrongRole"/>,
    /// whether the principal holds that role, holds another one, or decides by a delegation.
    /// </summary>
    internal Task<GoldpathApprovalDecisionOutcome> DecideAssertedAsync(Guid id, string decidedBy, string assertedRole, bool principalHoldsAsserted,
        bool granted, string reason, CancellationToken cancellationToken)
        => OnFreshReadAsync(id, () => DecideOnceAsync(id, decidedBy, principalHoldsAsserted ? assertedRole : "", assertedRole,
            granted, reason, cancellationToken));

    private async Task<GoldpathApprovalDecisionOutcome> DecideOnceAsync(Guid id, string decidedBy, string deciderRole, string? assertedRole, bool granted,
        string reason, CancellationToken cancellationToken)
    {
        var request = await _store.GetAsync(id, cancellationToken);
        if (request is null)
        {
            return GoldpathApprovalDecisionOutcome.NotFound;
        }

        if (request.Status != GoldpathApprovalStatus.Pending)
        {
            return GoldpathApprovalDecisionOutcome.NotPending;
        }

        if (string.Equals(request.RequestedBy, decidedBy, StringComparison.OrdinalIgnoreCase))
        {
            return GoldpathApprovalDecisionOutcome.FourEyesViolation;
        }

        // An asserted role binds the decision to the rung it names (#478). It is compared with THIS read's pending rung, so
        // neither a held other role nor a delegation, nor an escalation since the caller looked, lets it decide another rung.
        if (assertedRole is not null
            && (string.IsNullOrWhiteSpace(assertedRole) || !string.Equals(request.PendingRole, assertedRole, StringComparison.OrdinalIgnoreCase)))
        {
            return GoldpathApprovalDecisionOutcome.WrongRole;
        }

        var now = _time.GetUtcNow();
        // A blank role is no role (#478): it never matches a rung, not even a rung a ladder declared blank, so the only way
        // a decider naming none may decide is a delegation the engine holds for its identity.
        if ((string.IsNullOrWhiteSpace(deciderRole) || !string.Equals(request.PendingRole, deciderRole, StringComparison.OrdinalIgnoreCase))
            && !await HasDelegationToRoleAsync(decidedBy, request.PendingRole, now, cancellationToken))
        {
            return GoldpathApprovalDecisionOutcome.WrongRole;
        }

        if (!granted)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return GoldpathApprovalDecisionOutcome.ReasonRequired;
            }

            request.Status = GoldpathApprovalStatus.Rejected;
            request.DecidedBy = decidedBy;
            request.Reason = reason;
            request.Trail.Add(new GoldpathApprovalTrailEntry(now, decidedBy, "rejected", reason));
            await _store.UpdateAsync(request, cancellationToken);
            GoldpathApprovalsMetrics.CountRejected(request.Ladder);
            await PublishAsync(new GoldpathApprovalRejected(request.Id, request.Ladder, request.Subject, decidedBy, reason), cancellationToken);
            return GoldpathApprovalDecisionOutcome.Applied;
        }

        // Distinct-eyes reads the WHOLE chain: an identity that signed a lower rung may not
        // sign again after escalation, even holding the higher role — otherwise a two-step
        // chain is one person clicking twice.
        var signatures = await _store.GetSignaturesAsync(request.Id, cancellationToken);
        if (signatures.Any(s => string.Equals(s.SignedBy, decidedBy, StringComparison.OrdinalIgnoreCase)))
        {
            return GoldpathApprovalDecisionOutcome.AlreadySigned;
        }

        var signature = new GoldpathApprovalSignature(request.Id, decidedBy, request.PendingRole, now);
        // Quorum counts per RUNG (signatures carry the rung they covered); an escalated
        // request starts its new rung's count from zero while the chain-wide list above
        // keeps every earlier signer barred.
        var collected = signatures.Count(s => string.Equals(s.Role, request.PendingRole, StringComparison.OrdinalIgnoreCase)) + 1;
        var required = RequiredApprovalsFor(request);
        if (collected < required)
        {
            request.Trail.Add(new GoldpathApprovalTrailEntry(now, decidedBy, "signed", $"{collected}/{required} at {request.PendingRole}: {reason}"));
            await SignAsync(signature, request, cancellationToken);
            return GoldpathApprovalDecisionOutcome.Applied;
        }

        request.Status = GoldpathApprovalStatus.Granted;
        request.DecidedBy = decidedBy;
        request.Reason = reason;
        request.Trail.Add(new GoldpathApprovalTrailEntry(now, decidedBy, "granted", reason));
        await SignAsync(signature, request, cancellationToken);
        GoldpathApprovalsMetrics.CountGranted(request.Ladder);
        await PublishAsync(new GoldpathApprovalGranted(request.Id, request.Ladder, request.Subject, decidedBy), cancellationToken);
        return GoldpathApprovalDecisionOutcome.Applied;
    }

    /// <summary>Takes back a PENDING request — only its requester may; the step lands in the trail.</summary>
    public Task<GoldpathApprovalDecisionOutcome> WithdrawAsync(Guid id, string requestedBy, CancellationToken cancellationToken = default)
        => OnFreshReadAsync(id, () => WithdrawOnceAsync(id, requestedBy, cancellationToken));

    private async Task<GoldpathApprovalDecisionOutcome> WithdrawOnceAsync(Guid id, string requestedBy, CancellationToken cancellationToken)
    {
        var request = await _store.GetAsync(id, cancellationToken);
        if (request is null)
        {
            return GoldpathApprovalDecisionOutcome.NotFound;
        }

        if (request.Status != GoldpathApprovalStatus.Pending)
        {
            return GoldpathApprovalDecisionOutcome.NotPending;
        }

        if (!string.Equals(request.RequestedBy, requestedBy, StringComparison.OrdinalIgnoreCase))
        {
            return GoldpathApprovalDecisionOutcome.NotRequester;
        }

        var now = _time.GetUtcNow();
        request.Status = GoldpathApprovalStatus.Withdrawn;
        request.Trail.Add(new GoldpathApprovalTrailEntry(now, requestedBy, "withdrawn", "taken back by the requester"));
        await _store.UpdateAsync(request, cancellationToken);
        GoldpathApprovalsMetrics.CountWithdrawn(request.Ladder);
        return GoldpathApprovalDecisionOutcome.Applied;
    }

    /// <summary>
    /// Resubmits a REJECTED request as a fresh one: re-routed by the same amount, linked by
    /// <see cref="GoldpathApprovalRequest.SupersedesId"/>, both trails cross-referenced — the
    /// audit reads request → reject → rework as one story. Anything but a rejected request
    /// throws: resubmission is rework, not a retry channel. A rejected request is resubmitted ONCE (#521): a second call
    /// throws and names the rework. On the built-in stores two calls at once create one rework between them, and the
    /// rework and the rejected request's <c>superseded</c> step are written together or not at all, so a call that
    /// failed leaves nothing behind and its retry starts clean.
    /// </summary>
    public async Task<GoldpathApprovalRequest> ResubmitAsync(Guid rejectedId, string requestedBy, CancellationToken cancellationToken = default)
    {
        var rejected = await _store.GetAsync(rejectedId, cancellationToken);
        if (rejected is null)
        {
            throw new InvalidOperationException($"Request '{rejectedId}' does not exist — only a rejected request may be resubmitted.");
        }

        if (rejected.Status != GoldpathApprovalStatus.Rejected)
        {
            throw new InvalidOperationException($"Request '{rejectedId}' is {rejected.Status} — only a rejected request may be resubmitted.");
        }

        // Once (#521). The trail answers a second call on any store; two calls at once both pass here, and the built-in
        // stores refuse the second rework's add.
        if (rejected.Trail.FirstOrDefault(t => t.Action == SupersededStep) is { } supersededStep)
        {
            throw new GoldpathApprovalAlreadyResubmittedException(rejected.Id, ReworkNamedBy(supersededStep));
        }

        if (!_options.Ladders.TryGetValue(rejected.Ladder, out var ladder))
        {
            throw new InvalidOperationException($"Ladder '{rejected.Ladder}' is not declared — approvals run on declared ladders only.");
        }

        var now = _time.GetUtcNow();
        var rung = ladder.Route(rejected.Amount);
        var request = new GoldpathApprovalRequest
        {
            Id = Guid.NewGuid(),
            Ladder = ladder.Name,
            Subject = rejected.Subject,
            Amount = rejected.Amount,
            RequestedBy = requestedBy,
            RequestedAt = now,
            PendingRole = rung.Role,
            PendingSince = now,
            Status = GoldpathApprovalStatus.Pending,
            SupersedesId = rejected.Id,
        };
        request.Trail.Add(new GoldpathApprovalTrailEntry(now, requestedBy, "resubmitted", $"supersedes {rejected.Id}; routed to {rung.Role} for {rejected.Amount}"));
        var superseded = new GoldpathApprovalTrailEntry(now, requestedBy, SupersededStep, $"{ResubmittedAs}{request.Id}");
        if (_store is IGoldpathApprovalAtomicStore atomic)
        {
            await ResubmitAtomicallyAsync(atomic, request, rejected, superseded, cancellationToken);
        }
        else
        {
            await ResubmitInTwoWritesAsync(request, rejected, superseded, cancellationToken);
        }

        await PublishAsync(new GoldpathApprovalRequested(request.Id, ladder.Name, request.Subject, request.Amount, rung.Role), cancellationToken);
        return request;
    }

    /// <summary>
    /// The built-in stores' resubmission (#521): the rework and the rejected request's <c>superseded</c> step in one
    /// write, so a failure between them cannot leave a rework that the rejected request does not name and that no
    /// retry could ever announce. A write that loses to another step on the rejected request reads it again, and a
    /// <c>superseded</c> step found then refuses this call as the first read would have.
    /// </summary>
    private async Task ResubmitAtomicallyAsync(IGoldpathApprovalAtomicStore atomic, GoldpathApprovalRequest request,
        GoldpathApprovalRequest rejected, GoldpathApprovalTrailEntry superseded, CancellationToken cancellationToken)
    {
        GoldpathApprovalRequest? current = rejected;
        await OnFreshReadAsync(rejected.Id, async () =>
        {
            if (current is null)
            {
                current = await _store.GetAsync(rejected.Id, cancellationToken)
                    ?? throw new InvalidOperationException($"Request '{rejected.Id}' disappeared while it was being superseded.");
                if (current.Trail.FirstOrDefault(t => t.Action == SupersededStep) is { } step)
                {
                    throw new GoldpathApprovalAlreadyResubmittedException(current.Id, ReworkNamedBy(step));
                }
            }

            var writing = current;
            current = null;   // a retry re-reads
            writing.Trail.Add(superseded);
            await atomic.ResubmitAsync(request, writing, cancellationToken);
            return true;
        });
    }

    /// <summary>
    /// A custom store's resubmission: the rework, then the rejected request's step, as two writes. The rejected request
    /// is terminal, so only another trail step can race the second (#480), and a lost write puts the step onto the
    /// request as it is now. A failure between the two is the store's to prevent; the built-in stores take
    /// <see cref="ResubmitAtomicallyAsync"/>.
    /// </summary>
    private async Task ResubmitInTwoWritesAsync(GoldpathApprovalRequest request, GoldpathApprovalRequest rejected,
        GoldpathApprovalTrailEntry superseded, CancellationToken cancellationToken)
    {
        await _store.AddAsync(request, cancellationToken);
        rejected.Trail.Add(superseded);
        GoldpathApprovalRequest? current = rejected;
        await OnFreshReadAsync(rejected.Id, async () =>
        {
            if (current is null)
            {
                current = await _store.GetAsync(rejected.Id, cancellationToken)
                    ?? throw new InvalidOperationException($"Request '{rejected.Id}' disappeared while it was being superseded.");
                current.Trail.Add(superseded);
            }

            var writing = current;
            current = null;   // a retry re-reads
            await _store.UpdateAsync(writing, cancellationToken);
            return true;
        });
    }

    /// <summary>The step a resubmission puts on the rejected request's trail, and the start of its detail.</summary>
    private const string SupersededStep = "superseded";

    private const string ResubmittedAs = "resubmitted as ";

    /// <summary>The rework a <c>superseded</c> step names, or null when its detail is not the engine's own.</summary>
    private static Guid? ReworkNamedBy(GoldpathApprovalTrailEntry step)
        => step.Detail is { } detail && detail.StartsWith(ResubmittedAs, StringComparison.Ordinal)
            && Guid.TryParse(detail.AsSpan(ResubmittedAs.Length), out var id) ? id : null;

    private int RequiredApprovalsFor(GoldpathApprovalRequest request)
    {
        if (!_options.Ladders.TryGetValue(request.Ladder, out var ladder))
        {
            return 1;
        }

        var rung = ladder.Rungs.FirstOrDefault(r => string.Equals(r.Role, request.PendingRole, StringComparison.OrdinalIgnoreCase));
        return rung?.RequiredApprovals ?? 1;
    }

    /// <summary>
    /// Refused since 0.1.0-preview.10 (#479): a delegation that names no role would admit its delegate to every rung
    /// of every ladder. Call <see cref="DelegateRoleAsync"/> with the delegator's role.
    /// </summary>
    /// <exception cref="InvalidOperationException">Always — the returned task faults with the teaching text.</exception>
    public Task DelegateAsync(string from, string to, TimeSpan window, CancellationToken cancellationToken = default)
        => Task.FromException(new InvalidOperationException(RoleRequired));

    // The refusal of the role-less overload, in words an adopter can act on.
    private const string RoleRequired =
        "A delegation names the role it delegates (#479): call DelegateRoleAsync(from, fromRole, to, window) with the delegator's role "
        + "taken from the authenticated delegator's claims, never from a request body. A delegation without a role would admit "
        + "its delegate to every rung of every ladder.";

    /// <summary>
    /// Delegates <paramref name="from"/>'s pending decisions on the <paramref name="fromRole"/> rung to
    /// <paramref name="to"/> for a bounded window (#479): the delegate may decide what the delegator may decide, on
    /// every ladder that has that rung, and nothing above or below it. Take <paramref name="fromRole"/> from the
    /// authenticated delegator's claims, never from a request body — the engine cannot tell who holds a role. Depth
    /// is one: a delegate cannot re-delegate — the cycle guard is structural, not a graph search.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="fromRole"/> is empty, or no rung of any declared ladder.</exception>
    /// <exception cref="InvalidOperationException">Self-delegation, a window above the declared maximum, or a delegator who holds a delegation.</exception>
    public async Task DelegateRoleAsync(string from, string fromRole, string to, TimeSpan window, CancellationToken cancellationToken = default)
    {
        // Recorded in the ladder's own spelling, so the delegation reads as the rung it admits to. A blank role matches
        // no rung, so it is refused here too.
        var role = _options.Ladders.Values.SelectMany(l => l.Rungs)
            .FirstOrDefault(r => string.Equals(r.Role, fromRole, StringComparison.OrdinalIgnoreCase))?.Role
            ?? throw new ArgumentException($"'{fromRole}' is no rung of any declared ladder, so a delegation of it would admit to nothing.", nameof(fromRole));

        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Delegation to self is meaningless.");
        }

        if (window > _options.MaxDelegationWindow)
        {
            throw new InvalidOperationException($"Delegation window exceeds the declared maximum ({_options.MaxDelegationWindow}).");
        }

        var now = _time.GetUtcNow();
        var active = await _store.GetDelegationsAsync(now, cancellationToken);
        if (active.Any(d => string.Equals(d.To, from, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"'{from}' currently holds a delegation and cannot re-delegate (depth is one).");
        }

        await _store.AddDelegationAsync(new GoldpathApprovalDelegation(from, to, now + window) { Role = role }, cancellationToken);
    }

    /// <summary>
    /// The escalation sweep: pending requests past their rung's deadline move UP one rung;
    /// overdue at the top rung EXPIRES. Schedule this through the Jobs module.
    /// </summary>
    public async Task<int> EscalateOverdueAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        var moved = 0;
        foreach (var request in await _store.GetPendingAsync(cancellationToken))
        {
            if (!_options.Ladders.TryGetValue(request.Ladder, out var ladder))
            {
                continue;
            }

            var rung = ladder.Rungs.FirstOrDefault(r => string.Equals(r.Role, request.PendingRole, StringComparison.OrdinalIgnoreCase));
            if (rung is null || now - request.PendingSince < rung.EscalateAfter)
            {
                continue;
            }

            var above = ladder.Above(rung);
            if (above is null)
            {
                request.Status = GoldpathApprovalStatus.Expired;
                request.Trail.Add(new GoldpathApprovalTrailEntry(now, "system", "expired", $"overdue at top rung {rung.Role}"));
                if (!await SweptAsync(request, cancellationToken))
                {
                    continue;
                }

                GoldpathApprovalsMetrics.CountExpired(request.Ladder);
                await PublishAsync(new GoldpathApprovalExpired(request.Id, request.Ladder, request.Subject, rung.Role), cancellationToken);
            }
            else
            {
                request.PendingRole = above.Role;
                request.PendingSince = now;
                request.Trail.Add(new GoldpathApprovalTrailEntry(now, "system", "escalated", $"{rung.Role} -> {above.Role}"));
                if (!await SweptAsync(request, cancellationToken))
                {
                    continue;
                }

                GoldpathApprovalsMetrics.CountEscalated(request.Ladder);
                await PublishAsync(new GoldpathApprovalEscalated(request.Id, request.Ladder, request.Subject, rung.Role, above.Role), cancellationToken);
            }

            moved++;
        }

        if (moved > 0)
        {
            _logger.LogInformation("Approvals escalation sweep moved {Count} request(s).", moved);
        }

        return moved;
    }

    /// <summary>
    /// The worklist: pending requests the given identity (holding the given role) may decide —
    /// their role's rung plus the rungs delegated to them (#479: a delegation's own rung, no other).
    /// Four-eyes filters their own requests out.
    /// </summary>
    public async Task<IReadOnlyList<GoldpathApprovalRequest>> WorklistAsync(string identity, string role, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        var delegations = await _store.GetDelegationsAsync(now, cancellationToken);
        var pending = await _store.GetPendingAsync(cancellationToken);
        return pending
            .Where(r => !string.Equals(r.RequestedBy, identity, StringComparison.OrdinalIgnoreCase))
            .Where(r => string.Equals(r.PendingRole, role, StringComparison.OrdinalIgnoreCase)
                || delegations.Any(d => Admits(d, identity, r.PendingRole)))
            .OrderBy(r => r.PendingSince)
            .ToList();
    }

    private async Task<bool> HasDelegationToRoleAsync(string identity, string pendingRole, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // A delegation transfers the DELEGATOR's decision right, and only that (#479): the rung of the role it was
        // granted with. A row with no role (written before preview.10) admits to nothing.
        var delegations = await _store.GetDelegationsAsync(now, cancellationToken);
        return delegations.Any(d => Admits(d, identity, pendingRole));
    }

    private static bool Admits(GoldpathApprovalDelegation delegation, string identity, string pendingRole)
        => string.Equals(delegation.To, identity, StringComparison.OrdinalIgnoreCase)
            && delegation.Role is { } role
            && string.Equals(role, pendingRole, StringComparison.OrdinalIgnoreCase);

    /// <summary>How many times one call reads again after losing a write race before it gives up (#480).</summary>
    private const int ConflictAttempts = 8;

    /// <summary>
    /// Runs <paramref name="attempt"/> — which reads the request itself — and runs it again on a fresh read when its
    /// write loses a race. Every lost race means another write landed, so the retries make progress; the bound only
    /// stops a call that keeps losing.
    /// </summary>
    private async Task<T> OnFreshReadAsync<T>(Guid id, Func<Task<T>> attempt)
    {
        for (var tries = 1; ; tries++)
        {
            try
            {
                return await attempt();
            }
            catch (GoldpathApprovalConflictException) when (tries < ConflictAttempts)
            {
                _logger.LogDebug("Approval request {RequestId} changed while it was being decided; reading it again (attempt {Attempt}).", id, tries + 1);
            }
        }
    }

    /// <summary>A quorum signature and the request's new state: one write on a built-in store, two on any other.</summary>
    private async Task SignAsync(GoldpathApprovalSignature signature, GoldpathApprovalRequest request, CancellationToken cancellationToken)
    {
        if (_store is IGoldpathApprovalAtomicStore atomic)
        {
            await atomic.SignAsync(signature, request, cancellationToken);
            return;
        }

        await _store.AddSignatureAsync(signature, cancellationToken);
        await _store.UpdateAsync(request, cancellationToken);
    }

    /// <summary>The sweep's write: a request decided while the sweep looked at it is left to the decision, and the
    /// next sweep reads it again (#480).</summary>
    private async Task<bool> SweptAsync(GoldpathApprovalRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _store.UpdateAsync(request, cancellationToken);
            return true;
        }
        catch (GoldpathApprovalConflictException)
        {
            _logger.LogDebug("Approval request {RequestId} changed during the escalation sweep; left for the next sweep.", request.Id);
            return false;
        }
    }

    private Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken)
        where TEvent : class, IIntegrationEvent
        => _publisher?.PublishAsync(integrationEvent, cancellationToken) ?? Task.CompletedTask;
}

/// <summary>
/// In-memory store: tests and single-node hosts; database stores compose via the seam. It keeps the database store's
/// two concurrency rules (#480): every read hands out a copy, a write made from a stale copy is refused, and one
/// identity signs a request once.
/// </summary>
public sealed class GoldpathInMemoryApprovalStore : IGoldpathApprovalStore, IGoldpathApprovalAtomicStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, GoldpathApprovalRequest> _requests = [];
    private readonly List<GoldpathApprovalDelegation> _delegations = [];
    private readonly List<GoldpathApprovalSignature> _signatures = [];

    /// <inheritdoc />
    /// <remarks>A second request with the same non-null <see cref="GoldpathApprovalRequest.SupersedesId"/> is refused
    /// with an <see cref="InvalidOperationException"/>, as the database store's unique index refuses it (#521).</remarks>
    public Task AddAsync(GoldpathApprovalRequest request, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            RefuseSecondRework(request);
            _requests.Add(request.Id, request.Snapshot());
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<GoldpathApprovalRequest?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_requests.TryGetValue(id, out var request) ? request.Snapshot() : null);
        }
    }

    /// <inheritdoc />
    /// <remarks>Refused with an <see cref="InvalidOperationException"/> when the request changed since this copy
    /// was read (#480).</remarks>
    public Task UpdateAsync(GoldpathApprovalRequest request, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Write(request);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GoldpathApprovalRequest>> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<GoldpathApprovalRequest> pending =
                _requests.Values.Where(r => r.Status == GoldpathApprovalStatus.Pending).Select(r => r.Snapshot()).ToList();
            return Task.FromResult(pending);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GoldpathApprovalRequest>> GetRecentAsync(int take, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<GoldpathApprovalRequest> recent =
                _requests.Values.OrderByDescending(r => r.RequestedAt).Take(take).Select(r => r.Snapshot()).ToList();
            return Task.FromResult(recent);
        }
    }

    /// <inheritdoc />
    public Task AddDelegationAsync(GoldpathApprovalDelegation delegation, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _delegations.Add(delegation);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GoldpathApprovalDelegation>> GetDelegationsAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<GoldpathApprovalDelegation> active = _delegations.Where(d => d.Until > now).ToList();
            return Task.FromResult(active);
        }
    }

    /// <inheritdoc />
    /// <remarks>A second signature by the same <c>SignedBy</c> on the same request is refused with an
    /// <see cref="InvalidOperationException"/>, as the database store's unique key refuses it (#480).</remarks>
    public Task AddSignatureAsync(GoldpathApprovalSignature signature, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            RefuseSecondSignature(signature);
            _signatures.Add(signature);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GoldpathApprovalSignature>> GetSignaturesAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<GoldpathApprovalSignature> signatures =
                _signatures.Where(s => s.RequestId == requestId).OrderBy(s => s.At).ToList();
            return Task.FromResult(signatures);
        }
    }

    Task IGoldpathApprovalAtomicStore.SignAsync(GoldpathApprovalSignature signature, GoldpathApprovalRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            // Both checks before either change: the pair lands together or not at all.
            RefuseSecondSignature(signature);
            RefuseStale(request);
            _signatures.Add(signature);
            Write(request);
        }

        return Task.CompletedTask;
    }

    Task IGoldpathApprovalAtomicStore.ResubmitAsync(GoldpathApprovalRequest rework, GoldpathApprovalRequest rejected, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            // Both checks before either change: the pair lands together or not at all (#521).
            RefuseSecondRework(rework);
            RefuseStale(rejected);
            _requests.Add(rework.Id, rework.Snapshot());
            Write(rejected);
        }

        return Task.CompletedTask;
    }

    // The index the database store declares unique: a non-null SupersedesId (#521).
    private void RefuseSecondRework(GoldpathApprovalRequest request)
    {
        if (request.SupersedesId is { } supersedes
            && _requests.Values.FirstOrDefault(r => r.SupersedesId == supersedes) is { } rework)
        {
            throw new GoldpathApprovalAlreadyResubmittedException(supersedes, rework.Id);
        }
    }

    // The key the database store declares unique: (RequestId, SignedBy), compared exactly. The engine's distinct-eyes
    // check above it ignores case.
    private void RefuseSecondSignature(GoldpathApprovalSignature signature)
    {
        if (_signatures.Any(s => s.RequestId == signature.RequestId && string.Equals(s.SignedBy, signature.SignedBy, StringComparison.Ordinal)))
        {
            throw new GoldpathApprovalConflictException(signature.RequestId);
        }
    }

    private void RefuseStale(GoldpathApprovalRequest request)
    {
        if (_requests.TryGetValue(request.Id, out var stored) && stored.Version != request.Version)
        {
            throw new GoldpathApprovalConflictException(request.Id);
        }
    }

    private void Write(GoldpathApprovalRequest request)
    {
        RefuseStale(request);
        request.Version++;
        _requests[request.Id] = request.Snapshot();
    }
}
