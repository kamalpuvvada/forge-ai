using System.Security.Cryptography;
using System.Text;

namespace Forge.Core;

public static class RepositoryDisplayIdentifier
{
    public static string Create(string repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        var normalized = repository.Trim().Replace('\\', '/').TrimEnd('/');
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized.ToUpperInvariant()));
        return $"Repository {Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant()}";
    }
}
