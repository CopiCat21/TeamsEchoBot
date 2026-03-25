namespace TeamsEchoBot.Helpers;

public static class HttpExtensions
{
    public static async Task<HttpRequestMessage> ToHttpRequestMessageAsync(this HttpRequest request)
    {
        var uriBuilder = new UriBuilder
        {
            Scheme = request.Scheme,
            Host   = request.Host.Host,
            Port   = request.Host.Port ?? (request.IsHttps ? 443 : 80),
            Path   = request.Path.ToString(),
            Query  = request.QueryString.ToString(),
        };

        var httpRequest = new HttpRequestMessage
        {
            Method     = new HttpMethod(request.Method),
            RequestUri = uriBuilder.Uri,
        };

        if (request.ContentLength > 0 || request.Body.CanRead)
        {
            var body = new MemoryStream();
            await request.Body.CopyToAsync(body).ConfigureAwait(false);
            body.Seek(0, SeekOrigin.Begin);
            httpRequest.Content = new StreamContent(body);
        }

        foreach (var (key, values) in request.Headers)
        {
            var headerValue = values.ToArray();

            if (key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            {
                httpRequest.Content?.Headers.TryAddWithoutValidation(key, headerValue);
            }
            else
            {
                httpRequest.Headers.TryAddWithoutValidation(key, headerValue);
            }
        }

        return httpRequest;
    }

    public static async Task CopyToHttpResponseAsync(
        this HttpResponseMessage responseMessage,
        HttpResponse response)
    {
        response.StatusCode = (int)responseMessage.StatusCode;

        foreach (var (key, values) in responseMessage.Headers)
        {
            if (key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                continue;
            response.Headers[key] = values.ToArray();
        }

        // Copy content headers and body
        if (responseMessage.Content != null)
        {
            foreach (var (key, values) in responseMessage.Content.Headers)
                response.Headers[key] = values.ToArray();

            await responseMessage.Content.CopyToAsync(response.Body).ConfigureAwait(false);
        }
    }
}
