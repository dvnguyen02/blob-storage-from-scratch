namespace BlobServer.Tests;

using Microsoft.AspNetCore.Mvc.Testing;
public class BLobEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public BLobEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }
    [Fact]
    public async Task PutThenGet_ReturnUploadedContent()
    {
        var content = new StringContent("hello from test", System.Text.Encoding.UTF8, "text/plain");
        var put = await _client.PutAsync("/testcontainer/hello.txt", content);
        put.EnsureSuccessStatusCode();

        var get = await _client.GetAsync("/testcontainer/hello.txt");
        get.EnsureSuccessStatusCode();

        var body = await get.Content.ReadAsStringAsync();
        Assert.Equal("hello from test", body);
    }

    [Fact]
    public async Task GetMissingBlob()
    {
        var respond = await _client.GetAsync("/testcontainer/missingblob.txt");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, respond.StatusCode);
    }

    [Fact]
    public async Task DeleteThenGet()
    {
        var content = new StringContent("to be deleted");
        await _client.PutAsync("/testcontainer/deleteme.txt", content);

        var del = await _client.DeleteAsync("/testcontainer/deleteme.txt");
        del.EnsureSuccessStatusCode();

        var response = await _client.GetAsync("/testcontainer/deleteme.txt");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetWithMatchingETag_Returns304()
    {
        // upload a blob
        var content = new StringContent("test content", System.Text.Encoding.UTF8, "text/plain");
        var put = await _client.PutAsync("/testcontainer/etag-test.txt", content);
        put.EnsureSuccessStatusCode();
        // grab etag from the response 
        var etag = put.Headers.ETag?.ToString();

        // get with that etag in if-none-match
        var request = new HttpRequestMessage(HttpMethod.Get, "/testcontainer/etag-test.txt");
        request.Headers.IfNoneMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue(etag!));
        var get = await _client.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.NotModified, get.StatusCode);


    }
}