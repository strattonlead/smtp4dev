using Microsoft.AspNetCore.Mvc;
using NSwag.Annotations;
using Rnwood.Smtp4dev.Server;

namespace Rnwood.Smtp4dev.Controllers
{
    /// <summary>
    /// Runtime numbers a operator or a front end needs to see, as opposed to settings.
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    public class MetricsController : Controller
    {
        /// <summary>
        /// Gets the server's current runtime metrics.
        /// </summary>
        [HttpGet]
        [SwaggerResponse(System.Net.HttpStatusCode.OK, typeof(ApiModel.Metrics), Description = "")]
        public ApiModel.Metrics GetMetrics()
        {
            return new ApiModel.Metrics
            {
                BlockedConnections = ScriptingHost.BlockedConnections,
            };
        }
    }
}
