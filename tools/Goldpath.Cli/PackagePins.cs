using System.Text.RegularExpressions;

namespace Goldpath.Cli;

/// <summary>
/// Reads central pins (<c>Directory.Packages.props</c>) the recipes need to reproduce: a
/// recipe that brings a package the template pins only under a symbol the app was generated
/// WITHOUT (the broker set) must pin it on the app's own train and Aspire line — a
/// PackageReference without its PackageVersion is NU1010 at restore, not a warning. Found
/// by the GmGrown nightly shape (2026-09-04): `--broker none` + `add feature outbox`.
/// </summary>
public static class PackagePins
{
    /// <summary>The version pinned for <paramref name="package"/>, or null when there is no such pin.</summary>
    public static string? Read(string props, string package)
    {
        var pattern = "<PackageVersion\\s+Include=\"" + Regex.Escape(package) + "\"\\s+Version=\"([^\"]+)\"";
        var match = Regex.Match(props, pattern);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Adds the pins that are not there yet, before the last <c>&lt;/ItemGroup&gt;</c>; a pin
    /// that exists (any version) is left alone — the app's train is the app's decision.
    /// </summary>
    public static string AddMissing(string props, IReadOnlyList<(string Package, string Version)> pins)
    {
        var missing = pins
            .Where(pin => !props.Contains("<PackageVersion Include=\"" + pin.Package + "\"", StringComparison.Ordinal))
            .Select(pin => "    <PackageVersion Include=\"" + pin.Package + "\" Version=\"" + pin.Version + "\" />")
            .ToList();
        if (missing.Count == 0)
        {
            return props;
        }

        var closing = props.LastIndexOf("  </ItemGroup>", StringComparison.Ordinal);
        if (closing < 0)
        {
            throw new CliFailureException("Directory.Packages.props has no </ItemGroup> to pin into — add the pins by hand: " + string.Join(", ", missing));
        }

        return props[..closing] + string.Join('\n', missing) + "\n" + props[closing..];
    }
}

/// <summary>
/// Versions the CLI must know by heart: packages the template pins only under a symbol the
/// app was generated without, so no central pin exists to read. Kept in sync with the
/// repo's Directory.Packages.props by a test — a dependency bump that forgets this line
/// fails that test, deliberately.
/// </summary>
public static class KnownVersions
{
    /// <summary>The RabbitMQ transport of the 8.x OSS line (messaging-exit RFC option A).</summary>
    public const string MassTransitRabbitMq = "8.5.12";
}
