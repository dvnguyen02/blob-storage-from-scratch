namespace BlobServer.Core.Errors;

public record BlobError(string Code, string Message);
public class Md5MismatchException : Exception
{
    public Md5MismatchException(string message) : base(message) { }
}