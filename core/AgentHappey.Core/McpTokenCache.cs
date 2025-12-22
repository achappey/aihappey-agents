using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace AgentHappey.Core;

public static class McpTokenCache
{
    private static readonly ConcurrentDictionary<string, CacheItem> _store = new();

    public static bool TryGet(string key, out string token)
    {
        token = null!;
        if (_store.TryGetValue(key, out var item))
        {
            if (item.ExpiresUtc > DateTime.UtcNow)
            {
                token = item.Token;
                return true;
            }
            _store.TryRemove(key, out _);
        }
        return false;
    }

    public static void Set(string key, string token, DateTime expiresUtc)
    {
        _store[key] = new CacheItem(token, expiresUtc);
    }

    private sealed record CacheItem(string Token, DateTime ExpiresUtc);
}

public static class McpTokenCacheKey
{
    public static string Make(string resource, string scopes, string userToken)
    {
        using var sha = SHA256.Create();
        var hash = Convert.ToHexString(
            sha.ComputeHash(Encoding.UTF8.GetBytes(userToken))
        );
        return $"{resource}|{scopes}|{hash}";
    }
}