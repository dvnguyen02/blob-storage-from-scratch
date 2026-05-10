namespace BlobServer.Tests;

using System.Text;
using Microsoft.AspNetCore.Http.Features;
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
    public async Task RangeRead_Returns206WithCorrectBytes()
    {
        // TODO(human): implement this test
        // 1. PUT a blob with content "Hello World"
        // 2. Build an HttpRequestMessage for GET with Range: bytes=0-4
        // 3. Send with _client.SendAsync(request)
        // 4. Assert status is 206
        // 5. Assert body is "Hello"
        var content = new StringContent("Hello World");
        await _client.PutAsync("/testcontainer/rangeread.txt", content);
        var request = new HttpRequestMessage(HttpMethod.Get, "/testcontainer/rangeread.txt");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 4);
        var response = await _client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.PartialContent, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("Hello", body);
    }

    [Fact]
    public async Task BlockBlob_StageAndCommit_ReturnsAssembledContent()
    {
        // TODO(human): implement this test
        // Stage block 1: PUT /testcontainer/assembled.txt?comp=block&blockid=block1
        //   body = "Hello "
        // Stage block 2: PUT /testcontainer/assembled.txt?comp=block&blockid=block2
        //   body = "World"
        // Commit block list: PUT /testcontainer/assembled.txt?comp=blocklist
        //   body = "<BlockList><Latest>block1</Latest><Latest>block2</Latest></BlockList>"
        // GET /testcontainer/assembled.txt
        // Assert status 200 and body "Hello World"

        var firstBlock = await _client.PutAsync("/testcontainer/assembled.txt?comp=block&blockid=block1", new StringContent("Hello "));
        var second = await _client.PutAsync("/testcontainer/assembled.txt?comp=block&blockid=block2", new StringContent("World"));
        string xmlString = "<BlockList><Latest>block1</Latest><Latest>block2</Latest></BlockList>";
        var xmlBody = new StringContent(xmlString, Encoding.UTF8, "application/xml");
        var blockList = await _client.PutAsync("/testcontainer/assembled.txt?comp=blocklist", xmlBody);
        var get = await _client.GetAsync("/testcontainer/assembled.txt");
        Assert.Equal(System.Net.HttpStatusCode.OK, get.StatusCode);
        var body = await get.Content.ReadAsStringAsync();
        Assert.Equal("Hello World", body);

    }

    [Fact]
    public async Task CommitBlockList_MissingBlock_ReturnsBadRequest()
    {
        // TODO(human): implement this test
        // Commit a block list referencing "ghost-block" which was never staged
        //   PUT /testcontainer/missing.txt?comp=blocklist
        //   body = "<BlockList><Latest>ghost-block</Latest></BlockList>"
        // Assert the response is 400 (BadRequest)
        string xmlString = "<BlockList><Latest>ghost-block</Latest></BlockList>";
        var xmlBody = new StringContent(xmlString, Encoding.UTF8, "application/xml");

        var put = await _client.PutAsync("/testcontainer/missing.txt?comp=blocklist", xmlBody);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, put.StatusCode);
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