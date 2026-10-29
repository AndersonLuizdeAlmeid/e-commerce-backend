using System.Reflection;
using ECommerce.Application;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Domain.Abstractions;
using FluentValidation;
using NetArchTest.Rules;

namespace ECommerce.ArchitectureTests;

/// <summary>Regra de dependência da Clean Architecture (arquivo 03 §1, arquivo 15 §6).</summary>
public sealed class LayerDependencyTests
{
    private const string BehaviorsNamespace = "ECommerce.Application.Behaviors";

    private static readonly Assembly DomainAssembly = typeof(Result).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(ApplicationAssemblyMarker).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(Infrastructure.DependencyInjection).Assembly;

    [Fact]
    public void Domain_DoesNotDependOnOtherLayersOrInfrastructureLibraries()
    {
        var result = Types.InAssembly(DomainAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "ECommerce.Application", "ECommerce.Infrastructure", "ECommerce.Api",
                "Dapper", "Npgsql", "Microsoft.AspNetCore")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Application_DoesNotDependOnInfrastructureOrApi()
    {
        var result = Types.InAssembly(ApplicationAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "ECommerce.Infrastructure", "ECommerce.Api", "Npgsql", "Microsoft.AspNetCore",
                "Microsoft.Extensions.Caching.Hybrid") // cache só pela porta ICacheService (arquivo 12 §2)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Infrastructure_DoesNotDependOnApi()
    {
        var result = Types.InAssembly(InfrastructureAssembly)
            .ShouldNot()
            .HaveDependencyOn("ECommerce.Api")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void QueryHandlers_AreSealedAndEndWithHandler()
    {
        var result = Types.InAssembly(ApplicationAssembly)
            .That()
            .ImplementInterface(typeof(IQueryHandler<,>))
            .And()
            .DoNotResideInNamespace(BehaviorsNamespace)
            .Should()
            .BeSealed()
            .And()
            .HaveNameEndingWith("Handler", StringComparison.Ordinal)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void CommandHandlers_AreSealedInternalAndEndWithHandler()
    {
        var result = Types.InAssembly(ApplicationAssembly)
            .That()
            .ImplementInterface(typeof(ICommandHandler<>))
            .Or()
            .ImplementInterface(typeof(ICommandHandler<,>))
            .Should()
            .BeSealed()
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));

        var named = Types.InAssembly(ApplicationAssembly)
            .That()
            .ImplementInterface(typeof(ICommandHandler<>))
            .Or()
            .ImplementInterface(typeof(ICommandHandler<,>))
            .And()
            .DoNotResideInNamespace(BehaviorsNamespace)
            .Should()
            .HaveNameEndingWith("Handler", StringComparison.Ordinal)
            .And()
            .NotBePublic()
            .GetResult();

        named.IsSuccessful.ShouldBeTrue(Describe(named));
    }

    [Fact]
    public void DomainEntities_HaveNoPublicSetters()
    {
        var offenders = DomainAssembly.GetTypes()
            .Where(type => type.Namespace is not null && !type.Namespace.EndsWith(".Abstractions", StringComparison.Ordinal))
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(property => property.SetMethod is { IsPublic: true } setter
                && !setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)))
            .Select(property => $"{property.DeclaringType!.Name}.{property.Name}")
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void Behaviors_AreSealedAndNotPublic()
    {
        var result = Types.InAssembly(ApplicationAssembly)
            .That()
            .ResideInNamespace(BehaviorsNamespace)
            .Should()
            .BeSealed()
            .And()
            .NotBePublic()
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Validators_AreSealedAndEndWithValidator()
    {
        var result = Types.InAssembly(ApplicationAssembly)
            .That()
            .Inherit(typeof(AbstractValidator<>))
            .Should()
            .BeSealed()
            .And()
            .HaveNameEndingWith("Validator", StringComparison.Ordinal)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    private static string Describe(NetArchTest.Rules.TestResult result) =>
        "Tipos que violam a regra: " + string.Join(", ", result.FailingTypeNames ?? []);
}
