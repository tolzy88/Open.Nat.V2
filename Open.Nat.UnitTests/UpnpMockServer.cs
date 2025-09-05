using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Open.Nat.UnitTests
{
    public sealed class UpnpMockServer : IDisposable
    {
        private readonly Configuration _cfg;
        private readonly HttpListener _listener = new();
        private CancellationTokenSource _cts;

        // Optional hooks to override behavior per test
        public Func<HttpListenerContext, Task> OnGetServiceDescription { get; init; }
        public Func<HttpListenerContext, Task> OnGetScpd { get; init; }
        public Func<HttpListenerContext, Task> OnGetExternalIp { get; init; }
        public Func<HttpListenerContext, Task> OnAddPortMapping { get; init; }
        public Func<HttpListenerContext, Task> OnGetGenericMapping { get; init; }
        public Func<HttpListenerContext, Task> OnDeletePortMapping { get; init; }

        public UpnpMockServer(Configuration cfg = null)
        {
            _cfg = cfg ?? new Configuration();

            var p = _cfg.BasePrefix.EndsWith("/") ? _cfg.BasePrefix : _cfg.BasePrefix + "/";
            _listener.Prefixes.Add(p);
            _listener.AuthenticationSchemes = AuthenticationSchemes.Anonymous;
        }

        public async Task StartAsync(CancellationToken ct = default)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _listener.Start();

            _ = Task.Run(() => HttpLoopAsync(_cts.Token), _cts.Token).ConfigureAwait(false);

            if (_cfg.Ssdp != SsdpMode.None)
            {
                _ = Task.Run(() => SsdpLoopAsync(_cts.Token, _cfg.Ssdp), _cts.Token).ConfigureAwait(false);
            }

            await Task.Yield(); // ensure StartAsync returns after loops started
        }

        public void Dispose()
        {
            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _listener.Close();
            }
            catch
            {
                // ignored
            }
        }

        private async Task HttpLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync().WaitAsync(ct);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }

                _ = Task.Run(() => HandleRequestAsync(ctx), ct);
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext ctx)
        {
            try
            {
                var req = ctx.Request;
                // Paths are compared on absolute path (no query)
                var path = req.Url!.AbsolutePath;

                if (path.Equals(_cfg.ServiceDescPath, StringComparison.Ordinal))
                {
                    if (OnGetServiceDescription is not null) { await OnGetServiceDescription(ctx); return; }
                    await WriteXmlAsync(ctx, DeviceDescriptorXml(), 200);
                    return;
                }

                if (path.Equals(_cfg.ScpdPath, StringComparison.Ordinal))
                {
                    if (OnGetScpd is not null) { await OnGetScpd(ctx); return; }
                    await WriteXmlAsync(ctx, ScpdStubXml(), 200);
                    return;
                }

                if (path.Equals(_cfg.ControlPath, StringComparison.Ordinal) &&
                    req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    var soapAction = req.Headers["SOAPACTION"] ?? string.Empty;
                    soapAction = soapAction.Trim('"');
                    var action = soapAction.Contains('#') ? soapAction[(soapAction.IndexOf('#') + 1)..] : soapAction;

                    switch (action)
                    {
                        case "GetExternalIPAddress":
                            if (OnGetExternalIp is not null) { await OnGetExternalIp(ctx); return; }
                            await WriteXmlAsync(ctx, GetExternalIpResponseXml(_cfg.ExternalIp), 200);
                            return;

                        case "AddPortMapping":
                            if (OnAddPortMapping is not null) { await OnAddPortMapping(ctx); return; }
                            await WriteXmlAsync(ctx, AddPortMappingResponseXml(), 200);
                            return;

                        case "GetGenericPortMappingEntry":
                            if (OnGetGenericMapping is not null) { await OnGetGenericMapping(ctx); return; }
                            await WriteXmlAsync(ctx, SoapFaultXml(713, "SpecifiedArrayIndexInvalid"), 500);
                            return;

                        case "DeletePortMapping":
                            if (OnDeletePortMapping is not null) { await OnDeletePortMapping(ctx); return; }
                            await WriteXmlAsync(ctx, DeletePortMappingResponseXml(), 200);
                            return;

                        default:
                            await WriteXmlAsync(ctx, SoapFaultXml(401, "Invalid Action"), 500);
                            return;
                    }
                }

                await WritePlainAsync(ctx, "Not Found", 404);
            }
            catch
            {
                SafeClose(ctx);
            }
        }

        private async Task SsdpLoopAsync(CancellationToken ct, SsdpMode mode)
        {
            if (mode == SsdpMode.IPv6)
            {
                using var udp = new UdpClient(AddressFamily.InterNetworkV6);
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.ExclusiveAddressUse = false;
                udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, 1900));
                udp.JoinMulticastGroup(IPAddress.Parse("ff02::c"));

                await SsdpReceiveLoopAsync(udp, ct);
            }
            else // IPv4
            {
                using var udp = new UdpClient(AddressFamily.InterNetwork);
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.ExclusiveAddressUse = false;
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, 1900));
                udp.JoinMulticastGroup(IPAddress.Parse("239.255.255.250"));

                await SsdpReceiveLoopAsync(udp, ct);
            }
        }

        private async Task SsdpReceiveLoopAsync(UdpClient udp, CancellationToken ct)
        {
            var location = AbsoluteUrl(_cfg.ServiceDescPath);
            var st = $"urn:schemas-upnp-org:service:{_cfg.ServiceType}";
            var usn = $"uuid:{_cfg.ServiceUuid}::{st}";

            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult p;
                try { p = await udp.ReceiveAsync(ct); }
                catch (OperationCanceledException) { break; }

                var msg = Encoding.UTF8.GetString(p.Buffer);
                if (!msg.Contains("M-SEARCH", StringComparison.OrdinalIgnoreCase)) continue;
                if (!msg.Contains("ssdp:discover", StringComparison.OrdinalIgnoreCase)) continue;

                // Minimal, valid SSDP 200 OK
                var resp = $"""
                HTTP/1.1 200 OK
                CACHE-CONTROL: max-age=1800
                DATE: {DateTime.UtcNow:R}
                EXT:
                LOCATION: {location}
                SERVER: Mock/1.0 UPnP/1.0 Test/1.0
                ST: {st}
                USN: {usn}
                """;
                var bytes = Encoding.UTF8.GetBytes(resp.Replace("\n", "\r\n"));
                await udp.SendAsync(bytes, bytes.Length, p.RemoteEndPoint);
            }
        }

        private static string ScpdStubXml() => 
        """
        <?xml version="1.0"?>
        <scpd xmlns="urn:schemas-upnp-org:service-1-0">
          <specVersion>
            <major>1</major>
            <minor>0</minor>
          </specVersion>
          <actionList>
            <action>
              <name>GetExternalIPAddress</name>
              <argumentList>
                <argument>
                  <name>NewExternalIPAddress</name>
                  <direction>out</direction>
                  <relatedStateVariable>ExternalIPAddress</relatedStateVariable>
                </argument>
              </argumentList>
            </action>
          </actionList>
          <serviceStateTable>
            <stateVariable sendEvents="no">
              <name>ExternalIPAddress</name>
              <dataType>string</dataType>
            </stateVariable>
          </serviceStateTable>
        </scpd>
        """;

        private string AbsoluteUrl(string path)
        {
            var baseUrl = _cfg.BasePrefix.EndsWith("/") ? _cfg.BasePrefix[..^1] : _cfg.BasePrefix;
            return baseUrl + path;
        }

        private static async Task WriteXmlAsync(HttpListenerContext ctx, string xml, int status)
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "text/xml; charset=\"utf-8\"";
            var bytes = Encoding.UTF8.GetBytes(xml);
            await ctx.Response.OutputStream.WriteAsync(bytes);
            SafeClose(ctx);
        }

        private static async Task WritePlainAsync(HttpListenerContext ctx, string text, int status)
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "text/plain; charset=\"utf-8\"";
            var bytes = Encoding.UTF8.GetBytes(text);
            await ctx.Response.OutputStream.WriteAsync(bytes);
            SafeClose(ctx);
        }

        private static void SafeClose(HttpListenerContext ctx)
        {
            try { ctx.Response.OutputStream.Flush(); } catch { }
            try { ctx.Response.Close(); } catch { }
        }

        private string DeviceDescriptorXml()
        {
            var baseUrl = AbsoluteUrl("/"); // URLBase requires trailing slash
            return $"""
            <?xml version="1.0"?>
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <specVersion><major>1</major><minor>0</minor></specVersion>
              <URLBase>{baseUrl}</URLBase>
              <device>
                <deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType>
                <friendlyName>Mock IGD</friendlyName>
                <manufacturer>Open.Nat.UnitTests</manufacturer>
                <UDN>uuid:{_cfg.ServiceUuid}-igd</UDN>
                <deviceList>
                  <device>
                    <deviceType>urn:schemas-upnp-org:device:WANDevice:1</deviceType>
                    <UDN>uuid:{_cfg.ServiceUuid}-wan</UDN>
                    <deviceList>
                      <device>
                        <deviceType>urn:schemas-upnp-org:device:WANConnectionDevice:1</deviceType>
                        <UDN>uuid:{_cfg.ServiceUuid}</UDN>
                        <serviceList>
                          <service>
                            <serviceType>urn:schemas-upnp-org:service:{_cfg.ServiceType}</serviceType>
                            <serviceId>urn:upnp-org:serviceId:WANIPConn1</serviceId>
                            <controlURL>{_cfg.ControlPath}</controlURL>
                            <eventSubURL>{_cfg.ControlPath}</eventSubURL>
                            <SCPDURL>{_cfg.ScpdPath}</SCPDURL>
                          </service>
                        </serviceList>
                      </device>
                    </deviceList>
                  </device>
                </deviceList>
              </device>
            </root>
            """;
        }

        private static string GetExternalIpResponseXml(IPAddress ip) => 
        $"""
        <?xml version="1.0"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
          <s:Body>
            <m:GetExternalIPAddressResponse xmlns:m="urn:schemas-upnp-org:service:WANIPConnection:1">
              <NewExternalIPAddress>{ip}</NewExternalIPAddress>
            </m:GetExternalIPAddressResponse>
          </s:Body>
        </s:Envelope>
        """;

        private static string AddPortMappingResponseXml() => 
        """
        <?xml version="1.0"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
          <s:Body>
            <m:AddPortMappingResponse xmlns:m="urn:schemas-upnp-org:service:WANIPConnection:1"/>
          </s:Body>
        </s:Envelope>
        """;

        private static string DeletePortMappingResponseXml() => 
        """
        <?xml version="1.0"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
            <s:Body>
            <m:DeletePortMappingResponse xmlns:m="urn:schemas-upnp-org:service:WANIPConnection:1"/>
            </s:Body>
        </s:Envelope>
        """;

        private static string SoapFaultXml(int code, string description) => 
        $"""
        <?xml version="1.0"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
          <s:Body>
            <s:Fault>
              <faultcode>s:Client</faultcode>
              <faultstring>UPnPError</faultstring>
              <detail>
                <UPnPError xmlns="urn:schemas-upnp-org:control-1-0">
                  <errorCode>{code}</errorCode>
                  <errorDescription>{description}</errorDescription>
                </UPnPError>
              </detail>
            </s:Fault>
          </s:Body>
        </s:Envelope>
        """;

        public enum SsdpMode { None, IPv4, IPv6 }

        public sealed class Configuration
        {
            public string BasePrefix { get; init; } = "http://127.0.0.1:5431/";
            public string ServiceDescPath { get; init; } = "/dyndev/uuid:0000e068-20a0-00e0-20a0-48a8000808e0";
            public string ServiceType { get; init; } = "WANIPConnection:1";
            public string ServiceUuid { get; init; } = "0000e068-20a0-00e0-20a0-48a802086048";
            public string ControlPath { get; init; } = "/uuid:0000e068-20a0-00e0-20a0-48a802086048/WANIPConnection:1";
            public string ScpdPath { get; init; } = "/dynsvc/WANIPConnection:1.xml";
            public IPAddress ExternalIp { get; init; } = IPAddress.Parse("222.222.111.111");
            public SsdpMode Ssdp { get; init; } = SsdpMode.None;
        }
    }
}
