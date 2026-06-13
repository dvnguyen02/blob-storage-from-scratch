namespace BlobServer.Core.Security;

using System.Text;

public static class HmacSignatureCalculator
{
    // Compute HMAC-SHA256 of message using key, return base 64 string 
    public static string Compute(string message, string key)
    {
        var keyBytes = Convert.FromBase64String(key);
        var messageBytes = Encoding.UTF8.GetBytes(message);
        var hmacObject = new System.Security.Cryptography.HMACSHA256(keyBytes);
        var hashBytes = hmacObject.ComputeHash(messageBytes);
        return Convert.ToBase64String(hashBytes);
    }

    // Build the canonical string for Shared key auth 
    // Format: " 
    // {method}\n{contentMd5}\n{contentType}\n{date}\n{resource}" 
    // Pass empty string "" for any header that is absent
    public static string BuildCanonicalString(
        string method, string contentMd5, string contentType,
        string date, string resource
    )
    {
        return $"{method}\n{contentMd5}\n{contentType}\n{date}\n{resource}";

    }
}