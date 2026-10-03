using System.Net;
using System.Text.RegularExpressions;

namespace Mailserver.Tests;

/// <summary>Browser stand-in for the web interface: keeps cookies and submits forms with their antiforgery token.</summary>
public sealed partial class WebClient : IDisposable
{
    private readonly HttpClient _http;

    public WebClient(int port)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{port}") };
    }

    public string LastPage { get; private set; } = "";

    public async Task<HttpResponseMessage> GetAsync(string path)
    {
        var response = await _http.GetAsync(path);
        LastPage = await response.Content.ReadAsStringAsync();
        return response;
    }

    /// <summary>Loads <paramref name="formPage"/> for an antiforgery token, then posts the fields to <paramref name="action"/> and follows the redirect.</summary>
    public async Task<HttpResponseMessage> PostAsync(string formPage, string action, params (string Name, string Value)[] fields)
    {
        await GetAsync(formPage);
        var token = Token().Match(LastPage);
        var values = fields.ToList();
        if (token.Success)
        {
            values.Add(("__RequestVerificationToken", WebUtility.HtmlDecode(token.Groups[1].Value)));
        }

        var response = await _http.PostAsync(action, new FormUrlEncodedContent(values.Select(v => new KeyValuePair<string, string>(v.Name, v.Value))));
        LastPage = await response.Content.ReadAsStringAsync();
        if (response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location is { } location)
        {
            await GetAsync(location.ToString());
        }

        return response;
    }

    /// <summary>Like <see cref="PostAsync"/>, as multipart/form-data with files.</summary>
    public async Task<HttpResponseMessage> PostMultipartAsync(string formPage, string action, (string Name, string Value)[] fields,
        params (string Name, string FileName, byte[] Content)[] files)
    {
        await GetAsync(formPage);
        var token = Token().Match(LastPage);
        using var content = new MultipartFormDataContent();
        foreach (var (name, value) in fields)
        {
            content.Add(new StringContent(value), name);
        }

        if (token.Success)
        {
            content.Add(new StringContent(WebUtility.HtmlDecode(token.Groups[1].Value)), "__RequestVerificationToken");
        }

        foreach (var (name, fileName, bytes) in files)
        {
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
            content.Add(file, name, fileName);
        }

        var response = await _http.PostAsync(action, content);
        LastPage = await response.Content.ReadAsStringAsync();
        if (response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location is { } location)
        {
            await GetAsync(location.ToString());
        }

        return response;
    }

    public async Task<byte[]> GetBytesAsync(string path) => await _http.GetByteArrayAsync(path);

    public async Task LoginAsync(string email, string password)
    {
        var response = await PostAsync("/Login", "/Login", ("email", email), ("password", password));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    public Task<HttpResponseMessage> PostRawAsync(string action, params (string Name, string Value)[] fields) =>
        _http.PostAsync(action, new FormUrlEncodedContent(fields.Select(v => new KeyValuePair<string, string>(v.Name, v.Value))));

    public void Dispose() => _http.Dispose();

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex Token();
}
