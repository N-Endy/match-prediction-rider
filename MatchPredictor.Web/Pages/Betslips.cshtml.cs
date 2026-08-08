using MatchPredictor.Application.Services;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatchPredictor.Web.Pages;

public class BetslipsModel : PageModel
{
    private readonly IBetslipQueries _betslipQueries;

    public BetslipsModel(IBetslipQueries betslipQueries)
    {
        _betslipQueries = betslipQueries;
    }

    public BetslipSet? CurrentSet { get; private set; }
    public string GeneratedLocalLabel { get; private set; } = string.Empty;
    public Betslip? BankerSlip { get; private set; }
    public IReadOnlyList<Betslip> OtherSlips { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        CurrentSet = await _betslipQueries.GetCurrentSetAsync(ct);
        if (CurrentSet is null)
        {
            return;
        }

        var local = DateTimeProvider.ConvertUtcToLocal(CurrentSet.GeneratedAtUtc);
        GeneratedLocalLabel = $"{local:ddd d MMM yyyy, HH:mm} WAT";

        BankerSlip = CurrentSet.Slips
            .FirstOrDefault(s => s.SlipNumber == BetslipGenerationService.BankerSlipNumber
                                 || s.TierLabel.StartsWith("Banker", StringComparison.OrdinalIgnoreCase));

        OtherSlips = CurrentSet.Slips
            .Where(s => s != BankerSlip)
            .OrderBy(s => s.SlipNumber)
            .ToList();
    }
}
