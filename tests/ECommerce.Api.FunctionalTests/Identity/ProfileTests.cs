using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ECommerce.Api.FunctionalTests.Infrastructure;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Identity.SeedInitialAdmin;
using Microsoft.Extensions.DependencyInjection;

namespace ECommerce.Api.FunctionalTests.Identity;

public sealed class ProfileTests(ApiFactory factory) : FunctionalTest(factory)
{
    private const string ValidCpf = "529.982.247-25";
    private const string OtherValidCpf = "111.444.777-35";

    [Fact, Trait("Spec", "RF-005")]
    public async Task GetProfile_ReturnsOwnDataWithMaskedCpf()
    {
        using var client = await AuthClient.RegisteredAsync(Factory, "maria@loja.teste", ValidCpf);

        var profile = await (await client.GetAsync("/api/v1/me/profile")).Content.ReadFromJsonAsync<JsonElement>(CancellationToken);

        profile.GetProperty("fullName").GetString().ShouldBe("Maria da Silva");
        profile.GetProperty("email").GetString().ShouldBe("maria@loja.teste");
        profile.GetProperty("phone").GetString().ShouldBe("11987654321");
        profile.GetProperty("cpfMasked").GetString().ShouldBe("***.982.247-**");
        profile.GetProperty("hasCpf").GetBoolean().ShouldBeTrue();
    }

    [Fact, Trait("Spec", "RF-005")]
    public async Task UpdateProfile_ChangesNameAndPhoneAndKeepsCpfWhenEmpty()
    {
        using var client = await AuthClient.RegisteredAsync(Factory, "maria@loja.teste", ValidCpf);

        var response = await client.PutAsync("/api/v1/me/profile", new { fullName = "  Maria   Souza ", phone = "(21) 3333-4444", cpf = "" });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var profile = await (await client.GetAsync("/api/v1/me/profile")).Content.ReadFromJsonAsync<JsonElement>(CancellationToken);
        profile.GetProperty("fullName").GetString().ShouldBe("Maria Souza");
        profile.GetProperty("phone").GetString().ShouldBe("2133334444");
        profile.GetProperty("cpfMasked").GetString().ShouldBe("***.982.247-**");
    }

    [Fact, Trait("Spec", "RF-005")]
    public async Task UpdateProfile_WithCpfOfAnotherCustomer_ReturnsConflict()
    {
        (await AuthClient.RegisteredAsync(Factory, "joao@loja.teste", OtherValidCpf)).Dispose();
        using var client = await AuthClient.RegisteredAsync(Factory, "maria@loja.teste");

        var response = await client.PutAsync("/api/v1/me/profile", new { fullName = "Maria da Silva", phone = "11987654321", cpf = OtherValidCpf });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact, Trait("Spec", "RF-060")]
    public async Task SeedInitialAdmin_CreatesAdminOnlyOnce()
    {
        (await SeedAdminAsync("admin@loja.teste")).ShouldBeTrue();
        (await SeedAdminAsync("outro-admin@loja.teste")).ShouldBeFalse();

        var admins = await Factory.QuerySingleAsync<long>("SELECT count(*) FROM identity.users WHERE role = 'Admin'");
        admins.ShouldBe(1);
    }

    [Fact, Trait("Spec", "Q06")]
    public async Task Admin_CannotUseCustomerArea()
    {
        await SeedAdminAsync("admin@loja.teste");
        using var admin = new AuthClient(Factory);
        (await admin.LoginAsync("admin@loja.teste", "Admin@12345")).EnsureSuccessStatusCode();

        (await admin.GetAsync("/api/v1/me/profile")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await admin.GetAsync("/api/v1/auth/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<bool> SeedAdminAsync(string email)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<SeedInitialAdminCommand, bool>>();
        var result = await handler.Handle(new SeedInitialAdminCommand(email, "Admin@12345"), CancellationToken);
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : null);
        return result.Value;
    }
}
