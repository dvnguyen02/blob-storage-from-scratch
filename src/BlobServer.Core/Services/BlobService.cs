namespace BlobServer.Core.Services;

using System.Security.Cryptography;
using BlobServer.Core.Metadata;
using BlobServer.Core.Metadata.Entities;
using BlobServer.Core.Storage;
using Microsoft.EntityFrameworkCore;
public class BlobService
{
    private readonly BlobDbContext db;
    private readonly IBlobStore store;
    public BlobService(BlobDbContext db, IBlobStore store)
    {
        this.db = db;
        this.store = store;
    }

    public async Task<Blob> PutAsync(string container, string name, Stream content, string? contentType, CancellationToken ct)
    {
        var containerRow = await db.Containers.FirstOrDefaultAsync(c => c.Name == container, ct);
        if (containerRow is null)
        {
            containerRow = new Container { Name = container, CreatedAt = DateTime.UtcNow };
            db.Containers.Add(containerRow);
            await db.SaveChangesAsync(ct);
        }
        // Read content into memory first, then get the size
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        var size = buffer.Length;

        var etag = MD5.HashData(buffer.ToArray());
        string base64 = $"\"{Convert.ToBase64String(etag)}\"";


        await store.WriteAsync(container, name, buffer, ct);
        var blobRow = await db.Blobs.FirstOrDefaultAsync(b => b.ContainerId == containerRow.Id && b.Name == name, ct);
        if (blobRow is null)
        {
            blobRow = new Blob
            {
                ContainerId = containerRow.Id,
                Name = name,
                Size = size,
                ContentType = contentType,
                ETag = base64,
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow
            };
            db.Blobs.Add(blobRow);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            // update if the blob row already exsists
            blobRow.Size = size;
            blobRow.ETag = base64;
            blobRow.ContentType = contentType;
            blobRow.ModifiedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return blobRow;

    }
    public async Task<(Blob Blob, Stream BlobStream)?> GetAsync(string container, string name, CancellationToken ct)
    {
        var containerRow = await db.Containers.FirstOrDefaultAsync(c => c.Name == container, ct);
        if (containerRow is null)
        {
            return null;
        }
        var blobRow = await db.Blobs.FirstOrDefaultAsync(b => b.ContainerId == containerRow.Id && b.Name == name, ct);
        if (blobRow is null)
        {
            return null;
        }
        var stream = await store.OpenReadAsync(container, name, ct);
        if (stream is null) { return null; }
        return (blobRow, stream);
    }

    // public async Task<List<string>> ListAsync(string container, CancellationToken ct)
    // {
    //     var containerRow = await db.Containers.FirstOrDefaultAsync(c => c.Name == container, ct);
    //     if (containerRow is null)
    //     {
    //         return null;
    //     }
    //     var blobRows = db.Blobs.FirstOrDefaultAsync(b => b.ContainerId == containerRow.Id);
    //     List<string> blobNames = new List<string>();
    //     foreach (row in blobRows)
    //     {
    //         blobNames.Add(row.Name);
    //     }
    //     return blobNames;

    // }

    public async Task<bool> DeleteAsync(string container, string name, CancellationToken ct)
    {
        var containerRow = await db.Containers.FirstOrDefaultAsync(c => c.Name == container, ct);
        if (containerRow is null) { return false; }
        var blobRow = await db.Blobs.FirstOrDefaultAsync(b => b.Name == name && b.ContainerId == containerRow.Id, ct);
        if (blobRow is null) { return false; }
        db.Blobs.Remove(blobRow);
        await db.SaveChangesAsync(ct);
        await store.DeleteAsync(container, name, ct);
        return true;

    }
}