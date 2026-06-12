using System.Globalization;
using System.Text.Json;
using Hangfire;
using MatchPredictor.Application.Helpers;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MatchPredictor.Application.Services;

public partial class AnalyzerService
{
    public async Task BackfillStoredPredictionTimesAsync(int lookbackDays = 90)
    {
        var today = DateTimeProvider.GetLocalDate();
        var dates = Enumerable.Range(0, Math.Max(lookbackDays, 0) + 1)
            .Select(offset => today.AddDays(-offset))
            .ToHashSet();

        var matches = await _dbContext.MatchDatas
            .Where(match => match.MatchLocalDate.HasValue && dates.Contains(match.MatchLocalDate.Value))
            .ToListAsync();

        if (matches.Count == 0)
        {
            return;
        }

        var predictions = await _dbContext.Predictions
            .Where(prediction => dates.Contains(prediction.MatchLocalDate))
            .ToListAsync();

        var forecasts = await _dbContext.ForecastObservations
            .Where(forecast => dates.Contains(forecast.MatchLocalDate))
            .ToListAsync();

        var matchLookup = matches
            .GroupBy(match => (
                Date: match.MatchLocalDate ?? default,
                Home: Norm(match.HomeTeam),
                Away: Norm(match.AwayTeam),
                League: Norm(match.League)))
            .ToDictionary(group => group.Key, group => group.OrderBy(m => m.MatchDateTime).First());

        var teamLookup = matches
            .GroupBy(match => (
                Home: Norm(match.HomeTeam),
                Away: Norm(match.AwayTeam),
                League: Norm(match.League)))
            .ToDictionary(group => group.Key, group => group.OrderBy(m => m.MatchDateTime).ToList());

        var updatedPredictions = 0;
        var updatedForecasts = 0;

        foreach (var prediction in predictions)
        {
            var matched = FindMatchingMatchData(prediction.MatchLocalDate, prediction.HomeTeam, prediction.AwayTeam, prediction.League, prediction.MatchDateTime, matchLookup, teamLookup);
            if (matched != null && ApplyStoredTime(prediction, matched))
            {
                updatedPredictions++;
            }
        }

        foreach (var forecast in forecasts)
        {
            var matched = FindMatchingMatchData(forecast.MatchLocalDate, forecast.HomeTeam, forecast.AwayTeam, forecast.League, forecast.MatchDateTime, matchLookup, teamLookup);
            if (matched != null && ApplyStoredTime(forecast, matched))
            {
                updatedForecasts++;
            }
        }

        if (updatedPredictions > 0 || updatedForecasts > 0)
        {
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation(
                "Repaired stored kickoff times for {PredictionCount} predictions and {ForecastCount} forecasts.",
                updatedPredictions,
                updatedForecasts);
        }
    }

    private async Task BackfillCanonicalFixtureFieldsAsync(int lookbackDays = 90)
    {
        var today = DateTimeProvider.GetLocalDate();
        var dates = Enumerable.Range(0, Math.Max(lookbackDays, 0) + 1)
            .Select(offset => today.AddDays(-offset))
            .ToHashSet();

        var matches = await _dbContext.MatchDatas
            .Where(match =>
                (match.MatchLocalDate.HasValue && dates.Contains(match.MatchLocalDate.Value)) ||
                (!match.MatchLocalDate.HasValue && match.Date != null))
            .ToListAsync();
        var predictions = await _dbContext.Predictions
            .Where(prediction => dates.Contains(prediction.MatchLocalDate) || prediction.MatchLocalDate == default)
            .ToListAsync();
        var forecasts = await _dbContext.ForecastObservations
            .Where(forecast => dates.Contains(forecast.MatchLocalDate) || forecast.MatchLocalDate == default)
            .ToListAsync();

        var updatedMatches = 0;
        foreach (var match in matches)
        {
            if (ApplyCanonicalFixtureIdentity(match))
            {
                updatedMatches++;
            }
        }

        var updatedPredictions = 0;
        foreach (var prediction in predictions)
        {
            if (ApplyCanonicalFixtureIdentity(prediction))
            {
                updatedPredictions++;
            }
        }

        var updatedForecasts = 0;
        foreach (var forecast in forecasts)
        {
            if (ApplyCanonicalFixtureIdentity(forecast))
            {
                updatedForecasts++;
            }
        }

        if (updatedMatches > 0 || updatedPredictions > 0 || updatedForecasts > 0)
        {
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation(
                "Backfilled canonical fixture fields for {MatchCount} match rows, {PredictionCount} predictions, and {ForecastCount} forecasts.",
                updatedMatches,
                updatedPredictions,
                updatedForecasts);
        }
    }

    public async Task BackfillDecisionProvenanceAsync(int lookbackDays = 90)
    {
        var today = DateTimeProvider.GetLocalDate();
        var dates = Enumerable.Range(0, Math.Max(lookbackDays, 0) + 1)
            .Select(offset => today.AddDays(-offset))
            .ToHashSet();

        var predictions = await _dbContext.Predictions
            .Where(prediction =>
                dates.Contains(prediction.MatchLocalDate) &&
                (string.IsNullOrEmpty(prediction.CalibratorUsed) ||
                 prediction.CalibratorUsed == "Unknown" ||
                 string.IsNullOrEmpty(prediction.ThresholdSource) ||
                 prediction.ThresholdSource == "Unknown" ||
                 prediction.ThresholdUsed <= 0 ||
                 !prediction.WasPublished))
            .ToListAsync();

        var forecasts = await _dbContext.ForecastObservations
            .Where(forecast =>
                dates.Contains(forecast.MatchLocalDate) &&
                (string.IsNullOrEmpty(forecast.CalibratorUsed) ||
                 forecast.CalibratorUsed == "Unknown" ||
                 string.IsNullOrEmpty(forecast.ThresholdSource) ||
                 forecast.ThresholdSource == "Unknown" ||
                 forecast.ThresholdUsed <= 0))
            .ToListAsync();

        var updatedPredictions = 0;
        foreach (var prediction in predictions)
        {
            if (!TryResolvePredictionMarket(prediction, out var market))
            {
                continue;
            }

            if (ApplyDecisionBackfill(prediction, market))
            {
                updatedPredictions++;
            }
        }

        var updatedForecasts = 0;
        foreach (var forecast in forecasts)
        {
            if (ApplyDecisionBackfill(forecast, forecast.Market))
            {
                updatedForecasts++;
            }
        }

        if (updatedPredictions > 0 || updatedForecasts > 0)
        {
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation(
                "Backfilled decision provenance for {PredictionCount} predictions and {ForecastCount} forecasts.",
                updatedPredictions,
                updatedForecasts);
        }
    }
    private bool ApplyDecisionBackfill(Prediction prediction, PredictionMarket market)
    {
        var updated = false;

        if (string.IsNullOrWhiteSpace(prediction.CalibratorUsed) || prediction.CalibratorUsed == "Unknown")
        {
            prediction.CalibratorUsed = "Bucket";
            updated = true;
        }

        if (prediction.ThresholdUsed <= 0)
        {
            prediction.ThresholdUsed = ResolveFallbackThreshold(market);
            updated = true;
        }

        if (string.IsNullOrWhiteSpace(prediction.ThresholdSource) || prediction.ThresholdSource == "Unknown")
        {
            prediction.ThresholdSource = "Configured";
            updated = true;
        }

        if (!prediction.WasPublished)
        {
            prediction.WasPublished = true;
            updated = true;
        }

        return updated;
    }

    private bool ApplyDecisionBackfill(ForecastObservation forecast, PredictionMarket market)
    {
        var updated = false;

        if (string.IsNullOrWhiteSpace(forecast.CalibratorUsed) || forecast.CalibratorUsed == "Unknown")
        {
            forecast.CalibratorUsed = "Bucket";
            updated = true;
        }

        if (forecast.ThresholdUsed <= 0)
        {
            forecast.ThresholdUsed = ResolveFallbackThreshold(market);
            updated = true;
        }

        if (string.IsNullOrWhiteSpace(forecast.ThresholdSource) || forecast.ThresholdSource == "Unknown")
        {
            forecast.ThresholdSource = "Configured";
            updated = true;
        }

        return updated;
    }

    private bool TryResolvePredictionMarket(Prediction prediction, out PredictionMarket market)
    {
        market = prediction.PredictionCategory switch
        {
            "BothTeamsScore" => PredictionMarket.BothTeamsScore,
            "Over2.5Goals" => PredictionMarket.Over25Goals,
            "Under2.5Goals" => PredictionMarket.Under25Goals,
            "Draw" => PredictionMarket.Draw,
            "StraightWin" when prediction.PredictedOutcome == "Home Win" => PredictionMarket.HomeWin,
            "StraightWin" when prediction.PredictedOutcome == "Away Win" => PredictionMarket.AwayWin,
            _ => default
        };

        return prediction.PredictionCategory is "BothTeamsScore" or "Over2.5Goals" or "Under2.5Goals" or "Draw" ||
               (prediction.PredictionCategory == "StraightWin" && prediction.PredictedOutcome is "Home Win" or "Away Win");
    }

    private double ResolveFallbackThreshold(PredictionMarket market)
    {
        return _predictionSettings.ResolveFallbackThreshold(market);
    }
    private static MatchData? FindMatchingMatchData(
        DateOnly date,
        string homeTeam,
        string awayTeam,
        string league,
        DateTime? matchDateTime,
        IReadOnlyDictionary<(DateOnly Date, string Home, string Away, string League), MatchData> datedMatches,
        IReadOnlyDictionary<(string Home, string Away, string League), List<MatchData>> teamMatches)
    {
        var datedKey = (
            Date: date,
            Home: Norm(homeTeam),
            Away: Norm(awayTeam),
            League: Norm(league));

        if (datedMatches.TryGetValue(datedKey, out var exactMatch))
        {
            return exactMatch;
        }

        var teamKey = (
            Home: Norm(homeTeam),
            Away: Norm(awayTeam),
            League: Norm(league));

        if (!teamMatches.TryGetValue(teamKey, out var candidates) || candidates.Count == 0)
        {
            return null;
        }

        if (matchDateTime.HasValue)
        {
            return candidates
                .OrderBy(candidate => candidate.MatchDateTime.HasValue
                    ? Math.Abs((candidate.MatchDateTime.Value - matchDateTime.Value).TotalMinutes)
                    : double.MaxValue)
                .FirstOrDefault();
        }

        return candidates.FirstOrDefault();
    }

    private static bool ApplyStoredTime(Prediction prediction, MatchData match)
    {
        var updated = false;

        if (!string.Equals(prediction.Date, match.Date, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(match.Date))
        {
            prediction.Date = match.Date!;
            updated = true;
        }

        if (!string.Equals(prediction.Time, match.Time, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(match.Time))
        {
            prediction.Time = match.Time!;
            updated = true;
        }

        if (prediction.MatchDateTime != match.MatchDateTime && match.MatchDateTime.HasValue)
        {
            prediction.MatchDateTime = match.MatchDateTime;
            updated = true;
        }

        if (match.MatchLocalDate.HasValue && prediction.MatchLocalDate != match.MatchLocalDate.Value)
        {
            prediction.MatchLocalDate = match.MatchLocalDate.Value;
            updated = true;
        }

        if (prediction.MatchLocalTime != match.MatchLocalTime)
        {
            prediction.MatchLocalTime = match.MatchLocalTime;
            updated = true;
        }

        if (!string.Equals(prediction.FixtureKey, match.FixtureKey, StringComparison.Ordinal))
        {
            prediction.FixtureKey = match.FixtureKey;
            updated = true;
        }

        return updated;
    }

    private static bool ApplyStoredTime(ForecastObservation forecast, MatchData match)
    {
        var updated = false;

        if (!string.Equals(forecast.Date, match.Date, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(match.Date))
        {
            forecast.Date = match.Date!;
            updated = true;
        }

        if (!string.Equals(forecast.Time, match.Time, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(match.Time))
        {
            forecast.Time = match.Time!;
            updated = true;
        }

        if (forecast.MatchDateTime != match.MatchDateTime && match.MatchDateTime.HasValue)
        {
            forecast.MatchDateTime = match.MatchDateTime;
            updated = true;
        }

        if (match.MatchLocalDate.HasValue && forecast.MatchLocalDate != match.MatchLocalDate.Value)
        {
            forecast.MatchLocalDate = match.MatchLocalDate.Value;
            updated = true;
        }

        if (forecast.MatchLocalTime != match.MatchLocalTime)
        {
            forecast.MatchLocalTime = match.MatchLocalTime;
            updated = true;
        }

        if (!string.Equals(forecast.FixtureKey, match.FixtureKey, StringComparison.Ordinal))
        {
            forecast.FixtureKey = match.FixtureKey;
            updated = true;
        }

        return updated;
    }

    private static bool ApplyCanonicalFixtureIdentity(MatchData match)
    {
        var identity = FixtureIdentityFactory.FromMatchData(match);
        var updated = false;

        if (identity.MatchLocalDate.HasValue && match.MatchLocalDate != identity.MatchLocalDate)
        {
            match.MatchLocalDate = identity.MatchLocalDate;
            updated = true;
        }

        if (match.MatchLocalTime != identity.MatchLocalTime)
        {
            match.MatchLocalTime = identity.MatchLocalTime;
            updated = true;
        }

        if (identity.MatchLocalDate.HasValue)
        {
            var legacyDate = DateTimeProvider.FormatLocalDate(identity.MatchLocalDate.Value);
            if (!string.Equals(match.Date, legacyDate, StringComparison.Ordinal))
            {
                match.Date = legacyDate;
                updated = true;
            }
        }

        if (identity.MatchLocalTime.HasValue)
        {
            var legacyTime = DateTimeProvider.FormatLocalTime(identity.MatchLocalTime.Value);
            if (!string.Equals(match.Time, legacyTime, StringComparison.Ordinal))
            {
                match.Time = legacyTime;
                updated = true;
            }
        }

        if (!string.Equals(match.FixtureKey, identity.FixtureKey, StringComparison.Ordinal))
        {
            match.FixtureKey = identity.FixtureKey;
            updated = true;
        }

        return updated;
    }

    private static bool ApplyCanonicalFixtureIdentity(Prediction prediction)
    {
        var identity = FixtureIdentityFactory.FromPrediction(prediction);
        var updated = false;

        if (prediction.MatchLocalDate == default && identity.MatchLocalDate.HasValue)
        {
            prediction.MatchLocalDate = identity.MatchLocalDate.Value;
            updated = true;
        }

        if (prediction.MatchLocalTime != identity.MatchLocalTime)
        {
            prediction.MatchLocalTime = identity.MatchLocalTime;
            updated = true;
        }

        if (prediction.MatchLocalDate != default)
        {
            var legacyDate = DateTimeProvider.FormatLocalDate(prediction.MatchLocalDate);
            if (!string.Equals(prediction.Date, legacyDate, StringComparison.Ordinal))
            {
                prediction.Date = legacyDate;
                updated = true;
            }
        }

        if (prediction.MatchLocalTime.HasValue)
        {
            var legacyTime = DateTimeProvider.FormatLocalTime(prediction.MatchLocalTime.Value);
            if (!string.Equals(prediction.Time, legacyTime, StringComparison.Ordinal))
            {
                prediction.Time = legacyTime;
                updated = true;
            }
        }

        if (!string.Equals(prediction.FixtureKey, identity.FixtureKey, StringComparison.Ordinal))
        {
            prediction.FixtureKey = identity.FixtureKey;
            updated = true;
        }

        return updated;
    }

    private static bool ApplyCanonicalFixtureIdentity(ForecastObservation forecast)
    {
        var identity = FixtureIdentityFactory.FromForecast(forecast);
        var updated = false;

        if (forecast.MatchLocalDate == default && identity.MatchLocalDate.HasValue)
        {
            forecast.MatchLocalDate = identity.MatchLocalDate.Value;
            updated = true;
        }

        if (forecast.MatchLocalTime != identity.MatchLocalTime)
        {
            forecast.MatchLocalTime = identity.MatchLocalTime;
            updated = true;
        }

        if (forecast.MatchLocalDate != default)
        {
            var legacyDate = DateTimeProvider.FormatLocalDate(forecast.MatchLocalDate);
            if (!string.Equals(forecast.Date, legacyDate, StringComparison.Ordinal))
            {
                forecast.Date = legacyDate;
                updated = true;
            }
        }

        if (forecast.MatchLocalTime.HasValue)
        {
            var legacyTime = DateTimeProvider.FormatLocalTime(forecast.MatchLocalTime.Value);
            if (!string.Equals(forecast.Time, legacyTime, StringComparison.Ordinal))
            {
                forecast.Time = legacyTime;
                updated = true;
            }
        }

        if (!string.Equals(forecast.FixtureKey, identity.FixtureKey, StringComparison.Ordinal))
        {
            forecast.FixtureKey = identity.FixtureKey;
            updated = true;
        }

        return updated;
    }

    private static bool ApplyCanonicalFixtureIdentity(PredictionCandidate candidate)
    {
        var localDate = candidate.MatchLocalDate != default
            ? candidate.MatchLocalDate
            : DateTimeProvider.ParseLocalDateOrNull(candidate.Date) ?? default;
        var localTime = candidate.MatchLocalTime ?? DateTimeProvider.ParseLocalTimeOrNull(candidate.Time);
        var matchDateTime = candidate.MatchDateTime;

        if (matchDateTime is null && localDate != default)
        {
            var inferredLocalDateTime = localDate.ToDateTime(localTime ?? new TimeOnly(0, 0), DateTimeKind.Unspecified);
            matchDateTime = DateTimeProvider.ConvertLocalToUtc(inferredLocalDateTime);
        }

        var identity = FixtureIdentityFactory.Build(
            candidate.HomeTeam,
            candidate.AwayTeam,
            candidate.League,
            localDate == default ? null : localDate,
            localTime,
            matchDateTime);

        var updated = false;

        if (candidate.MatchLocalDate == default && localDate != default)
        {
            candidate.MatchLocalDate = localDate;
            updated = true;
        }

        if (candidate.MatchLocalTime != localTime)
        {
            candidate.MatchLocalTime = localTime;
            updated = true;
        }

        if (candidate.MatchDateTime != matchDateTime)
        {
            candidate.MatchDateTime = matchDateTime;
            updated = true;
        }

        if (candidate.MatchLocalDate != default)
        {
            var legacyDate = DateTimeProvider.FormatLocalDate(candidate.MatchLocalDate);
            if (!string.Equals(candidate.Date, legacyDate, StringComparison.Ordinal))
            {
                candidate.Date = legacyDate;
                updated = true;
            }
        }

        if (candidate.MatchLocalTime.HasValue)
        {
            var legacyTime = DateTimeProvider.FormatLocalTime(candidate.MatchLocalTime.Value);
            if (!string.Equals(candidate.Time, legacyTime, StringComparison.Ordinal))
            {
                candidate.Time = legacyTime;
                updated = true;
            }
        }

        if (!string.Equals(candidate.FixtureKey, identity.FixtureKey, StringComparison.Ordinal))
        {
            candidate.FixtureKey = identity.FixtureKey;
            updated = true;
        }

        return updated;
    }

}
