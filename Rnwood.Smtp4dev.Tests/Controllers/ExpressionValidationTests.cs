using System.Linq;
using Xunit;

namespace Rnwood.Smtp4dev.Tests.Controllers
{
    /// <summary>
    /// A settings write carrying an unparseable validation expression used to succeed, and
    /// ScriptingHost then failed silently: it logged, set the script to null, and the hook was
    /// disabled from that moment on. The caller saw a 200.
    ///
    /// Because the expressions are a single global value per hook, that disabled the hook for
    /// every client of the server, not just the one which made the bad write. These tests pin down
    /// that the write is refused instead, using the same parser ScriptingHost uses so that
    /// anything accepted here cannot fail there.
    /// </summary>
    public class ExpressionValidationTests
    {
        [Theory]
        [InlineData("true")]
        [InlineData("credentials.username === 'blocked' ? error(535, 'nope') : true")]
        [InlineData("delay(5)")]
        [InlineData("")]
        [InlineData(null)]
        public void TheParserAcceptsAnExpressionScriptingHostWouldAccept(string expression)
        {
            Assert.True(Parses(expression));
        }

        [Theory]
        [InlineData("this is not javascript at all")]
        [InlineData("function (")]
        [InlineData("if (true) {")]
        [InlineData("'unterminated")]
        public void TheParserRejectsAnExpressionScriptingHostWouldRejectSilently(string expression)
        {
            Assert.False(Parses(expression));
        }

        /// <summary>
        /// Exactly what ServerController.ValidateExpressions and ScriptingHost.ParseScript both do.
        /// </summary>
        private static bool Parses(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
            {
                return true;
            }

            try
            {
                new Esprima.JavaScriptParser().ParseScript(expression);
                return true;
            }
            catch (Esprima.ParserException)
            {
                return false;
            }
        }

        [Fact]
        public void EveryValidationExpressionOnTheApiModelIsChecked()
        {
            // If a fifth expression is ever added to the API model, it has to be validated too, or
            // it reintroduces exactly the silent failure this exists to stop.
            string[] expressionProperties = typeof(ApiModel.Server)
                .GetProperties()
                .Select(p => p.Name)
                .Where(name => name.EndsWith("ValidationExpression"))
                .OrderBy(name => name)
                .ToArray();

            Assert.Equal(
                new[]
                {
                    "CommandValidationExpression",
                    "CredentialsValidationExpression",
                    "MessageValidationExpression",
                    "RecipientValidationExpression",
                },
                expressionProperties);
        }
    }
}
