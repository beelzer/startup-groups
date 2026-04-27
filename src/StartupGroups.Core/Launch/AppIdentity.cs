using System.Security.Cryptography;
using System.Text;

namespace StartupGroups.Core.Launch;

public static class AppIdentity
{
    public static string ComputeAppId(string? resolvedPath, string? fallbackName)
    {
        if (!string.IsNullOrEmpty(resolvedPath))
        {
            return ComputePathHash(resolvedPath);
        }

        if (!string.IsNullOrEmpty(fallbackName))
        {
            return "name:" + ComputeStringHash(fallbackName.Trim().ToLowerInvariant());
        }

        return "unknown:" + Guid.NewGuid().ToString("N");
    }

    public static string ComputePathHash(string path)
    {
        var normalized = path.Trim().ToLowerInvariant();
        return "path:" + ComputeStringHash(normalized);
    }

    private static string ComputeStringHash(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }
}
