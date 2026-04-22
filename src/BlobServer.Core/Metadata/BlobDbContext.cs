namespace BlobServer.Core.Metadata;

using BlobServer.Core.Metadata.Entities;
using Microsoft.EntityFrameworkCore;

public class BlobDbContext : DbContext
{
    public BlobDbContext(DbContextOptions<BlobDbContext> options) : base(options) { }
    public DbSet<Container> Containers => Set<Container>();
    public DbSet<Blob> Blobs => Set<Blob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // TODO: configure unique index on (ContainerID, Name) so that no similar containerid and similar name happen
        modelBuilder.Entity<Blob>()
                .HasIndex(b => new { b.ContainerId, b.Name })
                .IsUnique();
    }


}


