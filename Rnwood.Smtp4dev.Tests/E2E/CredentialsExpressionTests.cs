using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
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
using TaskQueue = Rnwood.Smtp4dev.Server.TaskQueue;
using TlsMode = Rnwood.Smtp4dev.Server.TlsMode;

namespace Rnwood.Smtp4dev.Tests.E2E
{
    /// <summary>
    /// The credentials expression used to be all or nothing: any expression at all replaced
    /// password validation for the whole server. An expression which rejects one account therefore
    /// had to return true for every other account - and true there means "authenticated", not
    /// "carry on checking".
    ///
    /// So an expression written to fail one login silently accepted every other login with any
    /// password at all. These tests pin down that null means "no opinion" and the normal check
    /// still runs.
    /// </summary>
    public class CredentialsExpressionTests : IDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        private readonly SqliteInMemory database = new SqliteInMemory();
        private readonly ServiceProvider serviceProvider;
        private readonly Smtp4devServer server;

        public CredentialsExpressionTests()
        {
            var serverOptions = new ChangeableTestOptionsMonitor<ServerOptions>(new ServerOptions
            {
                Port = 0,
                BindAddress = "127.0.0.1",
                HostName = "localhost",
                AllowRemoteConnections = false,
                DisableIPv6 = true,
                TlsMode = TlsMode.None,
                Pop3TlsMode = TlsMode.None,
                ImapPort = null,
                AuthenticationRequired = true,
                SmtpAllowAnyCredentials = false,
                Users = new[]
                {
                    new UserOptions { Username = "blocked@localhost", Password = "pw", DefaultMailbox = "Default" },
                    new UserOptions { Username = "allowed@localhost", Password = "pw", DefaultMailbox = "Default" },
                },
                // Rejects one account and says nothing about anybody else.
                CredentialsValidationExpression =
                    "credentials.Username === 'blocked@localhost' ? false : null",
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
        public async Task TheNamedAccountIsRejected()
        {
            Assert.StartsWith("535", await Authenticate("blocked@localhost", "pw"));
        }

        [Fact]
        public async Task AnotherAccountStillAuthenticatesNormally()
        {
            Assert.StartsWith("235", await Authenticate("allowed@localhost", "pw"));
        }

        [Fact]
        public async Task AWrongPasswordIsStillRejected()
        {
            // This is the one that mattered: with the old behaviour the expression's fall through
            // said "authenticated" and any password was accepted.
            Assert.StartsWith("535", await Authenticate("allowed@localhost", "not-the-password"));
        }

        public void Dispose()
        {
            server.Stop();
            serviceProvider.Dispose();
            database.Dispose();
        }

        private async Task<string> Authenticate(string username, string password)
        {
            int port = server.ListeningEndpoints[0].Port;

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(Timeout);

            using NetworkStream stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

            Assert.StartsWith("220", await Read(reader));

            await writer.WriteLineAsync("EHLO test");
            for (; ; )
            {
                string line = await Read(reader);
                if (line.Length < 4 || line[3] != '-') break;
            }

            string credential = Convert.ToBase64String(
                Encoding.ASCII.GetBytes("\0" + username + "\0" + password));
            await writer.WriteLineAsync("AUTH PLAIN " + credential);

            return await Read(reader);
        }

        private static async Task<string> Read(StreamReader reader) =>
            await reader.ReadLineAsync().WaitAsync(Timeout);
    }
}
