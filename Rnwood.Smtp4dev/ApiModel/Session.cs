using Rnwood.SmtpServer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Rnwood.Smtp4dev.ApiModel
{
    public class Session
    {
        public Session(DbModel.Session dbSession)
        {
            this.Id = dbSession.Id;
            this.ClientAddress = dbSession.ClientAddress;
            this.ClientName = dbSession.ClientName;
            this.Error = dbSession.SessionError;
            this.ErrorType = dbSession.SessionErrorType?.ToString();
            this.StartDate = dbSession.StartDate;
            if (dbSession.HasBareLineFeed)
            {
                this.Warnings.Add(new SessionWarning { Details = "Session contains bare line feeds (LF without CR). RFC 5321 requires CRLF line endings." });
            }
        }


        public Guid Id { get; private set; }

        public string ClientAddress { get; private set; }
        public string ClientName { get; private set; }

        /// <summary>
        /// The username this connection authenticated as, or null if it has not authenticated yet.
        ///
        /// This is not persisted with the session; it is attached from the live connection at the
        /// point a validation expression is evaluated. Without it a scripting expression has no
        /// way to tell which account a connection belongs to at any hook other than AUTH and RCPT,
        /// which means a rule written for one account would fire on everyone's connections.
        /// </summary>
        public string AuthenticatedUser { get; set; }
        public string ErrorType { get; private set; }
        public DateTime StartDate { get; }
        public string Error { get; private set; }
        
        public List<SessionWarning> Warnings { get; set; } = new List<SessionWarning>();


    }
}
