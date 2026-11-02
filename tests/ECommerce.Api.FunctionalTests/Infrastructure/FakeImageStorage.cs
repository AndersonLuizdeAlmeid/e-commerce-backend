using System.Collections.Concurrent;
using ECommerce.Application.Abstractions.Images;

namespace ECommerce.Api.FunctionalTests.Infrastructure;

/// <summary>Bucket em memória: os testes conferem o que foi enviado e apagado sem precisar de bucket.</summary>
public sealed class FakeImageStorage : IImageStorage
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = new();

    public bool FailNextPut { get; set; }

    public byte[]? Find(string key) => _objects.GetValueOrDefault(key);

    public string PublicUrl(string key) => $"https://imagens.teste/{key}";

    public Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken)
    {
        if (FailNextPut)
        {
            FailNextPut = false;
            throw new InvalidOperationException("Falha simulada no bucket.");
        }

        _objects[key] = content;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        _objects.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
