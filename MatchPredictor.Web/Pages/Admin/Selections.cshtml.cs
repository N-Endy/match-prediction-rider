using MatchPredictor.Domain.Helpers;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Utils;
using MatchPredictor.Web.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace MatchPredictor.Web.Pages.Admin;

[PrivateSelectionsAuth]
public class SelectionsModel : PageModel
{
    private readonly IBetslipQueries _betslipQueries;
    private readonly IBetslipGenerationService _betslipGenerationService;
    private readonly BetslipSettings _settings;
    private readonly ILogger<SelectionsModel> _logger;

    public SelectionsModel(
        IBetslipQueries betslipQueries,
        IBetslipGenerationService betslipGenerationService,
        IOptions<BetslipSettings> betslipOptions,
        ILogger<SelectionsModel> logger)
    {
        _betslipQueries = betslipQueries;
        _betslipGenerationService = betslipGenerationService;
        _settings = betslipOptions.Value;
        _logger = logger;
    }

    public BetslipSet? CurrentSet { get; private set; }
    public IReadOnlyList<Betslip> SynergySlips { get; private set; } = [];
    public string GeneratedLocalLabel { get; private set; } = string.Empty;
    public decimal ReferenceStakeNaira { get; private set; } = 100m;
    public IReadOnlyList<string> AvailableTodayRuns { get; private set; } = [];
    public string SelectedRun { get; private set; } = string.Empty;

    [TempData]
    public string? StatusAlertMessage { get; set; }

    [TempData]
    public bool StatusAlertSuccess { get; set; }

    public async Task OnGetAsync(string? run = null, CancellationToken ct = default)
    {
        ApplyStake();
        await LoadDataAsync(run, ct);
    }

    public async Task<IActionResult> OnPostRefreshAsync(CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Admin requested on-demand regeneration of synergy betslips.");
            await _betslipGenerationService.GenerateDailyBetslipsAsync();
            StatusAlertMessage = "Betslips recalculated and updated successfully!";
            StatusAlertSuccess = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh betslips on demand.");
            StatusAlertMessage = $"Refresh failed: {ex.Message}";
            StatusAlertSuccess = false;
        }

        return RedirectToPage("/Admin/Selections");
    }

    private void ApplyStake()
    {
        ReferenceStakeNaira = _settings.ReferenceStakeNaira > 0 ? _settings.ReferenceStakeNaira : 100m;
        ViewData["ReferenceStakeNaira"] = ReferenceStakeNaira;
    }

    private async Task LoadDataAsync(string? requestedRun, CancellationToken ct)
    {
        var todaySets = await _betslipQueries.GetTodaySetsAsync(ct);
        AvailableTodayRuns = todaySets
            .Select(s => s.RunLabel.ToLowerInvariant())
            .Distinct()
            .ToList();

        if (todaySets.Count > 0)
        {
            if (!string.IsNullOrWhiteSpace(requestedRun))
            {
                CurrentSet = todaySets.FirstOrDefault(s =>
                    string.Equals(s.RunLabel, requestedRun, StringComparison.OrdinalIgnoreCase))
                    ?? todaySets.LastOrDefault(s => s.IsCurrent)
                    ?? todaySets.Last();
            }
            else
            {
                CurrentSet = todaySets.LastOrDefault(s => s.IsCurrent) ?? todaySets.Last();
            }
        }
        else
        {
            CurrentSet = await _betslipQueries.GetCurrentSetAsync(ct);
        }

        if (CurrentSet is null)
        {
            SynergySlips = [];
            return;
        }

        SelectedRun = CurrentSet.RunLabel.ToLowerInvariant();
        var local = DateTimeProvider.ConvertUtcToLocal(CurrentSet.GeneratedAtUtc);
        GeneratedLocalLabel = $"{local:ddd d MMM yyyy, HH:mm} WAT";

        // Extract the 6 private synergy slips
        SynergySlips = CurrentSet.Slips
            .Where(s => s.IsPrivate && BetslipKinds.IsSynergySlip(s))
            .OrderBy(s => s.SlipNumber)
            .ToList();
    }
}
