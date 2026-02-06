using Microsoft.AspNetCore.Mvc;
using SharpAI.Core;

namespace SharpAI.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LmStudioController : ControllerBase
    {
        private readonly LmStudioService LmStudio;

        public LmStudioController(LmStudioService lmStudio)
        {
            this.LmStudio = lmStudio;
        }



    }
}
