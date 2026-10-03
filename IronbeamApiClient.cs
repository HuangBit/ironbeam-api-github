using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace IronbeamApiSample
{
    /// <summary>
    /// Sanitized extraction of the Ironbeam API calling layer.
    /// No credentials, trading strategy, or UI code is included.
    /// </summary>
    public sealed class IronbeamApiClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private readonly string _wsBaseUrl;

        private string _sessionToken;
        private string _streamId;

        public IronbeamApiClient(
            string baseUrl = "https://live.ironbeamapi.com",
            string wsBaseUrl = "wss://live.ironbeamapi.com/v2")
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _wsBaseUrl = wsBaseUrl.TrimEnd('/');
            _http = new HttpClient();
        }

        public string SessionToken => _sessionToken;
        public string StreamId => _streamId;

        public async Task<string> AuthenticateAsync(
            string username,
            string password,
            string apiKey,
            CancellationToken cancellationToken = default)
        {
            var payload = new
            {
                Username = username,
                Password = password,
                ApiKey = apiKey
            };

            using var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            using var response = await _http.PostAsync(
                _baseUrl + "/v2/auth",
                content,
                cancellationToken);

            var body = await response.Content.ReadAsStringAsync();

            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(body);

            _sessionToken = doc.RootElement
                .GetProperty("token")
                .GetString();

            return _sessionToken;
        }

        public async Task<string> CreateStreamAsync(
            CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                _baseUrl + "/v2/stream/create");

            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _sessionToken);

            using var response = await _http.SendAsync(
                request,
                cancellationToken);

            var body = await response.Content.ReadAsStringAsync();

            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.TryGetProperty("status", out var status) &&
                status.GetString() != "OK")
            {
                throw new InvalidOperationException(body);
            }

            _streamId = doc.RootElement
                .GetProperty("streamId")
                .GetString();

            return _streamId;
        }

        public async Task SubscribeQuotesAsync(
            string exchangeSymbol,
            CancellationToken cancellationToken = default)
        {
            await SubscribeMarketAsync(
                "/v2/market/quotes/subscribe/",
                exchangeSymbol,
                cancellationToken);
        }

        public async Task SubscribeDepthsAsync(
            string exchangeSymbol,
            CancellationToken cancellationToken = default)
        {
            await SubscribeMarketAsync(
                "/v2/market/depths/subscribe/",
                exchangeSymbol,
                cancellationToken);
        }

        public async Task SubscribeTradesAsync(
            string exchangeSymbol,
            CancellationToken cancellationToken = default)
        {
            await SubscribeMarketAsync(
                "/v2/market/trades/subscribe/",
                exchangeSymbol,
                cancellationToken);
        }

        private async Task SubscribeMarketAsync(
            string endpoint,
            string exchangeSymbol,
            CancellationToken cancellationToken)
        {
            EnsureAuthenticated();
            EnsureStream();

            var url = _baseUrl + endpoint + Uri.EscapeDataString(_streamId)
                      + "?symbols=" + Uri.EscapeDataString(exchangeSymbol);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _sessionToken);
            request.Headers.UserAgent.ParseAdd("IronbeamApiSample/1.0");

            using var response = await _http.SendAsync(
                request,
                cancellationToken);

            var body = await response.Content.ReadAsStringAsync();

            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.TryGetProperty("status", out var status) &&
                status.GetString() != "OK")
            {
                throw new InvalidOperationException(body);
            }
        }

        public async Task<string> GetQuotesAsync(
            string exchangeSymbol,
            CancellationToken cancellationToken = default)
        {
            return await GetMarketDataAsync(
                "/v2/market/quotes?symbols=",
                exchangeSymbol,
                cancellationToken);
        }

        public async Task<string> GetDepthAsync(
            string exchangeSymbol,
            CancellationToken cancellationToken = default)
        {
            return await GetMarketDataAsync(
                "/v2/market/depth?symbols=",
                exchangeSymbol,
                cancellationToken);
        }

        public async Task<string> GetTradesAsync(
            string exchangeSymbol,
            long fromUnixMs,
            long toUnixMs,
            int max = 100,
            bool earlier = false,
            CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            var url = _baseUrl +
                      "/v2/market/trades/" +
                      Uri.EscapeDataString(exchangeSymbol) + "/" +
                      fromUnixMs + "/" +
                      toUnixMs + "/" +
                      max + "/" +
                      earlier.ToString().ToLowerInvariant();

            using var request = CreateAuthenticatedRequest(HttpMethod.Get, url);

            using var response = await _http.SendAsync(
                request,
                cancellationToken);

            var body = await response.Content.ReadAsStringAsync();

            response.EnsureSuccessStatusCode();
            return body;
        }

        private async Task<string> GetMarketDataAsync(
            string endpoint,
            string exchangeSymbol,
            CancellationToken cancellationToken)
        {
            EnsureAuthenticated();

            var url = _baseUrl +
                      endpoint +
                      Uri.EscapeDataString(exchangeSymbol);

            using var request = CreateAuthenticatedRequest(HttpMethod.Get, url);

            using var response = await _http.SendAsync(
                request,
                cancellationToken);

            var body = await response.Content.ReadAsStringAsync();

            response.EnsureSuccessStatusCode();
            return body;
        }

        public async Task<string> PlaceOrderAsync(
            string accountId,
            string exchangeSymbol,
            double quantity,
            string side,
            string orderType,
            double limitPrice,
            CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            var url = _baseUrl + "/v2/order/" +
                      Uri.EscapeDataString(accountId) + "/place";

            var payload = new
            {
                accountId = accountId,
                exchSym = exchangeSymbol,
                side = side,             // BUY / SELL
                quantity = quantity,
                limitPrice = limitPrice,
                orderType = orderType,   // MARKET / LIMIT / STOP / ...
                duration = "0",          // example from source project
                waitForOrderId = true
            };

            using var request = CreateAuthenticatedRequest(HttpMethod.Post, url);
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            using var response = await _http.SendAsync(
                request,
                cancellationToken);

            var body = await response.Content.ReadAsStringAsync();

            response.EnsureSuccessStatusCode();
            return body;
        }

        public async Task<List<string>> GetNewOrderIdsAsync(
            string accountId,
            CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            var url = _baseUrl + "/v2/order/" +
                      Uri.EscapeDataString(accountId) + "/NEW";

            using var request = CreateAuthenticatedRequest(HttpMethod.Get, url);

            using var response = await _http.SendAsync(
                request,
                cancellationToken);

            var body = await response.Content.ReadAsStringAsync();

            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(body);

            var result = new List<string>();

            if (!doc.RootElement.TryGetProperty("orders", out var orders))
                return result;

            foreach (var order in orders.EnumerateArray())
            {
                if (order.TryGetProperty("status", out var status) &&
                    status.GetString() == "NEW" &&
                    order.TryGetProperty("orderId", out var orderId))
                {
                    result.Add(orderId.GetString());
                }
            }

            return result;
        }

        public async Task<string> CancelMultipleOrdersAsync(
            string accountId,
            IEnumerable<string> orderIds,
            CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            var url = _baseUrl + "/v2/order/" +
                      Uri.EscapeDataString(accountId) +
                      "/cancelMultiple";

            var payload = new
            {
                accountId = accountId,
                orderIds = orderIds
            };

            using var request = CreateAuthenticatedRequest(HttpMethod.Delete, url);
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            using var response = await _http.SendAsync(
                request,
                cancellationToken);

            var body = await response.Content.ReadAsStringAsync();

            response.EnsureSuccessStatusCode();
            return body;
        }

        public async Task<string> LogoutAsync(
            CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            var payload = new
            {
                token = _sessionToken
            };

            using var request = CreateAuthenticatedRequest(
                HttpMethod.Post,
                _baseUrl + "/v2/logout");

            request.Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            using var response = await _http.SendAsync(
                request,
                cancellationToken);

            var body = await response.Content.ReadAsStringAsync();

            response.EnsureSuccessStatusCode();

            _sessionToken = null;
            _streamId = null;

            return body;
        }

        public ClientWebSocket CreateStreamWebSocket()
        {
            EnsureAuthenticated();
            EnsureStream();

            // The source project connects to:
            // wss://.../v2/stream/{streamId}
            var ws = new ClientWebSocket();

            // Keep the token out of source-code URLs.
            // If the API environment requires an Authorization header
            // for the WebSocket handshake, configure it here.
            ws.Options.SetRequestHeader(
                "Authorization",
                "Bearer " + _sessionToken);

            return ws;
        }

        public Uri GetStreamWebSocketUri()
        {
            EnsureStream();
            return new Uri(_wsBaseUrl + "/stream/" + _streamId);
        }

        private HttpRequestMessage CreateAuthenticatedRequest(
            HttpMethod method,
            string url)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _sessionToken);
            return request;
        }

        private void EnsureAuthenticated()
        {
            if (string.IsNullOrEmpty(_sessionToken))
                throw new InvalidOperationException("Authenticate first.");
        }

        private void EnsureStream()
        {
            if (string.IsNullOrEmpty(_streamId))
                throw new InvalidOperationException("CreateStreamAsync first.");
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }
}
