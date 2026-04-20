public class Blob
{
    public int Id { get; set; }
    public int ContainerId { get; set; }
    public Container Container { get; set; }
    public string Name { get; set; }
    public long Size { get; set; }
    public string ContentType { get; set; }
    public string ETag { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
}