using Mediant.Behaviors.DependencyInjection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Mediant.Abstractions;

namespace Goldpath;

/// <summary>
/// Registers Ring B auth: composed ASP.NET authentication (OIDC/JWT bearer or API key),
/// a secure-by-default fallback policy, and Mediant's <c>[Authorize]</c> behavior fed from
/// HTTP claims — one principal, both levels. Fail-closed everywhere (RFC D2).
/// </summary>
public static class GoldpathAuthExtensions
{
    /// <summary>Adds authentication + the fallback policy per the manifest strategy.</summary>
    public static TBuilder AddGoldpathAuth<TBuilder>(this TBuilder builder, Action<GoldpathAuthOptions>? configure = null)
        where TBuilder : IHostApplicationBuilder
    {
        var options = new GoldpathAuthOptions();
        builder.Configuration.GetSection("Goldpath:Auth").Bind(options);
        configure?.Invoke(options);
        builder.Services.AddSingleton(options);

        if (options.Strategy == GoldpathAuthStrategy.None)
        {
            // Internal service behind mTLS/gateway: no AUTHENTICATION is wired ON PURPOSE —
            // but the ops policies still register (audit A4): AdminSurfaceGuard requires
            // them, and an unregistered policy answers 500 at request time instead of the
            // honest 403. With no authentication there is no principal, no role, no pass:
            // the admin floor stays fail-closed, it just refuses correctly now.
            builder.Services.AddAuthorization(authz =>
            {
                authz.AddPolicy(GoldpathPolicies.Ops, policy => policy.RequireRole(options.OpsRole));
                authz.AddPolicy(GoldpathPolicies.OpsRead, policy => policy.RequireRole(options.OpsReadRole, options.OpsRole));
                authz.AddPolicy(GoldpathPolicies.OpsAllTenants, policy => policy.RequireRole(options.OpsAllTenantsRole));
            });
            builder.Services
                .AddAuthentication(GoldpathNoneAuthenticationHandler.SchemeName)
                .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, GoldpathNoneAuthenticationHandler>(
                    GoldpathNoneAuthenticationHandler.SchemeName, static _ => { });
            return builder;
        }

        // Secure by default: every endpoint demands a principal unless it explicitly opts out,
        // and the admin surfaces additionally demand the ops role (hardening H2 — the
        // /goldpath/admin/* mappers require this policy OUT OF THE BOX).
        builder.Services.AddAuthorization(authz =>
        {
            authz.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            authz.AddPolicy(GoldpathPolicies.Ops, policy => policy.RequireRole(options.OpsRole));
            authz.AddPolicy(GoldpathPolicies.OpsRead, policy => policy.RequireRole(options.OpsReadRole, options.OpsRole));
            authz.AddPolicy(GoldpathPolicies.OpsAllTenants, policy => policy.RequireRole(options.OpsAllTenantsRole));
        });

        if (options.Strategy == GoldpathAuthStrategy.OpenId
            && options.Authority is { Length: > 0 }
            && options.Audience is not { Length: > 0 }
            && !options.AllowAnyAudience)
        {
            // Fail-closed (audit A3): with a REAL authority, a missing audience silently
            // accepted tokens minted for other clients — refuse at startup, teach the fix.
            // No authority = nothing can authenticate anyway, so the first-click dev app
            // still starts with its 401 floor intact (ADR-0008, review R3 on this PR).
            throw new InvalidOperationException(
                "Goldpath:Auth:Authority is set but Goldpath:Auth:Audience is not. Set the audience, or opt out EXPLICITLY with Goldpath:Auth:AllowAnyAudience=true (accepts any token the authority minted).");
        }

        if (options.Strategy == GoldpathAuthStrategy.OpenId)
        {
            builder.Services
                .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(jwt =>
                {
                    jwt.Authority = options.Authority;
                    jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;
                    if (options.Audience is { Length: > 0 } audience)
                    {
                        jwt.TokenValidationParameters.ValidAudience = audience;
                    }
                    else
                    {
                        // Reachable only through the VISIBLE AllowAnyAudience opt-out —
                        // the eager guard above already refused the silent path.
                        jwt.TokenValidationParameters.ValidateAudience = false;
                    }

                    jwt.Events = new JwtBearerEvents
                    {
                        OnAuthenticationFailed = failed =>
                        {
                            GoldpathAuthMetrics.Failures.Add(1, new KeyValuePair<string, object?>(
                                "reason", failed.Exception.GetType().Name));
                            return Task.CompletedTask;
                        },
                    };
                });
        }
        else
        {
            builder.Services
                .AddAuthentication(GoldpathApiKeyAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, GoldpathApiKeyAuthenticationHandler>(
                    GoldpathApiKeyAuthenticationHandler.SchemeName, displayName: null, configureOptions: null);
        }

        // Command level — Mediant's [Authorize] fed from the SAME principal (RFC D5).
        builder.Services.AddHttpContextAccessor();
        builder.Services.TryAddScoped<IAuthorizationContext, HttpClaimsAuthorizationContext>();
        builder.Services.AddMediantAuthorization(_ => { });

#if NET10_0_OR_GREATER
        AddOpenApiSecurityScheme(builder.Services, options);
#endif
        return builder;
    }

    /// <summary>
    /// Wires the pipeline in the only correct order: authentication → token–tenant binding
    /// → authorization. Place AFTER <c>UseGoldpathMultiTenancy()</c> so the ambient tenant is
    /// resolved before the binding check.
    /// </summary>
    public static IApplicationBuilder UseGoldpathAuth(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetRequiredService<GoldpathAuthOptions>();
        if (options.Strategy == GoldpathAuthStrategy.None)
        {
            // A4: the deny-only scheme + authorization still enter the pipeline, so a
            // guarded admin route answers 401/403 instead of failing at startup/request.
            app.UseAuthentication();
            return app.UseAuthorization();
        }

        app.UseAuthentication();
        if (options.Strategy == GoldpathAuthStrategy.OpenId && options.BindTenant)
        {
            app.UseMiddleware<GoldpathTenantBindingMiddleware>();
        }

        return app.UseAuthorization();
    }

#if NET10_0_OR_GREATER
    private static void AddOpenApiSecurityScheme(IServiceCollection services, GoldpathAuthOptions options)
        => services.ConfigureAll<Microsoft.AspNetCore.OpenApi.OpenApiOptions>(openApi =>
            openApi.AddDocumentTransformer((document, _, _) =>
            {
                var scheme = options.Strategy == GoldpathAuthStrategy.OpenId
                    ? new Microsoft.OpenApi.OpenApiSecurityScheme
                    {
                        Type = Microsoft.OpenApi.SecuritySchemeType.Http,
                        Scheme = "bearer",
                        BearerFormat = "JWT",
                    }
                    : new Microsoft.OpenApi.OpenApiSecurityScheme
                    {
                        Type = Microsoft.OpenApi.SecuritySchemeType.ApiKey,
                        In = Microsoft.OpenApi.ParameterLocation.Header,
                        Name = options.ApiKeyHeader,
                    };

                document.Components ??= new Microsoft.OpenApi.OpenApiComponents();
                document.Components.SecuritySchemes ??=
                    new Dictionary<string, Microsoft.OpenApi.IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes["goldpath"] = scheme;
                return Task.CompletedTask;
            }));
#endif
}
