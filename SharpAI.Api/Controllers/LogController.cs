using Microsoft.AspNetCore.Mvc;
using SharpAI.Core;
using System.Linq;
using System.Collections.Generic;
using SharpAI.Shared;

namespace SharpAI.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LogController : ControllerBase
    {
        private readonly Appsettings _appsettings;

        public LogController(Appsettings appsettings)
        {
            this._appsettings = appsettings;
        }


        [HttpGet("appsettings")]
        public ActionResult<Appsettings>? GetAppsettings()
        {
            try
            {
                return this.Ok(this._appsettings);
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"[Error] Exception in GetAppSettings: {ex}");
                return this.StatusCode(500, "Error retrieving app settings");
            }
        }


        [HttpGet("log/binding")]
        public ActionResult<List<string>?> GetBindingLog()
        {
            try
            {
                // Take a thread-safe snapshot of the BindingList
                List<string> snapshot;
                lock (StaticLogger.LogEntriesBindingList)
                {
                    snapshot = StaticLogger.LogEntriesBindingList.ToList();
                }

                return this.Ok(snapshot);
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"[Error] Exception in GetLog: {ex}");
                return this.StatusCode(500, "Error reading logs");
            }
        }

        [HttpGet("log/entries")]
        public ActionResult<Dictionary<DateTime, string>?> GetEntriesLog()
        {
            try
            {
                var dict = StaticLogger.LogEntries.ToDictionary(entry => entry.Key, entry => entry.Value);
                return this.Ok(dict);
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"[Error] Exception in GetEntriesLog: {ex}");
                return this.StatusCode(500, "Error reading logs");
            }
        }

        [HttpPost("log/message")]
        public async Task<IActionResult> LogMessageAsync([FromBody] string logMessage = "")
        {
            try
            {
                await StaticLogger.LogAsync(logMessage);
                return this.Ok(true);
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"[Error] Exception in DoLogAsync: {ex}");
                return this.StatusCode(500, "Error logging message");
            }
        }


    }
}
