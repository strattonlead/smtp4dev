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
using Rnwood.Smtp4dev.DbModel;
using Rnwood.Smtp4dev.Hubs;
using Rnwood.Smtp4dev.Server.Settings;
using Rnwood.Smtp4dev.Tests.DBMigrations.Helpers;
using Rnwood.Smtp4dev.Tests.TestHelpers;
using Xunit;
using ImapServer = Rnwood.Smtp4dev.Server.ImapServer;
using ScriptingHost = Rnwood.Smtp4dev.Server.ScriptingHost;
using TaskQueue = Rnwood.Smtp4dev.Server.TaskQueue;
using TlsMode = Rnwood.Smtp4dev.Server.TlsMode;

namespace Rnwood.Smtp4dev.Tests.Server
{
    /// <summary>
    /// APPEND is how a message gets into a mailbox without arriving over SMTP, which is the only
    /// way to give it a folder, a flag and a received date.
    ///
    /// It used to accept only INBOX and Sent although the database models arbitrary folders, and
    /// refusing one was terminal: IMAP_Session sent the tagged NO and never resumed reading
    /// commands, so the client saw a reset and nothing was logged server side.
    /// </summary>
    public class ImapAppendTests : IDisposable
    {
        private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(30);

        private const string Raw = "From: a@b.test\r\nSubject: hi\r\n\r\nbody\r\n";

        private readonly SqliteInMemory database = new SqliteInMemory();
        private readonly ServiceProvider serviceProvider;
        private readonly TaskQueue taskQueue;
        private readonly ImapServer server;

        public ImapAppendTests()
        {
            var serverOptions = new ChangeableTestOptionsMonitor<ServerOptions>(new ServerOptions
            {
                BindAddress = "127.0.0.1",
                HostName = "localhost",
                AllowRemoteConnections = false,
                DisableIPv6 = true,
                TlsMode = TlsMode.None,
                Pop3TlsMode = TlsMode.None,
                ImapPort = 0,
                AuthenticationRequired = true,
                Users = new[]
                {
                    new UserOptions { Username = "box@localhost", Password = "pw", DefaultMailbox = "box" },
                },
            });

            var relayOptions = new ChangeableTestOptionsMonitor<RelayOptions>(new RelayOptions());

            taskQueue = new TaskQueue(LoggerFactory.Create(b => { }).CreateLogger<TaskQueue>());

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<NotificationsHub>();
            // The repository queues its writes, so the queue has to be running or AddMessage never
            // completes and the APPEND handler blocks on it.
            services.AddSingleton<Rnwood.Smtp4dev.Server.ITaskQueue>(_ =>
            {
                taskQueue.Start();
                return taskQueue;
            });
            services.AddScoped<Smtp4devDbContext>(_ => new Smtp4devDbContext(database.ContextOptions));
            services.AddScoped<IMessagesRepository, MessagesRepository>();
            serviceProvider = services.BuildServiceProvider();

            SeedMailbox();

            server = new ImapServer(
                serverOptions,
                new ScriptingHost(relayOptions, serverOptions),
                serviceProvider.GetRequiredService<IServiceScopeFactory>());

            server.TryStart();

            WaitFor(() => server.ListeningEndpoints.Length == 1, "the IMAP listener to start");
        }

        [Fact]
        public async Task AppendIntoInboxSucceeds()
        {
            using var client = await Connect();

            Assert.StartsWith("a2 OK", await client.Append("INBOX", Raw));
        }

        [Fact]
        public async Task AppendIntoAFolderWhichDoesNotExistIsRefused()
        {
            using var client = await Connect();

            string response = await client.Append("Junk", Raw);

            Assert.StartsWith("a2 NO", response);
            Assert.Contains("TRYCREATE", response);
        }

        [Fact]
        public async Task TheConnectionSurvivesARefusedAppend()
        {
            // The one that matters. The refusal used to stop the session reading commands, so the
            // next one never got an answer.
            using var client = await Connect();

            await client.Append("Junk", Raw);

            Assert.StartsWith("a3 OK", await client.Noop());
        }

        [Fact]
        public async Task ACreatedFolderCanBeAppendedTo()
        {
            using var client = await Connect();

            Assert.StartsWith("a4 OK", await client.Create("Junk"));
            Assert.StartsWith("a2 OK", await client.Append("Junk", Raw));
        }

        [Fact]
        public async Task AnAppendedMessageHasNoSessionOnItsSummary()
        {
            using (var client = await Connect())
            {
                Assert.StartsWith("a2 OK", await client.Append("INBOX", Raw));
            }

            using var scope = serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IMessagesRepository>();

            WaitFor(() => repository.GetMessageSummaries("box", "INBOX").Any(), "the message to land");

            // Nothing delivered it over SMTP, and that absence is the only signal a client has.
            Assert.Null(repository.GetMessageSummaries("box", "INBOX").Single().SessionId);
        }

        public void Dispose()
        {
            server.Stop();
            serviceProvider.Dispose();
            database.Dispose();
        }

        private void SeedMailbox()
        {
            using var context = new Smtp4devDbContext(database.ContextOptions);

            var mailbox = new Mailbox { Id = Guid.NewGuid(), Name = "box" };
            mailbox.MailboxFolders.Add(new MailboxFolder { Id = Guid.NewGuid(), Name = MailboxFolder.INBOX });
            mailbox.MailboxFolders.Add(new MailboxFolder { Id = Guid.NewGuid(), Name = MailboxFolder.SENT });
            context.Mailboxes.Add(mailbox);

            if (!context.ImapState.Any())
            {
                context.ImapState.Add(new ImapState { Id = Guid.NewGuid(), LastUid = 0 });
            }

            context.SaveChanges();
        }

        private Task<ImapTestClient> Connect() => ImapTestClient.Connect(server.ListeningEndpoints[0].Port);

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

        /// <summary>A minimal IMAP client: LOGIN, APPEND, CREATE, NOOP.</summary>
        private sealed class ImapTestClient : IDisposable
        {
            private readonly TcpClient client;
            private readonly NetworkStream stream;
            private readonly StreamReader reader;
            private readonly StreamWriter writer;

            private ImapTestClient(TcpClient client)
            {
                this.client = client;
                stream = client.GetStream();
                reader = new StreamReader(stream, Encoding.ASCII);
                writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };
            }

            public static async Task<ImapTestClient> Connect(int port)
            {
                var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, port).WaitAsync(ResponseTimeout);

                var client = new ImapTestClient(tcp);
                Assert.StartsWith("* OK", await client.ReadLine());

                string login = await client.Command("a1", "LOGIN \"box@localhost\" \"pw\"");
                Assert.StartsWith("a1 OK", login);

                return client;
            }

            public async Task<string> Append(string folder, string raw)
            {
                byte[] bytes = Encoding.ASCII.GetBytes(raw);
                await writer.WriteLineAsync($"a2 APPEND \"{folder}\" {{{bytes.Length}}}");

                string line = await ReadLine();
                while (line.StartsWith("*"))
                {
                    line = await ReadLine();
                }

                //Refused before the literal, which is the whole point of the TRYCREATE case.
                if (!line.StartsWith("+"))
                {
                    return line;
                }

                await stream.WriteAsync(bytes);
                await writer.WriteLineAsync("");

                return await ReadTagged("a2");
            }

            public Task<string> Create(string folder) => Command("a4", $"CREATE \"{folder}\"");

            public Task<string> Noop() => Command("a3", "NOOP");

            private async Task<string> Command(string tag, string text)
            {
                await writer.WriteLineAsync($"{tag} {text}");

                return await ReadTagged(tag);
            }

            private async Task<string> ReadTagged(string tag)
            {
                for (; ; )
                {
                    string line = await ReadLine();
                    if (line.StartsWith(tag + " "))
                    {
                        return line;
                    }
                }
            }

            private async Task<string> ReadLine()
            {
                string line = await reader.ReadLineAsync().WaitAsync(ResponseTimeout);

                return line ?? throw new IOException("The engine closed the IMAP connection.");
            }

            public void Dispose()
            {
                try
                {
                    client.Close();
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
