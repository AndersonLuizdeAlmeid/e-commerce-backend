using System.Globalization;
using System.Text;
using Dapper;
using ECommerce.Application.Abstractions.Authentication;
using ECommerce.Application.Abstractions.Caching;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Outbox;
using ECommerce.Domain.Customers;
using ECommerce.Domain.Identity;
using ECommerce.Infrastructure.Authentication;
using ECommerce.Infrastructure.Caching;
using ECommerce.Infrastructure.Jobs;
using ECommerce.Infrastructure.Messaging;
using ECommerce.Infrastructure.Notifications;
using ECommerce.Infrastructure.Persistence;
using ECommerce.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ECommerce.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var connectionString = configuration.GetConnectionString("Database")
            ?? throw new InvalidOperationException("ConnectionStrings:Database não configurada.");

        DefaultTypeMap.MatchNamesWithUnderscores = true;

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton<IDbConnectionFactory, NpgsqlConnectionFactory>();

        AddPersistence(services);
        AddCaching(services, configuration);
        AddAuthentication(services, configuration, environment);
        AddMessaging(services, configuration);

        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("postgres", tags: ["ready"]);

        return services;
    }

    private static void AddPersistence(IServiceCollection services)
    {
        services.AddScoped<UnitOfWork>();
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<UnitOfWork>());
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IUserTokenRepository, UserTokenRepository>();
        services.AddScoped<ICustomerRepository, CustomerRepository>();
    }

    private static void AddCaching(IServiceCollection services, IConfiguration configuration)
    {
        // Só L1 (arquivo 20, T04): sem Redis enquanto houver uma instância.
        var defaultExpiration = TimeSpan.FromMinutes(
            int.TryParse(configuration["Caching:DefaultExpirationMinutes"], CultureInfo.InvariantCulture, out var minutes) ? minutes : 5);
        services.AddHybridCache(options => options.DefaultEntryOptions = new HybridCacheEntryOptions
        {
            Expiration = defaultExpiration,
            LocalCacheExpiration = defaultExpiration,
        });
        services.AddSingleton<ICacheService, HybridCacheService>();
    }

    private static void AddAuthentication(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<JwtSettings>()
            .Bind(configuration.GetSection(JwtSettings.SectionName))
            .Validate(
                settings => Encoding.UTF8.GetByteCount(settings.SigningKey) >= JwtSettings.MinimumKeyBytes,
                $"Jwt:SigningKey precisa de pelo menos {JwtSettings.MinimumKeyBytes} bytes.")
            .Validate(
                settings => environment.IsDevelopment() || environment.IsEnvironment("Testing")
                    || !settings.SigningKey.Contains(JwtSettings.DevelopmentKeyMarker, StringComparison.Ordinal),
                "A chave JWT de desenvolvimento não pode ser usada fora de Development.")
            .ValidateOnStart();

        services.AddSingleton(sp =>
        {
            var jwt = sp.GetRequiredService<IOptions<JwtSettings>>().Value;
            return new AuthLifetimes(
                TimeSpan.FromMinutes(jwt.AccessTokenMinutes),
                TimeSpan.FromDays(jwt.RefreshTokenDays),
                TimeSpan.FromDays(jwt.RefreshTokenAbsoluteDays));
        });
        services.AddSingleton<IAccessTokenIssuer, JwtAccessTokenIssuer>();
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<ISecureTokenFactory, SecureTokenFactory>();
    }

    private static void AddMessaging(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OutboxSettings>().Bind(configuration.GetSection(OutboxSettings.SectionName));
        services.AddScoped<IOutbox, OutboxWriter>();
        services.AddScoped<IOutboxMessageHandler, SendEmailHandler>();
        services.AddHostedService<OutboxProcessor>();
        services.AddHostedService<MaintenanceJob>();

        var emailSection = configuration.GetSection(EmailSettings.SectionName);
        services.AddOptions<EmailSettings>()
            .Bind(emailSection)
            .Validate(
                settings => settings.Provider is "Smtp" or "Resend",
                "Email:Provider deve ser 'Smtp' ou 'Resend'.")
            .Validate(
                settings => settings.Provider != "Resend" || !string.IsNullOrWhiteSpace(settings.Resend.ApiKey),
                "Email:Resend:ApiKey é obrigatório com o provedor Resend.")
            .ValidateOnStart();
        services.AddSingleton<EmailTemplateRenderer>();

        if (string.Equals(emailSection["Provider"], "Resend", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient<IEmailSender, ResendEmailSender>((sp, client) =>
            {
                client.BaseAddress = new Uri(sp.GetRequiredService<IOptions<EmailSettings>>().Value.Resend.BaseUrl);
                client.Timeout = TimeSpan.FromSeconds(15);
            });
        }
        else
        {
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        }
    }
}
