using System.Xml.Linq;
using BlobServer.Core.Errors;
using BlobServer.Core.Metadata;
using BlobServer.Core.Services;
using BlobServer.Core.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<BlobDbContext>(opt => opt.UseSqlite("Data Source=blobs.db"));
builder.Services.AddSingleton<IBlobStore>(new FileSystemBlobStore("storage"));
builder.Services.AddScoped<BlobService>();
var app = builder.Build();
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<BlobDbContext>().Database.EnsureCreated();

app.MapPut("/{container}", async (string container, BlobService service, CancellationToken ct) =>
{
    var result = await service.CreateContainerAsync(container, ct);
    if (result is true)
    {
        return Results.Created($"/{container}", null);
    }
    return Results.Conflict(new BlobError("ContainerAlreadyExists", "The specified container already exsists."));

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
        var committed = await service.CommitBlockListAsync(container, blob, blockIds, ct);
        httpContext.Response.Headers.ETag = committed.ETag;
        return Results.Ok();
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

    // None Match Header
    if (httpContext.Request.Headers.IfNoneMatch.ToString() == result.Value.Blob.ETag)
    {
        return Results.StatusCode(304);
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