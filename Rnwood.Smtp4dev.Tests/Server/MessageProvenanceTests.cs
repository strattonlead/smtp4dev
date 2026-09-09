using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Rnwood.Smtp4dev.Data;
using Rnwood.Smtp4dev.DbModel;
using Rnwood.Smtp4dev.Hubs;
using Rnwood.Smtp4dev.Server;
using Rnwood.Smtp4dev.Tests.DBMigrations.Helpers;
using Xunit;
using ApiMessage = Rnwood.Smtp4dev.ApiModel.Message;

namespace Rnwood.Smtp4dev.Tests.Server
{
    /// <summary>
    /// A message id is resolved across every mailbox, and the API used to project neither the
    /// mailbox a message was delivered to nor the SMTP session it arrived on - even though the
    /// database records both.
    ///
    /// Without the mailbox, a multi tenant front end holding an id cannot tell whose mailbox it
    /// landed in except by walking every mailbox it owns. Without the session, the session log
    /// endpoint cannot be reached from a message at all, so a transcript can never be tied to the
    /// message it produced.
    /// </summary>
    public class MessageProvenanceTests : IDisposable
    {
        private static readonly string Rfc822 = string.Join(
            "\r\n",
            "From: app@under.test",
            "To: someone@dl-provenance",
            "Subject: Confirm your email address",
            "",
            "Click the link.",
            "");

        private readonly SqliteInMemory database = new SqliteInMemory();

        [Fact]
        public async Task AMessage_ReportsTheMailboxItWasDeliveredToAndTheSessionItArrivedOn()
        {
            Guid messageId;
            Guid sessionId;

            using (var dbContext = new Smtp4devDbContext(database.ContextOptions))
            {
                dbContext.Database.EnsureCreated();

                var mailbox = new Mailbox { Id = Guid.NewGuid(), Name = "dl-provenance" };
                var folder = new MailboxFolder { Id = Guid.NewGuid(), Name = "INBOX", Mailbox = mailbox };
                var session = new Session
                {
                    Id = Guid.NewGuid(),
                    ClientAddress = "203.0.113.7",
                    StartDate = DateTime.UtcNow,
                    Log = "220 ready",
                };
                var message = new Message
                {
                    Id = Guid.NewGuid(),
                    From = "app@under.test",
                    To = "someone@dl-provenance",
                    Subject = "Confirm your email address",
                    ReceivedDate = DateTime.UtcNow,
                    Data = Encoding.ASCII.GetBytes(Rfc822),
                    Mailbox = mailbox,
                    MailboxFolder = folder,
                    Session = session,
                };

                dbContext.Mailboxes.Add(mailbox);
                dbContext.MailboxFolders.Add(folder);
                dbContext.Sessions.Add(session);
                dbContext.Messages.Add(message);
                await dbContext.SaveChangesAsync();

                messageId = message.Id;
                sessionId = session.Id;
            }

            using (var dbContext = new Smtp4devDbContext(database.ContextOptions))
            {
                var repository = new MessagesRepository(
                    new TaskQueue(NullLogger<TaskQueue>.Instance),
                    new NotificationsHub(),
                    dbContext);

                Message stored = await repository.TryGetMessageById(messageId, false);

                Assert.NotNull(stored);

                var api = new ApiMessage(stored);

                Assert.Equal("dl-provenance", api.MailboxName);
                Assert.Equal(sessionId, api.SessionId);
            }
        }

        public void Dispose()
        {
            database.Dispose();
        }
    }
}
