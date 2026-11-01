using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Goldpath;

/// <summary>
/// The four campaign options an operator turns during an incident (campaign RFC R3.3), as the
/// configuration section <c>Goldpath:Campaign</c> states them. Each member is null when the
/// section leaves its key unset, so the code value stays the default. It is the
/// <c>IOptionsMonitor</c> value <see cref="GoldpathCampaignOptions"/> reads through; a reload
/// produces a new instance, and an instance is never changed after it is read.
/// </summary>
internal sealed class GoldpathCampaignTuning
{
    /// <summary>The configuration section the four keys live in.</summary>
    internal const string SectionName = "Goldpath:Campaign";

    /// <summary>The configured ladder, replacing the code ladder whole; null = unset.</summary>
    public IReadOnlyList<TimeSpan>? RetryBackoff { get; private set; }

    /// <summary>The configured jitter; null = unset.</summary>
    public double? RetryJitter { get; private set; }

    /// <summary>The configured orphan age; null = unset.</summary>
    public TimeSpan? OrphanReleaseAfter { get; private set; }

    /// <summary>The configured stale-claim age; null = unset.</summary>
    public TimeSpan? StaleClaimAfter { get; private set; }

    /// <summary>
    /// The first key whose value cannot be used, and why; null when every configured key is
    /// usable. A reload carrying one is refused whole.
    /// </summary>
    public (string Key, string Reason)? Refusal { get; private set; }

    /// <summary>
    /// Reads the four keys from <paramref name="section"/>. Each key binds on its own into a
    /// fresh value, so a configured ladder replaces the code ladder instead of being appended to
    /// it (the binder appends to a pre-filled collection). A value the binder cannot convert is
    /// recorded as the refusal, never thrown: this runs inside a reload callback.
    /// </summary>
    internal void ReadFrom(IConfiguration section)
    {
        RetryBackoff = Read<List<TimeSpan>>(section, nameof(RetryBackoff), ReadLadder);
        RetryJitter = Read<double?>(section, nameof(RetryJitter), s => s.Get<double>());
        OrphanReleaseAfter = Read<TimeSpan?>(section, nameof(OrphanReleaseAfter), s => s.Get<TimeSpan>());
        StaleClaimAfter = Read<TimeSpan?>(section, nameof(StaleClaimAfter), s => s.Get<TimeSpan>());

        // A key that did not parse is already the refusal (Refuse keeps the first), and it reads
        // as unset below, so the checks that follow cannot replace it.
        if (RetryBackoff is { Count: 0 })
        {
            Refuse(nameof(RetryBackoff), "the ladder needs at least one rung");
        }
        else if (RetryBackoff is not null && RetryBackoff.Any(r => r < TimeSpan.Zero))
        {
            Refuse(nameof(RetryBackoff), "a rung cannot be negative");
        }
        else if (RetryJitter is { } jitter && !(jitter >= 0 && jitter <= 1))
        {
            Refuse(nameof(RetryJitter), string.Create(CultureInfo.InvariantCulture, $"jitter must be within [0, 1], not {jitter}"));
        }
        else if (OrphanReleaseAfter is { } orphan && orphan <= TimeSpan.Zero)
        {
            Refuse(nameof(OrphanReleaseAfter), string.Create(CultureInfo.InvariantCulture, $"the age must be positive, not {orphan}"));
        }
        else if (StaleClaimAfter is { } stale && stale <= TimeSpan.Zero)
        {
            Refuse(nameof(StaleClaimAfter), string.Create(CultureInfo.InvariantCulture, $"the age must be positive, not {stale}"));
        }
    }

    /// <summary>
    /// Binds each rung on its own, in index order. The binder's own list binding skips an element
    /// it cannot convert, so <c>RetryBackoff:1=garbage</c> would quietly shorten the ladder; here it
    /// throws, and the reload is refused.
    /// </summary>
    private static List<TimeSpan> ReadLadder(IConfigurationSection ladder)
    {
        var rungs = new List<(int Index, TimeSpan Rung)>();
        foreach (var rung in ladder.GetChildren())
        {
            if (!int.TryParse(rung.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            {
                throw new InvalidOperationException($"'{rung.Key}' is not a rung index");
            }

            rungs.Add((index, rung.Get<TimeSpan>()));
        }

        return [.. rungs.OrderBy(r => r.Index).Select(r => r.Rung)];
    }

    private T? Read<T>(IConfiguration section, string key, Func<IConfigurationSection, T?> bind)
    {
        var child = section.GetSection(key);
        var hasChildren = child.GetChildren().Any();
        if (child.Value is null && !hasChildren)
        {
            return default;
        }

        // A scalar set to "" is the conventional way to unset an environment variable; it stays
        // unset. A ladder set to "" (how a JSON [] arrives) is an empty ladder, and is refused.
        if (!hasChildren && child.Value is { Length: 0 } && typeof(T) != typeof(List<TimeSpan>))
        {
            return default;
        }

        // A ladder written as one value (Goldpath__Campaign__RetryBackoff=00:01:00) is a list with
        // no items to the binder; say what shape it needs rather than "no rungs".
        if (!hasChildren && child.Value is { Length: > 0 } && typeof(T) == typeof(List<TimeSpan>))
        {
            Refuse(key, $"a ladder is a list: set {key}:0, {key}:1, … rather than one value");
            return default;
        }

        try
        {
            return bind(child);
        }
        catch (InvalidOperationException ex)
        {
            Refuse(key, $"the value cannot be read ({ex.GetBaseException().Message})");
            return default;
        }
    }

    private void Refuse(string key, string reason) => Refusal ??= (key, reason);
}
