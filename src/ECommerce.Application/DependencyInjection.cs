using System.Globalization;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Behaviors;
using ECommerce.Application.Identity;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace ECommerce.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.Scan(scan => scan
            .FromAssemblyOf<ApplicationAssemblyMarker>()
            .AddClasses(
                classes => classes
                    .AssignableToAny(typeof(IQueryHandler<,>), typeof(ICommandHandler<>), typeof(ICommandHandler<,>))
                    .Where(type => !type.IsGenericTypeDefinition), // decorators não são handlers
                publicOnly: false)
            .AsImplementedInterfaces()
            .WithScopedLifetime());

        services.AddValidatorsFromAssemblyContaining<ApplicationAssemblyMarker>(ServiceLifetime.Singleton, includeInternalTypes: true);
        ValidatorOptions.Global.LanguageManager.Culture = CultureInfo.GetCultureInfo("pt-BR");

        // O último Decorate fica por fora: Validação → Transação → Handler.
        services.Decorate(typeof(IQueryHandler<,>), typeof(ValidatingQueryHandler<,>));
        services.Decorate(typeof(ICommandHandler<>), typeof(TransactionalCommandHandler<>));
        services.Decorate(typeof(ICommandHandler<,>), typeof(TransactionalCommandHandler<,>));
        services.Decorate(typeof(ICommandHandler<>), typeof(ValidatingCommandHandler<>));
        services.Decorate(typeof(ICommandHandler<,>), typeof(ValidatingCommandHandler<,>));

        services.AddScoped<SessionIssuer>();
        services.AddScoped<AccountEmails>();

        return services;
    }
}

public sealed class ApplicationAssemblyMarker;
