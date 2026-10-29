using System.Reflection;

namespace ECommerce.Api.Endpoints;

/// <summary>Agrupa as rotas de uma feature (ADR-001).</summary>
internal interface IEndpointGroup
{
    void Map(IEndpointRouteBuilder app);
}

internal static class EndpointGroupExtensions
{
    public static IEndpointRouteBuilder MapEndpointGroups(this IEndpointRouteBuilder app, IEndpointRouteBuilder root)
    {
        var groups = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false } && type.IsAssignableTo(typeof(IEndpointGroup)))
            .Select(type => (IEndpointGroup)Activator.CreateInstance(type)!);

        foreach (var group in groups)
        {
            group.Map(root);
        }

        return app;
    }
}
