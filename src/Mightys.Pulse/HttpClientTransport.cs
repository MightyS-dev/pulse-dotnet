using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Mightys.Pulse
{
    /// <summary>HTTP transport. Owns its handler; does not recompress or mutate the core's wire bytes.</summary>
    public sealed class HttpClientTransport : ITransport, IDisposable
    {
        private readonly HttpClient client;
        /// <summary>Creates the transport. Redirects, cookies and automatic decompression are off by default.
        /// <param name="handler">Optional handler, for tests.</param></summary>
        public HttpClientTransport(HttpMessageHandler handler = null)
        {
            client = new HttpClient(handler ?? new HttpClientHandler
            {
                AllowAutoRedirect = false, UseCookies = false,
                AutomaticDecompression = DecompressionMethods.None
            }) { Timeout = Timeout.InfiniteTimeSpan };
        }

        /// <summary>Sends one request and returns the response.</summary>
        public async Task<TransportResponse> SendAsync(TransportRequest request, CancellationToken cancellationToken)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
                using var message = new HttpRequestMessage(HttpMethod.Post, request.Url);
                message.Headers.Add("X-App-Key", request.AppKey);
                message.Content = new ByteArrayContent(request.Body);
                message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                if (request.IsGzip) message.Content.Headers.ContentEncoding.Add("gzip");
                using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token).ConfigureAwait(false);
                var retry = response.Headers.TryGetValues("Retry-After", out var values) ? values.FirstOrDefault() : null;
                var body = await ReadAdvice(response.Content, timeout.Token).ConfigureAwait(false);
                return new TransportResponse((int)response.StatusCode, body, retry);
            }
            catch (Exception) { return new TransportResponse(0); }
        }

        private static async Task<string> ReadAdvice(HttpContent content, CancellationToken cancellationToken)
        {
            if (content == null || content.Headers.ContentLength > 65536) return "";
            using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                if (count == 0) return Encoding.UTF8.GetString(output.ToArray());
                if (output.Length + count > 65536) return "";
                output.Write(buffer, 0, count);
            }
        }

        /// <summary>Releases the HTTP client.</summary>
        public void Dispose() => client.Dispose();
    }
}
