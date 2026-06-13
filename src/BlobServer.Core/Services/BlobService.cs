namespace BlobServer.Core.Services;

using System.Security.Cryptography;
using System.Text.Json;
using BlobServer.Core.Metadata;
using BlobServer.Core.Metadata.Entities;
using BlobServer.Core.Storage;
using BlobServer.Core.Errors;
using Microsoft.EntityFrameworkCore;

public enum DeleteContainerResult { Deleted, NotFound, NotEmpty }
public class BlobService
{
    private readonly BlobDbContext db;
    private readonly IBlobStore store;
    public BlobService(BlobDbContext db, IBlobStore store)
    {
        this.db = db;
        this.store = store;
    }

    public async Task<Blob> PutAsync(string container, string name, Stream content,
                                    string? contentType, Dictionary<string, string>? metadata,
                                    string? expectedMd5,
                                    CancellationToken ct)
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
        if (expectedMd5 is not null && expectedMd5 != "")
        {
            var actualMd5 = Convert.ToBase64String(etag);
            if (actualMd5 != expectedMd5)
            {
                throw new Md5MismatchException("The Content-MD5 header did not match the body's MD5.");
            }
        }
        string? metadataJson;
        if (metadata is null || metadata.Count == 0)
        {
            metadataJson = null;
        }
        else
        {
            metadataJson = JsonSerializer.Serialize(metadata);
        }
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
                ModifiedAt = DateTime.UtcNow,
                Metadata = metadataJson
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
            blobRow.Metadata = metadataJson;
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

    public async Task<List<Blob>?> ListAsync(string container, CancellationToken ct)
    {
        var containerRow = await db.Containers.FirstOrDefaultAsync(c => c.Name == container, ct);
        if (containerRow is null)
        {
            return null;
        }
        var blobRows = await db.Blobs.Where(b => b.ContainerId == containerRow.Id).ToListAsync(ct);
        return blobRows;

    }

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

    public async Task<bool> CreateContainerAsync(string name, CancellationToken ct)
    {
        var containerRow = await db.Containers.FirstOrDefaultAsync(c => c.Name == name, ct);
        if (containerRow is not null)
        {
            return false;
        }
        var container = new Container { Name = name, CreatedAt = DateTime.UtcNow };
        await db.Containers.AddAsync(container);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<DeleteContainerResult> DeleteContainerAsync(string name, CancellationToken ct)
    {
        var containerRow = await db.Containers.FirstOrDefaultAsync(c => c.Name == name, ct);
        if (containerRow is null)
        {
            return DeleteContainerResult.NotFound;
        }

        var hasBlobs = await db.Blobs.AnyAsync(b => b.ContainerId == containerRow.Id, ct);
        if (hasBlobs)
        {
            return DeleteContainerResult.NotEmpty;
        }

        db.Containers.Remove(containerRow);
        await db.SaveChangesAsync(ct);
        return DeleteContainerResult.Deleted;
    }

    public async Task StageBlockAsync(string container, string blob, string blockId, Stream content, CancellationToken ct)
    {
        await store.WriteBlockAsync(container, blob, blockId, content, ct);
    }

    public async Task<Blob> CommitBlockListAsync(string container, string blob, List<string> blockIds, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        foreach (var blockId in blockIds)
        {
            var blockStream = await store.OpenBlockAsync(container, blob, blockId, ct);
            if (blockStream is null)
                throw new InvalidOperationException($"Block '{blockId}' not found.");
            await blockStream.CopyToAsync(buffer, ct);
        }
        buffer.Position = 0;
        return await PutAsync(container, blob, buffer, null, null, null, ct);
    }

    ////////////////// HELPER /////////////////////   
    public async Task<string?> GetBlobTagAsync(string name, string container, CancellationToken ct)
    {
        var containerRow = await db.Containers.FirstOrDefaultAsync(c => c.Name == container, ct);
        if (containerRow is null)
        {
            return null;
        }
        var blobRow = await db.Blobs.FirstOrDefaultAsync(b => b.Name == name && b.ContainerId == containerRow.Id, ct);
        if (blobRow is null)
        {
            return null;
        }
        return blobRow.ETag;
    }
}