using Microsoft.AspNetCore.Mvc;

namespace SharpAI.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class OnlineController : ControllerBase
    {
        // Services



        // Ctor
        public OnlineController()
        {

        }


        // Endpoints
        [HttpGet("google-translate")]
        public async Task<ActionResult<string>?> GoogleTranslateAsync([FromQuery] string text, [FromQuery] string? originalLanguage = null, [FromQuery] string translateLanguage = "en")
        {
            try
            {
                var response = await SharpAI.Online.GoogleTranslateAccess.TranslateAsync(text, originalLanguage, translateLanguage);
                if (response == null)
                {
                    return this.StatusCode(500, "Failed to translate text.");
                }

                return response;
            }
            catch (Exception ex)
            {
                return this.StatusCode(500, ex.Message);
            }
        }



    }
}
