using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Open.Nat.UnitTests
{
	[TestClass]
	public class InternetProtocolV4Tests
	{
		private const string IPV4_ADDRESS = "192.0.2.1";
        private UpnpMockServer _server;

		[TestInitialize]
		public void Setup()
		{
            var cfg = new UpnpMockServer.Configuration
            {
                BasePrefix = "http://127.0.0.1:5431/",
                ServiceDescPath = "/dyndev/uuid:0000e068-20a0-00e0-20a0-48a8000808e0",
                ControlPath = "/uuid:0000e068-20a0-00e0-20a0-48a802086048/WANIPConnection:1",
                ServiceType = "WANIPConnection:1",
                ExternalIp = IPAddress.Parse(IPV4_ADDRESS),
                Ssdp = UpnpMockServer.SsdpMode.IPv4
            };
            _server = new UpnpMockServer(cfg);
            _server.StartAsync().GetAwaiter().GetResult();
        }

		[TestCleanup]
		public void TearDown()
		{
            _server.Dispose();
        }

		[TestMethod]
		public async Task Connect()
		{
			var nat = new NatDiscoverer();
			var cts = new CancellationTokenSource(5000);
			var devices = await nat.DiscoverDevicesAsync(PortMapper.Upnp, cts); // Use plural to get all devices to avoid races with other network devices
            NatDevice device = null;
            foreach (var d in devices)
			{
				if (d.HostEndPoint.ToString().StartsWith("127.0.0.1"))
					device = d;
            }
            Assert.IsNotNull(device);

			var ip = await device.GetExternalIPAsync();
			Assert.AreEqual(IPAddress.Parse(IPV4_ADDRESS), ip);
			
			var testMapping = new Mapping(Protocol.Tcp, 1600, 1600, "Test");
            await device.CreatePortMapAsync(testMapping);
			await device.DeletePortMapAsync(testMapping);
        }
	}
}
