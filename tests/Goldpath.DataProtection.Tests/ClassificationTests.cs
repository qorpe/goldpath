using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Goldpath.Tests;

public sealed class ClassificationTests
{
    private sealed class Customer
    {
        public string? Name { get; set; }

        [GoldpathPersonalData]
        public string? NationalId { get; set; }

        [GoldpathSensitiveData]
        public string? Salary { get; set; }

        [Mediant.Attributes.SensitiveData]
        public string? CardNumber { get; set; }
    }

    private sealed class LegacyCustomer
    {
        public string? TaxNumber { get; set; }

        [GoldpathSensitiveData]
        public string? Iban { get; set; }
    }

    private static GoldpathDataProtector Build(Action<GoldpathDataProtectionOptions>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddGoldpathDataProtection(configure);
        using var host = builder.Build();
        return Assert.IsType<GoldpathDataProtector>(host.Services.GetRequiredService<IGoldpathDataProtector>());
    }

    [Fact]
    public void Annotations_classify_and_plain_members_stay_unclassified()
    {
        var protector = Build();

        Assert.Equal(GoldpathDataClass.Personal, protector.Classify(typeof(Customer), nameof(Customer.NationalId)));
        Assert.Equal(GoldpathDataClass.Sensitive, protector.Classify(typeof(Customer), nameof(Customer.Salary)));
        Assert.False(protector.IsClassified(typeof(Customer), nameof(Customer.Name)));
    }

    [Fact]
    public void Mediant_sensitive_data_attribute_is_recognized_without_a_mediant_dependency()
    {
        var protector = Build();

        Assert.Equal(GoldpathDataClass.Sensitive, protector.Classify(typeof(Customer), nameof(Customer.CardNumber)));
    }

    [Fact]
    public void Catalog_classifies_untouchable_members_and_wins_ties_with_annotations()
    {
        var protector = Build(o => o.Catalog(c => c
            .Classify<LegacyCustomer>(x => x.TaxNumber, GoldpathDataClass.Personal)
            .Classify<LegacyCustomer>(x => x.Iban, GoldpathDataClass.Personal)));   // annotation says Sensitive

        Assert.Equal(GoldpathDataClass.Personal, protector.Classify(typeof(LegacyCustomer), nameof(LegacyCustomer.TaxNumber)));
        Assert.Equal(GoldpathDataClass.Personal, protector.Classify(typeof(LegacyCustomer), nameof(LegacyCustomer.Iban)));
    }

    [Fact]
    public void Erase_mode_redacts_to_the_fixed_token_and_passes_unclassified_through()
    {
        var protector = Build();

        Assert.Equal(GoldpathErasingRedactor.Token, protector.Redact(typeof(Customer), nameof(Customer.NationalId), "12345678901"));
        Assert.Equal("Ada", protector.Redact(typeof(Customer), nameof(Customer.Name), "Ada"));
        Assert.Null(protector.Redact(typeof(Customer), nameof(Customer.NationalId), null));
    }

    [Fact]
    public void Hmac_mode_is_deterministic_per_key_and_differs_across_keys()
    {
        var keyA = Convert.ToBase64String(Enumerable.Repeat((byte)7, 64).ToArray());
        var keyB = Convert.ToBase64String(Enumerable.Repeat((byte)9, 64).ToArray());
        var protectorA1 = Build(o => o.UseHmacRedaction(keyA));
        var protectorA2 = Build(o => o.UseHmacRedaction(keyA));
        var protectorB = Build(o => o.UseHmacRedaction(keyB, keyId: 2));

        var value = "12345678901";
        var hashedA1 = protectorA1.Redact(typeof(Customer), nameof(Customer.NationalId), value);
        var hashedA2 = protectorA2.Redact(typeof(Customer), nameof(Customer.NationalId), value);
        var hashedB = protectorB.Redact(typeof(Customer), nameof(Customer.NationalId), value);

        Assert.NotNull(hashedA1);
        Assert.DoesNotContain(value, hashedA1);
        Assert.Equal(hashedA1, hashedA2);        // correlation survives the redaction
        Assert.NotEqual(hashedA1, hashedB);      // …but only under the same key
    }

    [Fact]
    public void Hmac_mode_without_a_key_fails_loudly_at_startup()
    {
        var builder = Host.CreateApplicationBuilder();
        Assert.Throws<InvalidOperationException>(() =>
            builder.AddGoldpathDataProtection(o => o.Redactor = GoldpathRedactionMode.Hmac));
    }

    /// <summary>
    /// Threat model T-DPK-07: an HMAC key too short to resist a guess (under 44 Base64 characters, the
    /// composed redactor's own rule) or not Base64 at all is refused when the redactor is resolved,
    /// before a single value is pseudonymized with it.
    /// </summary>
    [Theory]
    [InlineData("c2hvcnQ=")]                 // "short": 8 characters
    [InlineData("not a base64 key at all, though long enough to pass a length check")]
    public void Hmac_mode_refuses_a_short_or_non_base64_key_before_it_redacts(string key)
        => Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => Build(o => o.UseHmacRedaction(key)));
}
