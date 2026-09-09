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
    /// Expression evaluation latency cannot detect a blocking fault, because for a blocking rule
    /// the block <em>is</em> the latency - the metric that would warn you is the one the fault
    /// makes look normal. This gauge is the number that actually says how close the server is to
    /// running out of threads.
    /// </summary>
    public class BlockedConnectionsTests
    {
        [Fact]
        public void ABlockedConnectionIsCounted_AndTheCountReturnsToZeroWhenItIsReleased()
        {
            bool connected = true;
            IConnection connection = Substitute.For<IConnection>();
            connection.IsConnected.Returns(_ => connected);

            int before = ScriptingHost.BlockedConnections;

            ScriptingHost host = CreateHost("delay(-1)");
            var evaluation = Task.Run(() => host.ValidateCommand(new SmtpCommand("NOOP"), null, connection));

            WaitUntil(() => ScriptingHost.BlockedConnections == before + 1, "the connection to be counted as blocked");

            connected = false;

            Assert.True(evaluation.Wait(TimeSpan.FromSeconds(30)), "the delay did not return");
            WaitUntil(() => ScriptingHost.BlockedConnections == before, "the count to return to where it started");
        }

        [Fact]
        public void AnExpressionWhichDoesNotBlockDoesNotMoveTheGauge()
        {
            IConnection connection = Substitute.For<IConnection>();
            connection.IsConnected.Returns(true);

            int before = ScriptingHost.BlockedConnections;

            ScriptingHost host = CreateHost("true");
            host.ValidateCommand(new SmtpCommand("NOOP"), null, connection);

            Assert.Equal(before, ScriptingHost.BlockedConnections);
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

        private static void WaitUntil(Func<bool> condition, string description)
        {
            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
            {
                if (condition()) return;
                Thread.Sleep(25);
            }

            Assert.Fail($"Timed out waiting for {description}.");
        }
    }
}
