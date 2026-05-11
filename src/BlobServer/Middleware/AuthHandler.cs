using System.Security.Cryptography;
using BlobServer.Core.Security;
using Microsoft.AspNetCore.Authentication;

public class AuthHandler(RequestDelegate next, IConfiguration config)
{

    private readonly string _accountName = config["BlobAuth:AccountName"]!;

    private readonly string _sharedKey = config["BlobAuth:SharedKey"]!;

    public async Task InvokeAsync(HttpContext context)
    {
        // Validate auth here 
        // If valid await next(context)
        // else: return 403
        var auth = context.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("SharedKey "))
        {
            var length = "SharedKey ".Length;
            var accountAndSig = auth[length..];
            string[] parts = accountAndSig.Split(":");
            string account = parts[0];
            string sig = parts[1];
            // Fall through anyway, dead code
            // if (account != _accountName)
            // {
            //     return; 
            // }
            var method = context.Request.Method;
            var contentMd5 = context.Request.Headers["Content-MD5"].ToString();
            var contentType = context.Request.ContentType ?? "";
            var date = context.Request.Headers["Date"].ToString();
            var resource = context.Request.Path.ToString();

            var canonicalString = HmacSignatureCalculator.BuildCanonicalString(method, contentMd5, contentType, date, resource);
            var expectedSig = HmacSignatureCalculator.Compute(canonicalString, _sharedKey);
            var expectedBytes = Convert.FromBase64String(expectedSig);
            var actualBytes = Convert.FromBase64String(sig);
            if (CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
            {
                await next(context);
                return;
            }
        }
        context.Response.StatusCode = 403;
        await context.Response.WriteAsJsonAsync(new { code = "AuthFailure", message = "Missing or invalid authorisation." });
    }
}
