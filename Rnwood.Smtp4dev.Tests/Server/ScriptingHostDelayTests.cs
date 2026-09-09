using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Rnwood.Smtp4dev.Server.Settings;
using Rnwood.Smtp4dev.Tests.TestHelpers;
using Rnwood.SmtpServer;
using Xunit;
using ScriptingHost = Rnwood.Smtp4dev.Server.ScriptingHost;
using ServerOptions = Rnwood.Smtp4dev.Server.Settings.ServerOptions;

namespace Rnwood.Smtp4dev.Tests.Server
{
    /// <summary>
    /// delay() used to be a single Thread.Sleep, so delay(-1) parked an OS thread until the
    /// process exited and a finite delay held one for its whole duration even after the client
    /// had gone. Expressions are global, so one tenant's timeout rule runs on every tenant's
    /// connections and that is thread exhaustion for everyone.
    /// </summary>
    public class ScriptingHostDelayTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        [Fact]
        public void IndefiniteDelay_ReturnsWhenTheConnectionGoesAway()
        {
            bool connected = true;
            IConnection connection = Substitute.For<IConnection>();
            connection.IsConnected.Returns(_ => connected);

            ScriptingHost host = CreateHost("delay(-1)");

            var stopwatch = Stopwatch.StartNew();
            Task evaluation = Task.Run(() => host.ValidateCommand(new SmtpCommand("NOOP"), null, connection));

            //Still blocked while the connection is up.
            Assert.False(evaluation.Wait(TimeSpan.FromSeconds(1)));

            connected = false;

            Assert.True(evaluation.Wait(Timeout), "delay(-1) did not return after the connection closed");
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"delay(-1) took {stopwatch.Elapsed} to notice the disconnect");
        }

        [Fact]
        public void FiniteDelay_ReturnsAfterTheRequestedTime_WhenTheConnectionStaysUp()
        {
            IConnection connection = Substitute.For<IConnection>();
            connection.IsConnected.Returns(true);

            ScriptingHost host = CreateHost("delay(1)");

            var stopwatch = Stopwatch.StartNew();
            host.ValidateCommand(new SmtpCommand("NOOP"), null, connection);
            stopwatch.Stop();

            Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(900), TimeSpan.FromSeconds(5));
        }

        [Fact]
        public void FiniteDelay_ReturnsEarly_WhenTheConnectionGoesAway()
        {
            bool connected = true;
            IConnection connection = Substitute.For<IConnection>();
            connection.IsConnected.Returns(_ => connected);

            ScriptingHost host = CreateHost("delay(120)");

            var stopwatch = Stopwatch.StartNew();
            Task evaluation = Task.Run(() => host.ValidateCommand(new SmtpCommand("NOOP"), null, connection));

            Thread.Sleep(TimeSpan.FromMilliseconds(500));
            connected = false;

            Assert.True(evaluation.Wait(Timeout), "delay(120) did not return after the connection closed");
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"delay(120) took {stopwatch.Elapsed} to notice the disconnect");
        }

        private static ScriptingHost CreateHost(string commandValidationExpression)
        {
            var serverOptions = new ChangeableTestOptionsMonitor<ServerOptions>(new ServerOptions
            {
                CommandValidationExpression = commandValidationExpression
            });

            var relayOptions = new ChangeableTestOptionsMonitor<RelayOptions>(new RelayOptions());

            return new ScriptingHost(relayOptions, serverOptions);
        }
    }
}
