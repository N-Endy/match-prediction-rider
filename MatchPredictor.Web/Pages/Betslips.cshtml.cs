using MatchPredictor.Application.Services;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace MatchPredictor.Web.Pages;

public class BetslipsModel : PageModel
{
    private readonly IBetslipQueries _betslipQueries;
    private readonly BetslipSettings _settings;

    public BetslipsModel(IBetslipQueries betslipQueries, IOptions<BetslipSettings> betslipOptions)
    {
        _betslipQueries = betslipQueries;
        _settings = betslipOptions.Value;
    }

    public BetslipSet? CurrentSet { get; private set; }
    public string GeneratedLocalLabel { get; private set; } = string.Empty;
    public Betslip? BankerSlip { get; private set; }
    public IReadOnlyList<Betslip> OtherSlips { get; private set; } = [];
    public decimal ReferenceStakeNaira { get; private set; } = 100m;

    public async Task OnGetAsync(CancellationToken ct)
    {
        ReferenceStakeNaira = _settings.ReferenceStakeNaira > 0 ? _settings.ReferenceStakeNaira : 100m;
        ViewData["ReferenceStakeNaira"] = ReferenceStakeNaira;

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
