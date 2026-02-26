using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface ISportyBetBookingService
{
    Task<BookingResult> BookGamesAsync(List<BookingSelection> selections);
}
