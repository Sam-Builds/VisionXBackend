using Microsoft.AspNetCore.Mvc;

namespace UIBackend.Api.Controllers;

[ApiController]
[Route("health")]
public class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new
        {
            status = "healthy",
            service = "VisionX UIBackend & Authenticity Service",
            version = "1.0.0",
            timestamp = DateTime.UtcNow
        });
    }
}
