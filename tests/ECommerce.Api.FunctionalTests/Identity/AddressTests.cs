using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ECommerce.Api.FunctionalTests.Catalog;
using ECommerce.Api.FunctionalTests.Infrastructure;

namespace ECommerce.Api.FunctionalTests.Identity;

public sealed class AddressTests(ApiFactory factory) : AdminCatalogTest(factory)
{
    private const string Path = "/api/v1/me/addresses";

    private static object Address(
        string street = "Rua Augusta", string postalCode = "01305-000", string state = "SP", bool isDefault = false, string? complement = "Apto 12") => new
        {
            recipient = "Maria da Silva",
            postalCode,
            street,
            number = "100",
            complement,
            district = "Consolação",
            city = "São Paulo",
            state,
            isDefault,
        };

    private Task<AuthClient> CustomerAsync(string email = "cliente@loja.teste") => AuthClient.RegisteredAsync(Factory, email);

    private static async Task<JsonElement> CreateAsync(AuthClient customer, object body)
    {
        var response = await customer.PostAsync(Path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken);
    }

    private static async Task<JsonElement> ListAsync(AuthClient customer)
    {
        var response = await customer.GetAsync(Path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken);
    }

    private static Guid Id(JsonElement address) => address.GetProperty("id").GetGuid();

    [Fact, Trait("Spec", "RF-007")]
    public async Task Address_CreateEditAndRemove()
    {
        using var customer = await CustomerAsync();
        (await ListAsync(customer)).GetArrayLength().ShouldBe(0);

        var created = await CreateAsync(customer, Address());
        created.GetProperty("postalCode").GetString().ShouldBe("01305000");
        created.GetProperty("isDefault").GetBoolean().ShouldBeTrue();

        (await customer.PutAsync($"{Path}/{Id(created)}", Address("Rua Oscar Freire", complement: ""))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var edited = (await ListAsync(customer))[0];
        edited.GetProperty("street").GetString().ShouldBe("Rua Oscar Freire");
        edited.GetProperty("complement").ValueKind.ShouldBe(JsonValueKind.Null);
        edited.GetProperty("isDefault").GetBoolean().ShouldBeTrue();

        (await customer.DeleteAsync($"{Path}/{Id(created)}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ListAsync(customer)).GetArrayLength().ShouldBe(0);
    }

    [Fact, Trait("Spec", "RF-007")]
    public async Task Default_IsAlwaysExactlyOne_AndComesFirst()
    {
        using var customer = await CustomerAsync();
        var first = await CreateAsync(customer, Address());
        var second = await CreateAsync(customer, Address("Rua Oscar Freire"));
        second.GetProperty("isDefault").GetBoolean().ShouldBeFalse();

        (await customer.PutAsync($"{Path}/{Id(second)}/default", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var list = await ListAsync(customer);
        Id(list[0]).ShouldBe(Id(second));
        list.EnumerateArray().Count(address => address.GetProperty("isDefault").GetBoolean()).ShouldBe(1);

        var third = await CreateAsync(customer, Address("Rua da Consolação", isDefault: true));
        Id((await ListAsync(customer))[0]).ShouldBe(Id(third));

        (await customer.DeleteAsync($"{Path}/{Id(third)}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var remaining = await ListAsync(customer);
        remaining.GetArrayLength().ShouldBe(2);
        Id(remaining[0]).ShouldBe(Id(second));
        remaining[0].GetProperty("isDefault").GetBoolean().ShouldBeTrue();
        Id(remaining[1]).ShouldBe(Id(first));
    }

    [Fact, Trait("Spec", "RF-007-CA1")]
    public async Task AnotherCustomersAddress_IsNotFound()
    {
        using var owner = await CustomerAsync("dona@loja.teste");
        var address = await CreateAsync(owner, Address());
        using var other = await CustomerAsync("outra@loja.teste");

        (await other.PutAsync($"{Path}/{Id(address)}", Address("Rua Invadida"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.PutAsync($"{Path}/{Id(address)}/default", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.DeleteAsync($"{Path}/{Id(address)}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ListAsync(other)).GetArrayLength().ShouldBe(0);

        (await ListAsync(owner))[0].GetProperty("street").GetString().ShouldBe("Rua Augusta");
    }

    [Fact, Trait("Spec", "RF-007-CA2")]
    public async Task InvalidFields_AreReportedByField()
    {
        using var customer = await CustomerAsync();

        var response = await customer.PostAsync(Path, new { recipient = "Ma", postalCode = "123", street = "", number = "1", district = "Centro", city = "São Paulo", state = "XX" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("errors");
        errors.TryGetProperty("recipient", out _).ShouldBeTrue();
        errors.TryGetProperty("postalCode", out _).ShouldBeTrue();
        errors.TryGetProperty("street", out _).ShouldBeTrue();
        errors.TryGetProperty("state", out _).ShouldBeTrue();
    }

    [Fact, Trait("Spec", "RF-007-CA2")]
    public async Task StateThatDoesNotMatchThePostalCode_IsRefused()
    {
        using var customer = await CustomerAsync();

        var response = await customer.PostAsync(Path, Address(state: "MG"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("errors").TryGetProperty("state", out _).ShouldBeTrue();
    }

    [Fact, Trait("Spec", "RF-007")]
    public async Task Book_IsLimitedToTenAddresses()
    {
        using var customer = await CustomerAsync();
        for (var i = 0; i < 10; i++)
        {
            await CreateAsync(customer, Address($"Rua {i + 1}"));
        }

        (await customer.PostAsync(Path, Address("Rua 11"))).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact, Trait("Spec", "RF-007")]
    public async Task TwoNewDefaultsAtTheSameTime_LeaveOneDefault()
    {
        using var customer = await CustomerAsync();
        await CreateAsync(customer, Address());

        var responses = await Task.WhenAll(
            customer.PostAsync(Path, Address("Rua A", isDefault: true)),
            customer.PostAsync(Path, Address("Rua B", isDefault: true)));

        responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.Created);
        var list = await ListAsync(customer);
        list.GetArrayLength().ShouldBe(3);
        list.EnumerateArray().Count(address => address.GetProperty("isDefault").GetBoolean()).ShouldBe(1);
    }

    [Fact, Trait("Spec", "Q06")]
    public async Task Admin_HasNoAddressBook()
    {
        using var admin = await AdminAsync();

        (await admin.GetAsync(Path)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Client.GetAsync(Path, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
