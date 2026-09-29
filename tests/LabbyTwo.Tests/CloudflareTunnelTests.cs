using System.Net;
using System.Text;
using LabbyTwo.Core;
using LabbyTwo.Providers;

namespace LabbyTwo.Tests;

/// <summary>
/// Both ways of watching a Cloudflare tunnel: the API, answered here by a fake handler
/// because it lives at a fixed public address, and cloudflared's own metrics port,
/// answered by a real listener on loopback. Nothing leaves the machine.
/// </summary>
public sealed class CloudflareTunnelTests
{
    // Trimmed from a real cfd_tunnel list: one tunnel on two data centres with one
    // connection mid-reconnect, and a second that has never connected.
    private const string TwoTunnels = """
        {
          "success": true, "errors": [], "messages": [],
          "result": [
            {
              "id": "c1744f8b-faa1-48a4-9e5c-02ac921467fa", "name": "home", "status": "healthy",
              "connections": [
                { "colo_name": "DFW", "client_id": "1bedc5b2", "client_version": "2025.1.0",
                  "opened_at": "2026-09-27T10:00:00Z", "origin_ip": "203.0.113.5", "is_pending_reconnect": false },
                { "colo_name": "DFW", "client_id": "1bedc5b2", "client_version": "2025.1.0",
                  "opened_at": "2026-09-27T10:00:01Z", "origin_ip": "203.0.113.5", "is_pending_reconnect": false },
                { "colo_name": "ATL", "client_id": "1bedc5b2", "client_version": "2025.1.0",
                  "opened_at": "2026-09-27T10:00:02Z", "origin_ip": "203.0.113.5", "is_pending_reconnect": false },
                { "colo_name": "ATL", "client_id": "1bedc5b2", "client_version": "2025.1.0",
                  "opened_at": "2026-09-29T09:00:00Z", "origin_ip": "203.0.113.5", "is_pending_reconnect": true }
              ]
            },
            { "id": "0f3a", "name": "Lab Stuff", "status": "inactive", "connections": [] }
          ]
        }
        """;

    private static Connection Api(string tunnel = "") => new()
    {
        Provider = "cloudflare",
        Settings = new SettingsBag { ["account_id"] = "acc", ["api_token"] = "tok", ["tunnel"] = tunnel },
    };

    [Fact]
    public async Task The_api_reports_connectors_edges_and_a_series_per_tunnel()
    {
        var provider = new CloudflareProvider(new Factory(new Answer(TwoTunnels)));
        var connection = Api();

        var result = await provider.ProbeAsync(connection, CancellationToken.None);

        Assert.True(result.Ok);
        var metrics = result.Metrics!;
        Assert.Equal(2, metrics["tunnels"]);
        Assert.Equal(1, metrics["tunnels_down"]);
        Assert.Equal(4, metrics["connections"]);
        // The inactive tunnel has none, which is the one the fewest should be about.
        Assert.Equal(0, metrics["fewest_connections"]);
        Assert.Equal(2, metrics["edge_locations"]);
        Assert.Equal(1, metrics["reconnecting"]);
        Assert.True(metrics["connected_hours"] > 0);
        Assert.Equal(4, metrics["connections:home"]);
        Assert.Equal(0, metrics["connections:lab-stuff"]);

        var home = result.Details!["home"];
        Assert.Contains("healthy", home);
        Assert.Contains("4 connectors", home);
        Assert.Contains("ATL ×2, DFW ×2", home);
        Assert.Contains("cloudflared 2025.1.0", home);
        Assert.Contains("1 reconnecting", home);
        Assert.Contains("connected ", home);
        // The origin's public address is not something to print on a dashboard card.
        Assert.DoesNotContain("203.0.113.5", home);

        // Named in the widget editor's dropdown once a probe has said what the tunnels are.
        var declared = provider.MetricsFor(connection).Single(m => m.Key == "connections:home");
        Assert.Equal("home connectors", declared.Label);
    }

    [Fact]
    public async Task One_tunnel_is_not_recorded_twice()
    {
        var provider = new CloudflareProvider(new Factory(new Answer(TwoTunnels)));

        var result = await provider.ProbeAsync(Api("home"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(4, result.Metrics!["connections"]);
        Assert.Equal(4, result.Metrics["fewest_connections"]);
        Assert.DoesNotContain(result.Metrics.Keys, k => k.StartsWith("connections:", StringComparison.Ordinal));
        Assert.Equal("1 healthy, 4 connectors via ATL ×2, DFW ×2", result.Message);
    }

    [Fact]
    public void The_edge_is_read_down_to_the_city()
    {
        Assert.Equal("DFW", CloudflareEdge.Colo("dfw08"));
        Assert.Equal("DFW", CloudflareEdge.Colo("DFW"));
        Assert.Equal("", CloudflareEdge.Colo(null));
        Assert.Equal(2, CloudflareEdge.Count(["dfw08", "dfw09", "atl01", ""]));
        Assert.Equal("ATL, DFW ×2", CloudflareEdge.Summarise(["dfw08", "atl01", "dfw09"]));
    }

    [Fact]
    public void Exposition_lines_are_read_with_their_labels()
    {
        const string text = """
            # HELP cloudflared_tunnel_server_locations Where each tunnel is connected to.
            # TYPE cloudflared_tunnel_server_locations gauge
            cloudflared_tunnel_server_locations{connection_id="0",edge_location="dfw08"} 1
            weird{path="a,b}\"c\""} 2.5 1700000000000
            nothing_here NaN
            broken{label="never closed 1
            """;

        var samples = PrometheusText.Parse(text.ReplaceLineEndings("\n"));

        Assert.Equal(3, samples.Count);
        Assert.Equal("dfw08", samples[0].Label("edge_location"));
        Assert.Equal("a,b}\"c\"", samples[1].Label("path"));
        Assert.Equal(2.5, samples[1].Value);
        Assert.True(double.IsNaN(samples[2].Value));
        Assert.Null(PrometheusText.Sum(samples, "absent"));
    }

    private const string Metrics = """
        # HELP build_info Build and version information
        # TYPE build_info gauge
        build_info{goversion="go1.22.5",revision="2025-01-10",type="",version="2025.1.0"} 1
        cloudflared_tunnel_ha_connections 4
        cloudflared_tunnel_server_locations{connection_id="0",edge_location="dfw08"} 1
        cloudflared_tunnel_server_locations{connection_id="1",edge_location="atl01"} 1
        cloudflared_tunnel_server_locations{connection_id="2",edge_location="dfw09"} 1
        cloudflared_tunnel_server_locations{connection_id="3",edge_location="atl02"} 1
        cloudflared_tunnel_server_locations{connection_id="3",edge_location="mia01"} 0
        cloudflared_tunnel_total_requests 1200
        cloudflared_tunnel_request_errors 3
        process_start_time_seconds 1.7e+09
        """;

    [Fact]
    public async Task Local_cloudflared_reports_connections_and_where_they_land()
    {
        using var server = new FakeCloudflared(HttpStatusCode.OK,
            """{"status":200,"readyConnections":4,"connectorId":"1bedc5b2-4c6d-4c41-9a1e-5e0d7f0e1d2a"}""", Metrics);
        var provider = new CloudflaredProvider(new RealFactory());

        var result = await provider.ProbeAsync(server.Connection, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("4 connections via ATL ×2, DFW ×2", result.Message);
        Assert.Equal(4, result.Metrics!["connections"]);
        Assert.Equal(2, result.Metrics["edge_locations"]);
        Assert.True(result.Metrics["uptime_days"] > 0);
        // A rate needs two readings; the first probe only remembers the counters.
        Assert.False(result.Metrics.ContainsKey("requests_per_min"));

        Assert.Equal("4 · ATL ×2, DFW ×2", result.Details!["Connections"]);
        Assert.Equal("2025.1.0", result.Details["cloudflared"]);
        Assert.Equal("1bedc5b2", result.Details["Connector"]);
    }

    [Fact]
    public async Task Not_ready_is_down_but_still_reports_the_zero()
    {
        using var server = new FakeCloudflared(HttpStatusCode.ServiceUnavailable,
            """{"status":503,"readyConnections":0,"connectorId":"1bedc5b2"}""", "cloudflared_tunnel_ha_connections 0\n");
        var provider = new CloudflaredProvider(new RealFactory());

        var result = await provider.ProbeAsync(server.Connection, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("not connected to Cloudflare", result.Message);
        // Without the number, "The tunnel dropped" would have no reading to fire on.
        Assert.Equal(0, result.Metrics!["connections"]);
    }

    [Fact]
    public async Task Something_else_on_the_port_is_not_mistaken_for_cloudflared()
    {
        using var server = new FakeCloudflared(HttpStatusCode.NotFound, "not found", "");
        var provider = new CloudflaredProvider(new RealFactory());

        var result = await provider.ProbeAsync(server.Connection, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("does not look like cloudflared", result.Message);
    }

    [Fact]
    public async Task Nothing_listening_says_how_to_turn_the_metrics_port_on()
    {
        // Bound and closed again, so the port is almost certainly free: connection refused.
        LoopbackListener.Start(out var port).Close();
        var provider = new CloudflaredProvider(new RealFactory());
        var connection = new Connection
        {
            Provider = "cloudflared",
            Settings = new SettingsBag { ["url"] = $"http://127.0.0.1:{port}" },
        };

        var result = await provider.ProbeAsync(connection, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("--metrics 0.0.0.0:2000", result.Message);
    }

    [Fact]
    public void Counters_become_rates_and_a_restart_is_not_a_negative_one()
    {
        var at = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        var before = new CloudflaredProvider.Counters(at, 1000, 10);

        var rates = CloudflaredProvider.Rates(before, new(at.AddMinutes(2), 1300, 14));
        Assert.Equal(150, rates!.Value.Requests, 3);
        Assert.Equal(2, rates.Value.Errors, 3);

        Assert.Null(CloudflaredProvider.Rates(before, new(at.AddMinutes(2), 20, 0)));
        Assert.Null(CloudflaredProvider.Rates(before, new(at, 1000, 10)));
    }

    [Theory]
    [InlineData("http://cloudflared:2000", "http://cloudflared:2000")]
    [InlineData("http://cloudflared:2000/metrics", "http://cloudflared:2000")]
    [InlineData("http://cloudflared:2000/ready/", "http://cloudflared:2000")]
    [InlineData("cloudflared:2000", "http://cloudflared:2000")]
    public void Any_url_a_guide_gives_means_the_same_server(string written, string expected) =>
        Assert.Equal(expected, CloudflaredProvider.BaseUrl(written));

    [Theory]
    [InlineData(4, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public void A_healthy_tunnel_does_not_trip_the_dropped_rule(double connections, bool fires)
    {
        var rule = new CloudflaredProvider(new RealFactory()).SuggestedRules
            .Single(r => r.Name == "The tunnel dropped")
            .ForConnection("c");

        Assert.Equal(fires, rule.IsBreaching(connections));
        // And a tunnel with no errors reports exactly zero, which the strict "above" leaves alone.
        var errors = new CloudflaredProvider(new RealFactory()).SuggestedRules
            .Single(r => r.Metric == "errors_per_min").ForConnection("c");
        Assert.False(errors.IsBreaching(0));
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RealFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>The Cloudflare API, answering every request with the same body.</summary>
    private sealed class Answer(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("api.cloudflare.com", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>cloudflared's metrics server: /ready and /metrics, on a loopback port.</summary>
    private sealed class FakeCloudflared : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _stop = new();

        public Connection Connection { get; }

        public FakeCloudflared(HttpStatusCode readyStatus, string ready, string metrics)
        {
            _listener = LoopbackListener.Start(out var port);
            Connection = new Connection
            {
                Provider = "cloudflared",
                Settings = new SettingsBag { ["url"] = $"http://127.0.0.1:{port}/metrics" },
            };
            _ = Task.Run(() => ServeAsync(readyStatus, ready, metrics));
        }

        private async Task ServeAsync(HttpStatusCode readyStatus, string ready, string metrics)
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception) when (_stop.IsCancellationRequested || !_listener.IsListening)
                {
                    return;
                }

                var (status, body) = context.Request.Url!.AbsolutePath switch
                {
                    "/ready" => (readyStatus, ready),
                    "/metrics" => (HttpStatusCode.OK, metrics),
                    _ => (HttpStatusCode.NotFound, ""),
                };
                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.StatusCode = (int)status;
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
        }
    }
}
