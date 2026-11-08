using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using ECommerce.Api.FunctionalTests.Catalog;
using ECommerce.Api.FunctionalTests.Infrastructure;
using ECommerce.Application.Abstractions.Caching;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Ordering;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ECommerce.Api.FunctionalTests.Ordering;

/// <summary>Base dos testes de pedido e pagamento: loja com frete configurado, peças com saldo e cliente pronta para comprar.</summary>
public abstract class ShopperTest(ApiFactory factory) : AdminCatalogTest(factory)
{
    private static readonly object ShippingSettings = new
    {
        originPostalCode = "01310-100",
        handlingDays = 2,
        fallbackPacPrice = 25m,
        fallbackSedexPrice = 45m,
        boxWeightGrams = 100,
        boxHeightCm = 4m,
        boxWidthCm = 12m,
        boxLengthCm = 16m,
    };

    protected async Task<AuthClient> StoreAsync()
    {
        var admin = await AdminAsync();
        (await admin.PutAsync("/api/v1/admin/shipping/settings", ShippingSettings)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await Factory.Services.GetRequiredService<ICacheService>().RemoveByTagAsync("shipping", CancellationToken);
        return admin;
    }

    /// <summary>Peça na loja a R$ 89,90 com o saldo pedido, criada pelo painel.</summary>
    protected static async Task<Guid> PieceAsync(AuthClient admin, string name, int stock)
    {
        var product = await ProductInStoreAsync(admin, name);
        var variant = (await GetProductAsync(admin, product)).GetProperty("variants")[0].GetProperty("id").GetGuid();
        if (stock > 0)
        {
            (await admin.PostAsync($"/api/v1/admin/stock/{variant}/movements", new { type = "In", quantity = stock, reason = "Teste" })).EnsureSuccessStatusCode();
        }

        return variant;
    }

    /// <summary>Cliente com e-mail confirmado (D05), um endereço guardado e a sacola com as peças pedidas.</summary>
    protected async Task<(AuthClient Client, Guid Address)> ShopperAsync(string email, params (Guid Variant, int Quantity)[] cart)
    {
        var client = await AuthClient.RegisteredAsync(Factory, email);
        await ConfirmEmailAsync(email);
        var address = await client.PostAsync("/api/v1/me/addresses", new
        {
            recipient = "Maria da Silva",
            postalCode = "20040-020",
            street = "Rua da Assembleia",
            number = "10",
            district = "Centro",
            city = "Rio de Janeiro",
            state = "RJ",
        });
        address.StatusCode.ShouldBe(HttpStatusCode.Created);
        foreach (var (variant, quantity) in cart)
        {
            (await client.PostAsync("/api/v1/cart/items", new { variantId = variant, quantity })).EnsureSuccessStatusCode();
        }

        return (client, (await address.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("id").GetGuid());
    }

    protected async Task ConfirmEmailAsync(string email)
    {
        await using var connection = new NpgsqlConnection(Factory.ConnectionString);
        await connection.ExecuteAsync("UPDATE identity.users SET email_confirmed_at = now() WHERE email = @Email", new { Email = email });
    }

    protected async Task ExecuteAsync(string sql, object parameters)
    {
        await using var connection = new NpgsqlConnection(Factory.ConnectionString);
        await connection.ExecuteAsync(sql, parameters);
    }

    protected async Task<T> ScalarAsync<T>(string sql, object parameters)
    {
        await using var connection = new NpgsqlConnection(Factory.ConnectionString);
        return await connection.ExecuteScalarAsync<T>(sql, parameters) ?? throw new InvalidOperationException(sql);
    }

    protected Task<int> StockAsync(Guid variant) =>
        ScalarAsync<int>("SELECT quantity_on_hand FROM inventory.stock_items WHERE variant_id = @Id", new { Id = variant });

    protected static Task<HttpResponseMessage> PlaceAsync(AuthClient client, Guid address, string service = "PAC", object? gift = null, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(new { addressId = address, shippingServiceCode = service, gift }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", client.AccessToken);
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return client.Http.SendAsync(request, CancellationToken);
    }

    protected static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        response.StatusCode.ShouldBe(expected, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken);
    }

    protected static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("code").GetString();

}
