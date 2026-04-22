using BlobServer.Core.Metadata;
using BlobServer.Core.Services;
using BlobServer.Core.Storage;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<BlobDbContext>(opt => opt.UseSqlite("Data Source=blobs.db"));
builder.Services.AddSingleton<IBlobStore>(new FileSystemBlobStore("storage"));
builder.Services.AddScoped<BlobService>();
var app = builder.Build();
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<BlobDbContext>().Database.EnsureCreated();

app.MapPut("/{container}/{blob}", async (string container, string blob, HttpRequest request, BlobService service, CancellationToken ct) =>
{
    var contentType = request.ContentType;
    await service.PutAsync(container, blob, request.Body, contentType, ct);
    return Results.Ok();
});

app.MapGet("/{container}/{blob}", async (string container, string blob, BlobService service, CancellationToken ct) =>
{
    var result = await service.GetAsync(container, blob, ct);
    if (result is null)
    {
        return Results.NotFound();
    }
    return Results.Stream(result.Value.BlobStream, result.Value.Blob.ContentType);
});

app.MapDelete("/{container}/{blob}", async (string container, string blob, BlobService service, CancellationToken ct) =>
{

    var deleted = await service.DeleteAsync(container, blob, ct);
    return deleted ? Results.NoContent() : Results.NotFound();

});

app.Run();