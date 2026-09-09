using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Rnwood.Smtp4dev.Data;
using Rnwood.Smtp4dev.Server.Settings;
using Rnwood.Smtp4dev.Tests.DBMigrations.Helpers;
using Rnwood.Smtp4dev.Tests.TestHelpers;
using Xunit;
using ImapServer = Rnwood.Smtp4dev.Server.ImapServer;
using ScriptingHost = Rnwood.Smtp4dev.Server.ScriptingHost;
using TlsMode = Rnwood.Smtp4dev.Server.TlsMode;

namespace Rnwood.Smtp4dev.Tests.Server
{
    /// <summary>
    /// A listener configured without TLS must behave exactly as IMAP did before it could do TLS at
    /// all. IMAP_Session advertises STARTTLS whenever its certificate is non null, so handing the
    /// certificate to every binding - rather than only to the ones which asked for TLS - makes a
    /// plaintext listener offer an upgrade, and a client connecting with SecureSocketOptions.Auto
    /// takes it and then fails to validate a self signed certificate.
    /// </summary>
    public class ImapServerPlaintextTests : IDisposable
    {
        private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(30);

        private readonly SqliteInMemory database = new SqliteInMemory();
        private readonly ServiceProvider serviceProvider;
        private readonly ImapServer server;

        public ImapServerPlaintextTests()
        {
            //A certificate is configured and SMTP uses it, exactly as the E2E harness runs. The
            //IMAP listener still asks for no TLS, so it must not see that certificate.
            var serverOptions = new ChangeableTestOptionsMonitor<ServerOptions>(new ServerOptions
            {
                BindAddress = "127.0.0.1",
                HostName = "localhost",
                AllowRemoteConnections = false,
                DisableIPv6 = true,
                TlsMode = TlsMode.StartTls,
                Pop3TlsMode = TlsMode.None,
                TlsCertificate = Path.GetFullPath("Resources/smtp4dev.crt"),
                TlsCertificatePrivateKey = Path.GetFullPath("Resources/smtp4dev.key"),
                ImapPort = 0
            });

            var relayOptions = new ChangeableTestOptionsMonitor<RelayOptions>(new RelayOptions());

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped<Smtp4devDbContext>(_ => new Smtp4devDbContext(database.ContextOptions));
            serviceProvider = services.BuildServiceProvider();

            server = new ImapServer(
                serverOptions,
                new ScriptingHost(relayOptions, serverOptions),
                serviceProvider.GetRequiredService<IServiceScopeFactory>());

            server.TryStart();

            WaitFor(() => server.ListeningEndpoints.Length == 1, "the IMAP listener to start");
        }

        [Fact]
        public async Task PlaintextListener_DoesNotAdvertiseStartTls()
        {
            int port = server.ListeningEndpoints[0].Port;

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(ResponseTimeout);

            using NetworkStream stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

            Assert.StartsWith("* OK", await reader.ReadLineAsync().WaitAsync(ResponseTimeout));

            await writer.WriteLineAsync("a1 CAPABILITY");

            string capabilities = await reader.ReadLineAsync().WaitAsync(ResponseTimeout);

            Assert.DoesNotContain("STARTTLS", capabilities, StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            server.Stop();
            serviceProvider.Dispose();
            database.Dispose();
        }

        private static void WaitFor(Func<bool> condition, string description)
        {
            DateTime deadline = DateTime.UtcNow.Add(ResponseTimeout);

            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                Thread.Sleep(50);
            }

            Assert.Fail($"Timed out waiting for {description}.");
        }
    }
}
