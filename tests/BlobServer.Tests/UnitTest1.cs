namespace BlobServer.Tests;

using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using BlobServer.Core.Security;
using FluentAssertions;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using BlobServer.Core.Errors;
using Microsoft.VisualStudio.TestPlatform.Common.Utilities;

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
        var hash = request.Content?.Headers.ContentMD5;
        var contentMd5 = hash is not null ? Convert.ToBase64String(hash) : "";

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
        await SignedPutAsync("/testcontainer/rangeread.txt", content);
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

    [Fact]
    public async Task HeadExistingBlobReturnsHeadersNoBody()
    {
        var content = new StringContent("head test", Encoding.UTF8, "test/plain");
        byte[] expectedBytes = Encoding.UTF8.GetBytes("head test");
        var put = await SignedPutAsync("/testcontainer/headtest.txt", content);
        var head = await SignedHeadAsync("/testcontainer/headtest.txt");
        Assert.Equal(System.Net.HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(expectedBytes.Length, head.Content.Headers.ContentLength);
        Assert.NotNull(head.Headers.ETag);
        var bodyBytes = await head.Content.ReadAsByteArrayAsync();
        Assert.Empty(bodyBytes);
    }

    [Fact]
    public async Task HeadMissingBlobReturns404()
    {
        var head = await SignedHeadAsync("/notexistingcontainer67/notexistingblob67");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, head.StatusCode);
    }

    [Fact]
    public async Task DeleteMissingContainerReturns404()
    {
        var del = await SignedDeleteAsync("/deletemissing"); // not exisiting container
        Assert.Equal(System.Net.HttpStatusCode.NotFound, del.StatusCode);
    }

    [Fact]
    public async Task DeleteNotEmptyContainerReturns409()
    {
        await SignedPutAsync("/deletenotempty/someblob.txt", new StringContent("delete not empty container.", Encoding.UTF8, "text/plain"));
        var del = await SignedDeleteAsync("/deletenotempty");
        Assert.Equal(System.Net.HttpStatusCode.Conflict, del.StatusCode);

    }

    [Fact]
    public async Task DeleteEmptyContainerReturns204()
    {
        await SignedPutAsync("/deleteempty", new StringContent(""));
        var del = await SignedDeleteAsync("/deleteempty");
        Assert.Equal(System.Net.HttpStatusCode.NoContent, del.StatusCode);
    }
    [Fact]
    public async Task IfModifedSinceFutureReturns304()
    {
        await SignedPutAsync("/ifmodfuture/returns304.txt", new StringContent("hello"));
        var request = new HttpRequestMessage(HttpMethod.Get, "/ifmodfuture/returns304.txt");
        request.Headers.IfModifiedSince = DateTimeOffset.UtcNow.AddHours(1);
        SignRequest(request);
        var result = await _client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.NotModified, result.StatusCode);
    }

    [Fact]
    public async Task IfModifiedSincePastReturns200()
    {
        await SignedPutAsync("/ifmodsincepast/returns200.txt", new StringContent("ifmodsincepast"));
        var request = new HttpRequestMessage(HttpMethod.Get, "/ifmodsincepast/returns200.txt");
        request.Headers.IfModifiedSince = DateTimeOffset.UtcNow.AddHours(-1);
        SignRequest(request);
        var result = await _client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.OK, result.StatusCode);

    }

    [Fact]
    public async Task MalformedIfModifiedSinceReturns200()
    {
        await SignedPutAsync("/malformedifmodifiedsicne/returns200.txt", new StringContent("malformed"));
        var request = new HttpRequestMessage(HttpMethod.Get, "/malformedifmodifiedsicne/returns200.txt");
        request.Headers.TryAddWithoutValidation("If-Modified-Since", "not-a-date");
        SignRequest(request);
        var result = await _client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.OK, result.StatusCode);
    }

    [Fact]
    public async Task IfNoneMatchWinsOverIfModifiedSince()
    {
        await SignedPutAsync("/ifnonematchwinsovermodifiedsince/toolongbruh.txt", new StringContent("something"));
        var request = new HttpRequestMessage(HttpMethod.Get, "/ifnonematchwinsovermodifiedsince/toolongbruh.txt");
        request.Headers.IfModifiedSince = DateTimeOffset.UtcNow.AddHours(1); // normally this would return 304
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"wrong-etag\""));
        SignRequest(request);
        var result = await _client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.OK, result.StatusCode);


    }

    [Fact]
    public async Task PutBlobWithXmsMeta()
    {
        var url = "/metacontainer/metablob.txt";

        // Build the PUT manually so we can attach x-ms-meta-* headers.
        // SignedPutAsync doesn't expose a way to add extra headers, so we
        // do the same dance it does: build, sign, send.
        var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StringContent("hello", Encoding.UTF8, "text/plain")
        };
        request.Headers.TryAddWithoutValidation("x-ms-meta-author", "Alice");
        request.Headers.TryAddWithoutValidation("x-ms-meta-purpose", "testing");
        SignRequest(request);
        var put = await _client.SendAsync(request);
        put.EnsureSuccessStatusCode();

        // GET it back and assert the headers come back on the response.
        var get = await SignedGetAsync(url);
        get.EnsureSuccessStatusCode();

        // Headers.GetValues returns IEnumerable<string> — .First() pulls the one value.
        get.Headers.GetValues("x-ms-meta-author").First().Should().Be("Alice");
        get.Headers.GetValues("x-ms-meta-purpose").First().Should().Be("testing");
    }

    [Fact]
    public async Task HeadBlobWithXmsMeta()
    {
        var url = "/metacontainer/metahead.txt";

        var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StringContent("hello", Encoding.UTF8, "text/plain")
        };
        request.Headers.TryAddWithoutValidation("x-ms-meta-author", "Bob");
        SignRequest(request);
        (await _client.SendAsync(request)).EnsureSuccessStatusCode();

        var head = await SignedHeadAsync(url);
        head.EnsureSuccessStatusCode();

        head.Headers.GetValues("x-ms-meta-author").First().Should().Be("Bob");
    }

    [Fact]
    public async Task PutBlobWithValidContentMd5()
    {
        var content = new StringContent("hello", Encoding.UTF8, "text/plain");
        string path = "/md5container/valid.txt";
        var bodyBytes = Encoding.UTF8.GetBytes("hello");
        var md5Hash = MD5.HashData(bodyBytes);
        var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = content
        };
        request.Content.Headers.ContentMD5 = md5Hash;
        SignRequest(request);
        var put = await _client.SendAsync(request);
        put.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task WrongContentMd5Returns400()
    {
        var content = new StringContent("hello", Encoding.UTF8, "text/plain");
        string path = "/md5container/invalid.txt";
        var wrongHash = MD5.HashData(Encoding.UTF8.GetBytes("somethingelse"));
        var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = content
        };
        request.Content.Headers.ContentMD5 = wrongHash;
        SignRequest(request);
        var put = await _client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, put.StatusCode);
        var body = await put.Content.ReadAsStringAsync();
        Assert.Contains("Md5Mismatch", body);
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