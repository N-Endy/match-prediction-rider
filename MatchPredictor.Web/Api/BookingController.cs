using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using Microsoft.AspNetCore.Mvc;

namespace MatchPredictor.Web.Api;

[ApiController]
[Route("api/booking")]
public class BookingController : ControllerBase
{
    private readonly ISportyBetBookingService _bookingService;

    public BookingController(ISportyBetBookingService bookingService)
    {
        _bookingService = bookingService;
    }

    [HttpPost("book")]
    public async Task<IActionResult> Book([FromBody] BookingRequest request)
    {
        if (request.Selections.Count == 0)
        {
            return BadRequest(new BookingResult { Success = false, Message = "No games selected." });
        }

        var result = await _bookingService.BookGamesAsync(request.Selections);
        return Ok(result);
    }
}
