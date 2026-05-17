namespace BlobServer.Tests;

using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using BlobServer.Core.Security;
public class BLobEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    private const string AccountName = "devaccount";
    private const string SharedKey = "DL4FvS1eXaGCSCHIYiB5RuEHy0+6zxEr0KBdG/lWbb8=";

    public BLobEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    private void SignRequest(HttpRequestMessage request)
    {
        // 1. Set the Date header on the request: request.Headers.Date = DateTimeOffset.UtcNow;
        //    (use this same value for the canonical string below — fetch it back as a string)
        // 2. Get the values you need from the request:
        //      method = request.Method.Method
        //      contentMd5 = "" (we don't use it)
        //      contentType = request.Content?.Headers.ContentType?.ToString() ?? ""
        //      date = request.Headers.Date?.ToString("R") ?? ""
        //      resource = request.RequestUri!.AbsolutePath
        // 3. Build canonical string with HmacSignatureCalculator.BuildCanonicalString
        // 4. Compute the HMAC: HmacSignatureCalculator.Compute(canonical, SharedKey)
        // 5. Set the Authorization header:
        //      request.Headers.Authorization =
        //          new System.Net.Http.Headers.AuthenticationHeaderValue("SharedKey", $"{AccountName}:{sig}");
        request.Headers.Date = DateTimeOffset.UtcNow;
        var method = request.Method.Method;
        var contentMd5 = "";
        string contentType = request.Content?.Headers.ContentType?.ToString() ?? "";
        var date = request.Headers.Date?.ToString("R") ?? "";
        var uriString = request.RequestUri!.ToString();
        var resource = uriString.Contains('?') ? uriString[..uriString.IndexOf('?')] : uriString;
        var canonicalString = HmacSignatureCalculator.BuildCanonicalString(method, contentMd5, contentType, date, resource);
        var sig = HmacSignatureCalculator.Compute(canonicalString, SharedKey);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("SharedKey", $"{AccountName}:{sig}");

    }


    [Fact]
    public async Task PutThenGet_ReturnUploadedContent()
    {
        var content = new StringContent("hello from test", System.Text.Encoding.UTF8, "text/plain");
        var put = await SignedPutAsync("/testcontainer/hello.txt", content);
        put.EnsureSuccessStatusCode();

        var get = await SignedGetAsync("/testcontainer/hello.txt");
        get.EnsureSuccessStatusCode();

        var body = await get.Content.ReadAsStringAsync();
        Assert.Equal("hello from test", body);
    }

    [Fact]
    public async Task GetMissingBlob()
    {
        var respond = await SignedGetAsync("/testcontainer/missingblob.txt");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, respond.StatusCode);
    }

    [Fact]
    public async Task DeleteThenGet()
    {
        var content = new StringContent("to be deleted");
        await SignedPutAsync("/testcontainer/deleteme.txt", content);

        var del = await SignedDeleteAsync("/testcontainer/deleteme.txt");
        del.EnsureSuccessStatusCode();

        var response = await SignedGetAsync("/testcontainer/deleteme.txt");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RangeRead_Returns206WithCorrectBytes()
    {
        // 1. PUT a blob with content "Hello World"
        // 2. Build an HttpRequestMessage for GET with Range: bytes=0-4
        // 3. Send with _client.SendAsync(request)
        // 4. Assert status is 206
        // 5. Assert body is "Hello"
        var content = new StringContent("Hello World");
        await _client.PutAsync("/testcontainer/rangeread.txt", content);
        var request = new HttpRequestMessage(HttpMethod.Get, "/testcontainer/rangeread.txt");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 4);
        SignRequest(request);
        var response = await _client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.PartialContent, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("Hello", body);
    }

    [Fact]
    public async Task BlockBlob_StageAndCommit_ReturnsAssembledContent()
    {
        // Stage block 1: PUT /testcontainer/assembled.txt?comp=block&blockid=block1
        //   body = "Hello "
        // Stage block 2: PUT /testcontainer/assembled.txt?comp=block&blockid=block2
        //   body = "World"
        // Commit block list: PUT /testcontainer/assembled.txt?comp=blocklist
        //   body = "<BlockList><Latest>block1</Latest><Latest>block2</Latest></BlockList>"
        // GET /testcontainer/assembled.txt
        // Assert status 200 and body "Hello World"

        var firstBlock = await SignedPutAsync("/testcontainer/assembled.txt?comp=block&blockid=block1", new StringContent("Hello "));
        var second = await SignedPutAsync("/testcontainer/assembled.txt?comp=block&blockid=block2", new StringContent("World"));
        string xmlString = "<BlockList><Latest>block1</Latest><Latest>block2</Latest></BlockList>";
        var xmlBody = new StringContent(xmlString, Encoding.UTF8, "application/xml");
        var blockList = await SignedPutAsync("/testcontainer/assembled.txt?comp=blocklist", xmlBody);
        var get = await SignedGetAsync("/testcontainer/assembled.txt");
        Assert.Equal(System.Net.HttpStatusCode.OK, get.StatusCode);
        var body = await get.Content.ReadAsStringAsync();
        Assert.Equal("Hello World", body);

    }

    [Fact]
    public async Task CommitBlockList_MissingBlock_ReturnsBadRequest()
    {
        // Commit a block list referencing "ghost-block" which was never staged
        //   PUT /testcontainer/missing.txt?comp=blocklist
        //   body = "<BlockList><Latest>ghost-block</Latest></BlockList>"
        // Assert the response is 400 (BadRequest)
        string xmlString = "<BlockList><Latest>ghost-block</Latest></BlockList>";
        var xmlBody = new StringContent(xmlString, Encoding.UTF8, "application/xml");

        var put = await SignedPutAsync("/testcontainer/missing.txt?comp=blocklist", xmlBody);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task ListContainer_ReturnsUploadedBlobs()
    {
        // 1. PUT two blobs into a unique container (use "listcontainer" to avoid collisions)
        //      /listcontainer/file1.txt  body = "aaa"
        //      /listcontainer/file2.txt  body = "bbb"
        // 2. GET /listcontainer
        // 3. Assert status 200
        // 4. Read the body as a string and assert it contains "file1.txt" and "file2.txt"
        var blob1 = await SignedPutAsync("/listcontainer/file1.txt", new StringContent("aaa"));
        var blob2 = await SignedPutAsync("/listcontainer/file2.txt", new StringContent("bbb"));
        var getList = await SignedGetAsync("/listcontainer");
        Assert.Equal(System.Net.HttpStatusCode.OK, getList.StatusCode);
        var body = await getList.Content.ReadAsStringAsync();
        Assert.Contains("file1.txt", body);
        Assert.Contains("file2.txt", body);
    }

    [Fact]
    public async Task GetWithMatchingETag_Returns304()
    {
        // upload a blob
        var content = new StringContent("test content", System.Text.Encoding.UTF8, "text/plain");
        var put = await SignedPutAsync("/testcontainer/etag-test.txt", content);
        put.EnsureSuccessStatusCode();
        // grab etag from the response 
        var etag = put.Headers.ETag?.ToString();

        // get with that etag in if-none-match
        var request = new HttpRequestMessage(HttpMethod.Get, "/testcontainer/etag-test.txt");
        request.Headers.IfNoneMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue(etag!));
        SignRequest(request);
        var get = await _client.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.NotModified, get.StatusCode);
    }

    [Fact]
    public async Task Compute_SameInputs_ReturnsSameResult()
    {
        var key = Convert.ToBase64String(new byte[32]); // init 32 zero bytes 
        var result1 = HmacSignatureCalculator.Compute("GET\n\ntext/plain\nMon, 01 Jan 2024 00:00:00GMT\n/mycontainer/test.txt", key);
        var result2 = HmacSignatureCalculator.Compute("GET\n\ntext/plain\nMon, 01 Jan 2024 00:00:00GMT\n/mycontainer/test.txt", key);
        Assert.Equal(result1, result2);
    }

    [Fact]
    public async Task NoAuthReturns403()
    {
        var get = await _client.GetAsync("/testcontainer/anything.txt");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, get.StatusCode);
    }

    [Fact]
    public async Task WrongKeyReturns403()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/testcontainer/anything.txt");
        request.Headers.Date = DateTimeOffset.UtcNow;


        // Build canonical string 
        var method = request.Method.Method;
        var contentMd5 = "";
        string contentType = request.Content?.Headers.ContentType?.ToString() ?? "";
        var date = request.Headers.Date?.ToString("R") ?? "";
        var uriString = request.RequestUri!.ToString();
        var resource = uriString.Contains('?') ? uriString[..uriString.IndexOf('?')] : uriString;
        var canonicalString = HmacSignatureCalculator.BuildCanonicalString(method, contentMd5, contentType, date, resource);
        var wrongKey = Convert.ToBase64String(new byte[32]);
        var sig = HmacSignatureCalculator.Compute(canonicalString, wrongKey);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("SharedKey", $"{AccountName}:{sig}");

        var response = await _client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);

    }

    [Fact]
    public async Task SignedInSucess()
    {
        var content = new StringContent("hello from test", System.Text.Encoding.UTF8, "text/plain");
        var put = await SignedPutAsync("/textcontainer/signedinsucesstest.txt", content);
        Assert.Equal(System.Net.HttpStatusCode.OK, put.StatusCode);
    }

    [Fact]
    public async Task ValidSasReadReturns200()
    {
        var content = new StringContent("hello", Encoding.UTF8, "text/plain");
        var put = await SignedPutAsync("/testcontainer/validsas.txt", content);
        var sasUrl = BuildSasUrl("/testcontainer/validsas.txt", DateTimeOffset.UtcNow.AddMinutes(67), "r");
        var get = await _client.GetAsync(sasUrl);
        Assert.Equal(System.Net.HttpStatusCode.OK, get.StatusCode);

    }
    [Fact]
    public async Task ExpiredSasReadReturns200()
    {
        var content = new StringContent("hello", Encoding.UTF8, "text/plain");
        var put = await SignedPutAsync("/testcontainer/validsas.txt", content);
        var sasUrl = BuildSasUrl("/testcontainer/validsas.txt", DateTimeOffset.UtcNow.AddMinutes(-67), "r");
        var get = await _client.GetAsync(sasUrl);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, get.StatusCode);

    }

    [Fact]
    public async Task WritePermOnGet()
    {
        var sasURl = BuildSasUrl("/testcontainer/writeperm.txt", DateTimeOffset.Now.AddDays(67), "w");
        var get = await _client.GetAsync(sasURl);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, get.StatusCode);

    }


    ///
    /// 
    /// HELPER
    /// 
    /// 
    public string BuildSasUrl(string path, DateTimeOffset expiry, string permission)
    {
        var expiryStr = expiry.ToString("o");
        var version = "2026-01-01";
        var canonical = $"{path}\n{expiryStr}\n{permission}\n{version}";
        var sig = HmacSignatureCalculator.Compute(canonical, SharedKey);
        return $"{path}?se={Uri.EscapeDataString(expiryStr)}&sp={permission}&sv={version}&sig={Uri.EscapeDataString(sig)}";

    }
    private async Task<HttpResponseMessage> SignedPutAsync(string url, HttpContent content)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
        SignRequest(req);
        return await _client.SendAsync(req);
    }

    private async Task<HttpResponseMessage> SignedGetAsync(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        SignRequest(req);
        return await _client.SendAsync(req);
    }
    private async Task<HttpResponseMessage> SignedDeleteAsync(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, url);
        SignRequest(req);
        return await _client.SendAsync(req);
    }

    private async Task<HttpResponseMessage> SignedHeadAsync(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Head, url);
        SignRequest(req);
        return await _client.SendAsync(req);

    }

}