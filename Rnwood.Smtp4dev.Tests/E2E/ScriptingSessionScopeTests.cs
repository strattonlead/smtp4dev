using System;
using System.IO;
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
using TaskQueue = Rnwood.Smtp4dev.Server.TaskQueue;
using TlsMode = Rnwood.Smtp4dev.Server.TlsMode;

namespace Rnwood.Smtp4dev.Tests.E2E
{
    /// <summary>
    /// Validation expressions are a single global value per hook, so a rule written for one
    /// account is evaluated on every account's connections. The only thing that can keep them
    /// apart is a handle identifying whose connection it is.
    ///
    /// At AUTH that is credentials.Username and at RCPT it is recipient, but at every other hook
    /// there was nothing at all: the session handed to an expression is built from the stored
    /// session, which never recorded who authenticated. These tests pin down that
    /// session.AuthenticatedUser reports it after AUTH and stays null before.
    /// </summary>
    public class ScriptingSessionScopeTests : IDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        private readonly SqliteInMemory database = new SqliteInMemory();
        private readonly ServiceProvider serviceProvider;
        private readonly ChangeableTestOptionsMonitor<ServerOptions> serverOptions;
        private readonly Smtp4devServer server;

        public ScriptingSessionScopeTests()
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
                AuthenticationRequired = true,
                Users = new[]
                {
                    new UserOptions { Username = "mine@localhost", Password = "pw", DefaultMailbox = "Default" },
                    new UserOptions { Username = "theirs@localhost", Password = "pw", DefaultMailbox = "Default" },
                },
                // Rejects MAIL FROM only for the account named. Without a per connection identity
                // at this hook the rule would have to hit both accounts or neither.
                CommandValidationExpression =
                    "command.Verb === 'MAIL' && session.AuthenticatedUser === 'mine@localhost' ? 550 : true",
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
        public async Task ARuleScopedToOneAccountAppliesToThatAccountOnly()
        {
            Assert.StartsWith("550", await MailFromAs("mine@localhost"));
            Assert.StartsWith("250", await MailFromAs("theirs@localhost"));
        }

        public void Dispose()
        {
            server.Stop();
            serviceProvider.Dispose();
            database.Dispose();
        }

        /// <summary>Authenticates, sends MAIL FROM, and returns the reply to it.</summary>
        private async Task<string> MailFromAs(string username)
        {
            int port = server.ListeningEndpoints[0].Port;

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(Timeout);

            using NetworkStream stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

            Assert.StartsWith("220", await Read(reader));

            await writer.WriteLineAsync("EHLO test");
            // Drain the multiline EHLO reply.
            for (; ; )
            {
                string line = await Read(reader);
                if (line.Length < 4 || line[3] != '-') break;
            }

            string credential = Convert.ToBase64String(
                Encoding.ASCII.GetBytes("\0" + username + "\0pw"));
            await writer.WriteLineAsync("AUTH PLAIN " + credential);
            Assert.StartsWith("235", await Read(reader));

            await writer.WriteLineAsync("MAIL FROM:<sender@test>");
            return await Read(reader);
        }

        private static async Task<string> Read(StreamReader reader) =>
            await reader.ReadLineAsync().WaitAsync(Timeout);
    }
}
