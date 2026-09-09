using System;
using System.IO;
using System.Net;
using System.Net.Security;
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
    /// IMAP had no TLS at all: every binding used the constructor which leaves SslMode at None and
    /// the certificate null, so 993 could not be served and STARTTLS on 143 was always refused.
    /// These tests prove both work, because IMAP carries the credentials and the password reset
    /// links which are the whole reason the sandbox is not allowed to speak plaintext.
    /// </summary>
    public class ImapServerTlsTests : IDisposable
    {
        private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(30);

        private readonly SqliteInMemory database = new SqliteInMemory();
        private readonly ServiceProvider serviceProvider;
        private readonly ChangeableTestOptionsMonitor<ServerOptions> serverOptions;
        private readonly ImapServer server;

        public ImapServerTlsTests()
        {
            serverOptions = new ChangeableTestOptionsMonitor<ServerOptions>(new ServerOptions
            {
                BindAddress = "127.0.0.1",
                HostName = "localhost",
                AllowRemoteConnections = false,
                DisableIPv6 = true,
                TlsMode = TlsMode.None,
                Pop3TlsMode = TlsMode.None,
                TlsCertificate = Path.GetFullPath("Resources/smtp4dev.crt"),
                TlsCertificatePrivateKey = Path.GetFullPath("Resources/smtp4dev.key"),
                ImapListeners =
                [
                    new ImapListenerOptions { Port = 0, TlsMode = TlsMode.StartTls },
                    new ImapListenerOptions { Port = 0, TlsMode = TlsMode.ImplicitTls }
                ]
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

            WaitFor(() => server.ListeningEndpoints.Length == 2, "both IMAP listeners to start");
        }

        [Fact]
        public async Task ImplicitTlsListener_CompletesAHandshakeAndGreets()
        {
            int port = server.ListeningEndpoints[1].Port;

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(ResponseTimeout);

            using var ssl = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync("localhost").WaitAsync(ResponseTimeout);

            var reader = new StreamReader(ssl, Encoding.ASCII);
            string greeting = await reader.ReadLineAsync().WaitAsync(ResponseTimeout);

            Assert.StartsWith("* OK", greeting);
        }

        [Fact]
        public async Task StartTlsListener_AdvertisesStartTls()
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

            Assert.Contains("STARTTLS", capabilities, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task StartTlsListener_CompletesAHandshake()
        {
            int port = server.ListeningEndpoints[0].Port;

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(ResponseTimeout);

            using NetworkStream stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

            Assert.StartsWith("* OK", await reader.ReadLineAsync().WaitAsync(ResponseTimeout));

            await writer.WriteLineAsync("a1 STARTTLS");
            Assert.StartsWith("a1 OK", await reader.ReadLineAsync().WaitAsync(ResponseTimeout));

            using var ssl = new SslStream(stream, false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync("localhost").WaitAsync(ResponseTimeout);

            Assert.True(ssl.IsEncrypted);
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
