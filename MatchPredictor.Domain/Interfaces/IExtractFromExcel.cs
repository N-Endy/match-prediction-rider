using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IExtractFromExcel
{
    IEnumerable<MatchData> ExtractMatchDatasetFromFile();
}