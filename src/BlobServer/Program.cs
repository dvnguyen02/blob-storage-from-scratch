using System.Xml.Linq;
using BlobServer.Core.Errors;
using BlobServer.Core.Metadata;
using BlobServer.Core.Services;
using BlobServer.Core.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var accountName = builder.Configuration["BlobAuth:AccountName"]!;
var sharedKey = builder.Configuration["BlobAuth:SharedKey"]!;
builder.Services.AddDbContext<BlobDbContext>(opt => opt.UseSqlite("Data Source=blobs.db"));
builder.Services.AddSingleton<IBlobStore>(new FileSystemBlobStore("storage"));
builder.Services.AddScoped<BlobService>();
var app = builder.Build();

using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<BlobDbContext>().Database.EnsureCreated();
app.UseMiddleware<AuthHandler>();
app.MapPut("/{container}", async (string container, BlobService service, CancellationToken ct) =>
{
    var result = await service.CreateContainerAsync(container, ct);
    if (result is true)
    {
        return Results.Created($"/{container}", null);
    }
    return Results.Conflict(new BlobError("ContainerAlreadyExists", "The specified container already exsists."));

});

// TODO(human): implement DELETE /{container}
//
// Call service.DeleteContainerAsync(container, ct) and map the three outcomes:
//   DeleteContainerResult.Deleted   → Results.NoContent()                    // 204
//   DeleteContainerResult.NotFound  → Results.Json(new BlobError(
//                                       "ContainerNotFound",
//                                       "The specified container does not exist."), statusCode: 404)
//   DeleteContainerResult.NotEmpty  → Results.Json(new BlobError(
//                                       "ContainerNotEmpty",
//                                       "The specified container is not empty."), statusCode: 409)
//
// Use a switch expression over the result — it's exhaustive (the compiler will warn
// if you forget a case). Pattern:
//
//   return result switch
//   {
//       DeleteContainerResult.Deleted  => Results.NoContent(),
//       DeleteContainerResult.NotFound => Results.Json(...),
//       DeleteContainerResult.NotEmpty => Results.Json(...),
//       _ => Results.StatusCode(500),
//   };

app.MapDelete("/{container}", async (string container, BlobService service, CancellationToken ct) =>
{
    var result = await service.DeleteContainerAsync(container, ct);
    return result switch
    {
        DeleteContainerResult.Deleted => Results.NoContent(),
        DeleteContainerResult.NotFound => Results.Json(new BlobError("ContainerNotFound", "The container does not exists."), statusCode: 404),
        DeleteContainerResult.NotEmpty => Results.Json(new BlobError("ContainerNotEmpty", "The specified container is not emtpy."), statusCode: 409),
        _ => Results.StatusCode(500),
    };
});

app.MapPut("/{container}/{blob}", async (string container, string blob,
                                        [FromQuery] string? comp,
                                        [FromQuery] string? blockId, HttpRequest request,
                                        BlobService service, CancellationToken ct, HttpContext httpContext) =>
{
    if (comp == "block")
    {
        if (blockId is null)
        {
            return Results.BadRequest(new BlobError("MissingBlockId", "blockId is required"));
        }
        await service.StageBlockAsync(container, blob, blockId, request.Body, ct);
        return Results.Ok();
    }

    else if (comp == "blocklist")
    {
        var body = await new StreamReader(request.Body).ReadToEndAsync(ct);
        var doc = XDocument.Parse(body);
        var blockIds = doc.Root!.Elements("Latest").Select(e => e.Value).ToList();
        try
        {
            var committed = await service.CommitBlockListAsync(container, blob, blockIds, ct);
            httpContext.Response.Headers.ETag = committed.ETag;
            return Results.Ok();
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new BlobError("InvalidBlockList", ex.Message));
        }
    }
    var contentType = request.ContentType;
    var currentEtag = await service.GetBlobTagAsync(blob, container, ct);
    // Check If Match header first 
    var ifMatch = httpContext.Request.Headers.IfMatch.ToString();
    if (ifMatch != string.Empty && currentEtag != ifMatch)
    {
        return Results.StatusCode(412);
    }
    // Then write
    var blobRow = await service.PutAsync(container, blob, request.Body, contentType, ct);
    httpContext.Response.Headers.ETag = blobRow.ETag;
    return Results.Ok();
});


app.MapGet("/{container}/{blob}", async (string container, string blob, BlobService service, CancellationToken ct, HttpContext httpContext) =>
{
    var result = await service.GetAsync(container, blob, ct);
    if (result is null)
    {
        return Results.Json(new BlobError("BlobNotFound", "The specified blob does not exist."), statusCode: 404);
    }

    var ifNoneMatch = httpContext.Request.Headers.IfNoneMatch.ToString();
    if (ifNoneMatch != string.Empty)
    {
        if (ifNoneMatch == result.Value.Blob.ETag)
        {
            return Results.StatusCode(304);
        }
    }
    else
    {
        var ifModSince = httpContext.Request.Headers.IfModifiedSince.ToString();
        if (ifModSince != string.Empty &&
            DateTimeOffset.TryParseExact(ifModSince, "R",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out var since)
            && DateTime.SpecifyKind(result.Value.Blob.ModifiedAt, DateTimeKind.Utc) <= since)
        {
            return Results.StatusCode(304);
        }
    }



    // If user include Range:bytes=x-y Header
    var rangeHeader = httpContext.Request.Headers.Range.ToString();
    if (rangeHeader != string.Empty)
    {
        var parts = rangeHeader.Replace("bytes=", "");
        var numbers = parts.Split("-");
        var start = long.Parse(numbers[0]);
        var end = long.Parse(numbers[1]);
        var length = (int)(end - start + 1);
        var bytes = new byte[length];

        // Return 416 when range does not have not enough bytes / satisfiable 
        if (start >= result.Value.Blob.Size || end >= result.Value.Blob.Size)
        {
            return Results.StatusCode(416);
        }

        result.Value.BlobStream.Seek(start, SeekOrigin.Begin);
        await result.Value.BlobStream.ReadExactlyAsync(bytes, ct);
        httpContext.Response.StatusCode = 206;
        httpContext.Response.ContentType = result.Value.Blob.ContentType ?? "application/octet-stream";
        httpContext.Response.Headers.ContentRange = $"bytes {start}-{end}/{result.Value.Blob.Size}";
        await httpContext.Response.Body.WriteAsync(bytes, ct);
        return Results.Empty;
    }

    return Results.Stream(result.Value.BlobStream, result.Value.Blob.ContentType);
});

app.MapMethods("/{container}/{blob}", new[] { "HEAD" }, async (string container, string blob, BlobService service, HttpContext httpContext, CancellationToken ct) =>
{
    var result = await service.GetAsync(container, blob, ct);
    if (result is null)
    {
        return Results.Json(new BlobError("BlobNotFound", "The specified blob does not exsist."), statusCode: 404);
    }

    var ifNoneMatch = httpContext.Request.Headers.IfNoneMatch.ToString();
    if (ifNoneMatch != string.Empty)
    {
        if (ifNoneMatch == result.Value.Blob.ETag)
        {
            result.Value.BlobStream.Dispose();
            return Results.StatusCode(304);
        }
    }
    else
    {
        var ifModSince = httpContext.Request.Headers.IfModifiedSince.ToString();
        if (ifModSince != string.Empty &&
            DateTimeOffset.TryParseExact(ifModSince, "R",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out var since) && DateTime.SpecifyKind(result.Value.Blob.ModifiedAt, DateTimeKind.Utc) <= since)
        {
            result.Value.BlobStream.Dispose();
            return Results.StatusCode(304);
        }
    }

    httpContext.Response.Headers.ETag = result.Value.Blob.ETag;
    httpContext.Response.Headers.ContentLength = result.Value.Blob.Size;
    httpContext.Response.ContentType = result.Value.Blob.ContentType;
    httpContext.Response.Headers.LastModified = result.Value.Blob.ModifiedAt.ToString("R");
    result.Value.BlobStream.Dispose();
    return Results.Empty;

});

app.MapGet("/{container}", async (string container, BlobService service, CancellationToken ct) =>
{
    var blobs = await service.ListAsync(container, ct);
    if (blobs is null)
    {
        return Results.Json(new BlobError("ContainerNotFound", "The specified container does not exists."), statusCode: 404);
    }
    ;
    var items = blobs.Select(b => new { b.Name, b.Size, b.ContentType, b.ETag, b.ModifiedAt });
    return Results.Ok(items);
});

app.MapDelete("/{container}/{blob}", async (string container, string blob, BlobService service, HttpContext httpContext, CancellationToken ct) =>
{
    var ifMatch = httpContext.Request.Headers.IfMatch.ToString();
    if (ifMatch != string.Empty)
    {
        var currentEtag = await service.GetBlobTagAsync(blob, container, ct);
        if (currentEtag != ifMatch)
        {
            return Results.StatusCode(412);
        }
    }
    var deleted = await service.DeleteAsync(container, blob, ct);
    return deleted ? Results.NoContent() : Results.Json(new BlobError("BlobNotFound", "The specified blob does not exist."), statusCode: 404);

});



app.Run();

public partial class Program
{

}