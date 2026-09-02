using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
using TlsMode = Rnwood.Smtp4dev.Server.TlsMode;
using TaskQueue = Rnwood.Smtp4dev.Server.TaskQueue;

namespace Rnwood.Smtp4dev.Tests.Server
{
    /// <summary>
    /// Covers which settings changes are allowed to restart the SMTP listener. Restarting kills every session
    /// which is in progress, so it must only happen for settings which the listener itself reads.
    /// </summary>
    public class Smtp4devServerRestartTests : IDisposable
    {
        private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Long enough for the server to have acted on a settings change (it throttles them for 100ms) so that
        /// a test which asserts that nothing happened is meaningful.
        /// </summary>
        private static readonly TimeSpan SettingsChangeSettleTime = TimeSpan.FromSeconds(2);

        private readonly SqliteInMemory database = new SqliteInMemory();
        private readonly ServiceProvider serviceProvider;
        private readonly ChangeableTestOptionsMonitor<ServerOptions> serverOptions;
        private readonly Smtp4devServer server;

        public Smtp4devServerRestartTests()
        {
            serverOptions = new ChangeableTestOptionsMonitor<ServerOptions>(new ServerOptions
            {
                Port = 0,
                BindAddress = "127.0.0.1",
                HostName = "localhost",
                AllowRemoteConnections = false,
                DisableIPv6 = true,
                TlsMode = TlsMode.None,
                Pop3TlsMode = TlsMode.None
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
        public async Task ChangingAMessageValidationExpression_DoesNotRestartTheListener()
        {
            int[] portsBefore = ListeningPorts();
            Assert.NotEmpty(portsBefore);

            using TcpClient client = await ConnectAsync(portsBefore[0]);
            using NetworkStream stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

            Assert.StartsWith("220", await ReadLineAsync(reader));

            ChangeOptions(options => options with { MessageValidationExpression = "true" });

            //The session which was open before the change is still usable.
            await writer.WriteLineAsync("NOOP");
            Assert.StartsWith("250", await ReadLineAsync(reader));

            Assert.True(server.IsRunning);
            Assert.Equal(portsBefore, ListeningPorts());
        }

        [Fact]
        public async Task AddingAMailbox_CreatesItWithoutRestartingTheListener()
        {
            int[] portsBefore = ListeningPorts();
            Assert.NotEmpty(portsBefore);

            using TcpClient client = await ConnectAsync(portsBefore[0]);
            using NetworkStream stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

            Assert.StartsWith("220", await ReadLineAsync(reader));
            Assert.DoesNotContain("addedatruntime", MailboxNames());

            ChangeOptions(options => options with
            {
                Mailboxes = new[] { new MailboxOptions { Name = "addedatruntime", Recipients = "addedatruntime@*" } }
            });

            WaitFor(() => MailboxNames().Contains("addedatruntime"), "the new mailbox to be created");

            //The session which was open before the change is still usable.
            await writer.WriteLineAsync("NOOP");
            Assert.StartsWith("250", await ReadLineAsync(reader));

            Assert.True(server.IsRunning);
            Assert.Equal(portsBefore, ListeningPorts());
        }

        [Fact]
        public async Task ChangingThePort_RestartsTheListener()
        {
            int portBefore = Assert.Single(ListeningPorts());
            int newPort = GetFreeTcpPort();

            ChangeOptions(options => options with { Port = newPort });

            WaitFor(() => ListeningPorts().SequenceEqual(new[] { newPort }), $"the listener to move to port {newPort}");

            Assert.True(server.IsRunning);

            SocketException exception = await Assert.ThrowsAsync<SocketException>(() => ConnectAsync(portBefore));
            Assert.Equal(SocketError.ConnectionRefused, exception.SocketErrorCode);
        }

        public void Dispose()
        {
            server.Stop();
            serviceProvider.Dispose();
            database.Dispose();
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
                //The listener is being replaced.
                return Array.Empty<int>();
            }
        }

        private string[] MailboxNames()
        {
            using IServiceScope scope = serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<Smtp4devDbContext>();
            return dbContext.Mailboxes.Select(mailbox => mailbox.Name).ToArray();
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
