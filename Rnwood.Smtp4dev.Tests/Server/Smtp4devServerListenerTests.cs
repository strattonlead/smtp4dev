using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rnwood.Smtp4dev.Data;
using Rnwood.Smtp4dev.Hubs;
using Rnwood.Smtp4dev.Server.Settings;
using Rnwood.Smtp4dev.Tests.DBMigrations.Helpers;
using Rnwood.Smtp4dev.Tests.TestHelpers;
using Xunit;
using ScriptingHost = Rnwood.Smtp4dev.Server.ScriptingHost;
using Smtp4devServer = Rnwood.Smtp4dev.Server.Smtp4devServer;
using TaskQueue = Rnwood.Smtp4dev.Server.TaskQueue;
using TlsMode = Rnwood.Smtp4dev.Server.TlsMode;

namespace Rnwood.Smtp4dev.Tests.Server
{
    /// <summary>
    /// Deadletter needs submission on three ports at once, two of them under different TLS modes.
    /// These tests pin down that each configured listener really gets its own socket, that
    /// changing the set touches only the listeners which changed, and that both TLS modes
    /// complete a handshake with the configured certificate.
    /// </summary>
    public class Smtp4devServerListenerTests : IDisposable
    {
        private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan SettingsChangeSettleTime = TimeSpan.FromSeconds(2);

        private readonly SqliteInMemory database = new SqliteInMemory();
        private readonly ServiceProvider serviceProvider;
        private readonly ChangeableTestOptionsMonitor<ServerOptions> serverOptions;
        private readonly Smtp4devServer server;

        public Smtp4devServerListenerTests()
        {
            serverOptions = new ChangeableTestOptionsMonitor<ServerOptions>(new ServerOptions
            {
                Port = 0,
                BindAddress = "127.0.0.1",
                HostName = "localhost",
                AllowRemoteConnections = false,
                DisableIPv6 = true,
                TlsMode = TlsMode.None,
                Pop3TlsMode = TlsMode.None,
                ImapPort = null,
                TlsCertificate = Path.GetFullPath("Resources/smtp4dev.crt"),
                TlsCertificatePrivateKey = Path.GetFullPath("Resources/smtp4dev.key")
            });

            var relayOptions = new ChangeableTestOptionsMonitor<RelayOptions>(new RelayOptions());

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped<Smtp4devDbContext>(_ => new Smtp4devDbContext(database.ContextOptions));
            serviceProvider = services.BuildServiceProvider();

            server = new Smtp4devServer(
                serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                serverOptions,
                relayOptions,
                new NotificationsHub(),
                _ => null,
                new TaskQueue(serviceProvider.GetRequiredService<ILogger<TaskQueue>>()),
                new ScriptingHost(relayOptions, serverOptions));

            server.TryStart();

            Assert.Null(server.Exception);
            Assert.True(server.IsRunning, "SMTP server did not start");
        }

        [Fact]
        public void EveryConfiguredListener_GetsItsOwnPort()
        {
            ChangeOptions(options => options with
            {
                SmtpListeners =
                [
                    new SmtpListenerOptions { Port = 0, TlsMode = TlsMode.StartTls },
                    new SmtpListenerOptions { Port = 0, TlsMode = TlsMode.ImplicitTls },
                    new SmtpListenerOptions { Port = 0, TlsMode = TlsMode.None }
                ]
            });

            WaitFor(() => ListeningPorts().Length == 3, "three listeners to be running");

            Assert.Null(server.Exception);
            Assert.True(server.IsRunning);
            Assert.Equal(3, ListeningPorts().Distinct().Count());
        }

        [Fact]
        public async Task AddingAListener_DoesNotDisturbASessionOnAnother()
        {
            int portBefore = Assert.Single(ListeningPorts());

            using TcpClient client = await ConnectAsync(portBefore);
            using NetworkStream stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

            Assert.StartsWith("220", await ReadLineAsync(reader));

            //The first entry matches the listener which is already running, so it keeps its socket
            //and the sessions on it; only the second entry is new.
            ChangeOptions(options => options with
            {
                SmtpListeners =
                [
                    new SmtpListenerOptions { Port = 0, TlsMode = TlsMode.None },
                    new SmtpListenerOptions { Port = 0, TlsMode = TlsMode.None }
                ]
            });

            WaitFor(() => ListeningPorts().Length == 2, "the second listener to start");

            //The session which was open before the change is still usable.
            await writer.WriteLineAsync("NOOP");
            Assert.StartsWith("250", await ReadLineAsync(reader));

            Assert.Contains(portBefore, ListeningPorts());
        }

        [Fact]
        public async Task RemovingAListener_StopsOnlyThatListener()
        {
            int keptPort = Assert.Single(ListeningPorts());
            int removedPort = GetFreeTcpPort();

            ChangeOptions(options => options with
            {
                SmtpListeners =
                [
                    new SmtpListenerOptions { Port = 0, TlsMode = TlsMode.None },
                    new SmtpListenerOptions { Port = removedPort, TlsMode = TlsMode.None }
                ]
            });

            WaitFor(() => ListeningPorts().Length == 2, "both listeners to start");
            Assert.Contains(removedPort, ListeningPorts());

            ChangeOptions(options => options with
            {
                SmtpListeners = [new SmtpListenerOptions { Port = 0, TlsMode = TlsMode.None }]
            });

            WaitFor(() => ListeningPorts().SequenceEqual(new[] { keptPort }), "the second listener to stop");

            using TcpClient client = await ConnectAsync(keptPort);
            using NetworkStream stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            Assert.StartsWith("220", await ReadLineAsync(reader));

            SocketException exception = await Assert.ThrowsAsync<SocketException>(() => ConnectAsync(removedPort));
            Assert.Equal(SocketError.ConnectionRefused, exception.SocketErrorCode);
        }

        [Fact]
        public async Task ImplicitTlsListener_CompletesAHandshake()
        {
            int port = StartSingleListener(TlsMode.ImplicitTls);

            using var client = new MailKit.Net.Smtp.SmtpClient
            {
                ServerCertificateValidationCallback = (_, _, _, _) => true,
                CheckCertificateRevocation = false
            };
            await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.SslOnConnect).WaitAsync(ResponseTimeout);

            Assert.True(client.IsSecure);

            await client.DisconnectAsync(true);
        }

        [Fact]
        public async Task StartTlsListener_CompletesAHandshake()
        {
            int port = StartSingleListener(TlsMode.StartTls);

            using var client = new MailKit.Net.Smtp.SmtpClient
            {
                ServerCertificateValidationCallback = (_, _, _, _) => true,
                CheckCertificateRevocation = false
            };
            await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.StartTls).WaitAsync(ResponseTimeout);

            Assert.True(client.IsSecure);

            await client.DisconnectAsync(true);
        }

        [Fact]
        public void ChangingTheCertificate_RestartsEveryListener()
        {
            ChangeOptions(options => options with
            {
                SmtpListeners =
                [
                    new SmtpListenerOptions { Port = 0, TlsMode = TlsMode.StartTls },
                    new SmtpListenerOptions { Port = 0, TlsMode = TlsMode.ImplicitTls }
                ]
            });

            WaitFor(() => ListeningPorts().Length == 2, "both listeners to start");
            int[] portsBefore = ListeningPorts();

            //A renewal points the server at a different certificate file. It is shared by every
            //listener, so every listener has to be recreated to pick it up.
            (string certPath, string keyPath) = CopyTestCertificate();

            ChangeOptions(options => options with
            {
                TlsCertificate = certPath,
                TlsCertificatePrivateKey = keyPath
            });

            WaitFor(
                () => ListeningPorts().Length == 2 && !ListeningPorts().Intersect(portsBefore).Any(),
                "both listeners to be recreated on new ephemeral ports");

            Assert.Null(server.Exception);
            Assert.True(server.IsRunning);
        }

        public void Dispose()
        {
            server.Stop();
            serviceProvider.Dispose();
            database.Dispose();
        }

        private static (string CertPath, string KeyPath) CopyTestCertificate()
        {
            string directory = Path.Combine(Path.GetTempPath(), "smtp4dev-cert-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            string certPath = Path.Combine(directory, "smtp4dev.crt");
            string keyPath = Path.Combine(directory, "smtp4dev.key");

            File.Copy(Path.GetFullPath("Resources/smtp4dev.crt"), certPath);
            File.Copy(Path.GetFullPath("Resources/smtp4dev.key"), keyPath);

            return (certPath, keyPath);
        }

        private int StartSingleListener(TlsMode tlsMode)
        {
            ChangeOptions(options => options with
            {
                SmtpListeners = [new SmtpListenerOptions { Port = 0, TlsMode = tlsMode }]
            });

            WaitFor(() => ListeningPorts().Length == 1, $"the {tlsMode} listener to start");
            Assert.Null(server.Exception);
            return ListeningPorts()[0];
        }

        private void ChangeOptions(Func<ServerOptions, ServerOptions> change)
        {
            serverOptions.Set(change(serverOptions.CurrentValue));
            Thread.Sleep(SettingsChangeSettleTime);
        }

        private int[] ListeningPorts()
        {
            try
            {
                return server.ListeningEndpoints.Select(endpoint => endpoint.Port).ToArray();
            }
            catch (ObjectDisposedException)
            {
                //A listener is being replaced.
                return Array.Empty<int>();
            }
        }

        private static async Task<TcpClient> ConnectAsync(int port)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(ResponseTimeout);
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        private static async Task<string> ReadLineAsync(StreamReader reader) =>
            await reader.ReadLineAsync().WaitAsync(ResponseTimeout);

        private static int GetFreeTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
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
