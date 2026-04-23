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
}