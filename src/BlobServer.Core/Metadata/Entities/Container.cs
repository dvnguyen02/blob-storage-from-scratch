namespace BlobServer.Core.Metadata.Entities;


public class Container
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public DateTime CreatedAt { get; set; }
}