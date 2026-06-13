using System.Security.Cryptography;
using BlobServer.Core.Security;

public class AuthHandler(RequestDelegate next, IConfiguration config)
{

    private readonly string _accountName = config["BlobAuth:AccountName"]!;

    private readonly string _sharedKey = config["BlobAuth:SharedKey"]!;

    public async Task InvokeAsync(HttpContext context)
    {
        // Validate auth here
        // If valid await next(context)
        // else: return 403

        // SAS path: present when ?sig=... is in the query string
        if (context.Request.Query.ContainsKey("sig"))
        {
            // TODO(human): validate SAS
            // 1. Read se, sp, sv, sig from context.Request.Query
            //      var sasSig = context.Request.Query["sig"].ToString();
            //      var expiry = context.Request.Query["se"].ToString();
            //      var permissions = context.Request.Query["sp"].ToString();
            //      var version = context.Request.Query["sv"].ToString();
            //
            // 2. Reject if expiry is in the past
            //      if (DateTimeOffset.Parse(expiry) < DateTimeOffset.UtcNow) → fall through to 403
            //
            // 3. Build the SAS canonical string: $"{resource}\n{expiry}\n{permissions}\n{version}"
            //      resource = context.Request.Path.ToString()
            //
            // 4. Compute the expected sig with HmacSignatureCalculator.Compute(canonical, _sharedKey)
            //
            // 5. Compare with CryptographicOperations.FixedTimeEquals (same pattern as SharedKey branch)
            //
            // 6. Check that permissions covers the verb:
            //      GET/HEAD requires "r" in permissions
            //      PUT requires "w" in permissions
            //      DELETE requires "d" in permissions
            //
            // 7. If everything checks out: await next(context); return;
            var sasSig = context.Request.Query["sig"].ToString();
            var expiry = context.Request.Query["se"].ToString();
            var permissions = context.Request.Query["sp"].ToString();
            var version = context.Request.Query["sv"].ToString();


            // Reject if expiry is in the past 
            if (DateTimeOffset.Parse(expiry) >= DateTimeOffset.UtcNow)
            {
                var resource = context.Request.Path.ToString();
                var canonicalString = $"{resource}\n{expiry}\n{permissions}\n{version}";
                var expectedSig = HmacSignatureCalculator.Compute(canonicalString, _sharedKey);
                var expectedBytes = Convert.FromBase64String(expectedSig);
                var actualBytes = Convert.FromBase64String(sasSig);
                if (CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
                {
                    var requiredPermissions = context.Request.Method switch
                    {
                        "GET" or "HEAD" => "r",
                        "PUT" => "w",
                        "DELETE" => "d",
                        _ => "?"
                    };
                    if (permissions.Contains(requiredPermissions))
                    {
                        await next(context);
                        return;
                    }
                }
            }


        }

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

        // Fallthrough 403
        context.Response.StatusCode = 403;
        await context.Response.WriteAsJsonAsync(new { code = "AuthFailure", message = "Missing or invalid authorisation." });
    }
}
