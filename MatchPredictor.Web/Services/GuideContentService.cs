namespace MatchPredictor.Web.Services;

public class GuideArticle
{
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string ReadingTime { get; set; } = "6 min read";
    public string PublishedDate { get; set; } = "September 2026";
    public string AuthorName { get; set; } = "MatchPredictor Quantitative Research";
    public string AuthorRole { get; set; } = "Predictive Modeling & Statistical Analysis Team";
    public string ContentHtml { get; set; } = string.Empty;
}

public interface IGuideContentService
{
    IReadOnlyList<GuideArticle> GetAllGuides();
    GuideArticle? GetGuideBySlug(string slug);
}

public class GuideContentService : IGuideContentService
{
    private static readonly List<GuideArticle> Guides =
    [
        new GuideArticle
        {
            Slug = "the-math-of-football-predictions-poisson-and-dixon-coles",
            Title = "The Mathematics of Football: How Poisson & Dixon-Coles Predict Scorelines",
            Category = "Statistical Modeling",
            Summary = "A rigorous breakdown of bivariate Poisson processes, attack/defence intensity parameters, and the Dixon-Coles low-scoreline correction.",
            ReadingTime = "7 min read",
            PublishedDate = "15 Sep 2026",
            ContentHtml = """
                <h2>1. The Foundation: Football as a Counting Process</h2>
                <p>
                    Predicting football (soccer) outcomes begins with a fundamental realization: football is a low-scoring sport governed by stochastic arrival processes. Across major European leagues, the average match yields approximately 2.6 to 2.8 goals. Because goals occur as discrete, relatively rare events across a continuous 90-minute timeline, the standard baseline mathematical model is the <strong>Poisson Distribution</strong>.
                </p>
                <p>
                    Under a simple Poisson assumption, the probability of a team scoring exactly <em>k</em> goals in a match given an expected goal rate &lambda; is expressed as:
                </p>
                <div class="mp-article-callout">
                    <p><strong>P(X = k) = (&lambda;<sup>k</sup> &times; e<sup>-&lambda;</sup>) / k!</strong></p>
                </div>
                <p>
                    Here, &lambda; represents the team's expected goals (xG). For instance, if Home Team A has an expected goal average of 1.75 against Away Team B, the probability of them scoring exactly 2 goals is:
                    <code>(1.75² × e⁻¹·⁷⁵) / 2! ≈ 26.6%</code>.
                </p>

                <h2>2. Attack &amp; Defence Strength Parameters</h2>
                <p>
                    To determine &lambda; for both sides, modern predictive frameworks calculate four core variables:
                </p>
                <ul>
                    <li><strong>Home Attack Strength (&alpha;<sub>h</sub>):</strong> The ratio of goals scored at home compared to the league average.</li>
                    <li><strong>Away Defence Weakness (&beta;<sub>a</sub>):</strong> The ratio of goals conceded away compared to the league average.</li>
                    <li><strong>Home Advantage Factor (&gamma;):</strong> The historical statistical uplift granted by home pitch, crowd familiarity, and travel fatigue on the visitor.</li>
                    <li><strong>Away Attack (&alpha;<sub>a</sub>) &amp; Home Defence (&beta;<sub>h</sub>):</strong> The corresponding metrics for the opposite fixture direction.</li>
                </ul>
                <p>
                    The expected goal intensities are then calculated as:
                    <br><code>&lambda; = &alpha;<sub>h</sub> &times; &beta;<sub>a</sub> &times; &gamma; &times; LeagueAvgHomeGoals</code>
                    <br><code>&mu; = &alpha;<sub>a</sub> &times; &beta;<sub>h</sub> &times; LeagueAvgAwayGoals</code>
                </p>

                <h2>3. The Independence Flaw &amp; The Dixon-Coles Solution</h2>
                <p>
                    While pure double Poisson models are elegant, they suffer from a well-known empirical defect: <strong>the assumption of independence</strong>. In actual football matches, scorelines of 0-0, 1-0, 0-1, and 1-1 occur more frequently than independent probability multiplication predicts. When a team scores early, game state dynamics change: the leading team often retreats into a low-block defensive posture, reducing the scoring rate for the remainder of the fixture.
                </p>
                <p>
                    In their seminal 1997 paper, statisticians Mark Dixon and Stuart Coles introduced an adjustment factor &tau; (tau) that specifically recalibrates low-scoring outcomes:
                </p>
                <ul>
                    <li><strong>0 - 0:</strong> Multiplied by <code>1 - &lambda;&mu;&rho;</code></li>
                    <li><strong>1 - 0:</strong> Multiplied by <code>1 + &mu;&rho;</code></li>
                    <li><strong>0 - 1:</strong> Multiplied by <code>1 + &lambda;&rho;</code></li>
                    <li><strong>1 - 1:</strong> Multiplied by <code>1 - &rho;</code></li>
                </ul>
                <p>
                    Where &rho; (rho) is a correlation parameter estimated across thousands of historical league matches (typically around -0.11 to -0.14). All higher scorelines (2-0, 2-1, etc.) maintain their Poisson independence factor.
                </p>

                <h2>4. Generating Market Probabilities From the Scoreline Matrix</h2>
                <p>
                    Once the adjusted 10&times;10 scoreline probability grid is computed, determining betting market distributions is mathematically straightforward:
                </p>
                <ul>
                    <li><strong>Home Win (1):</strong> Sum of all cells where Home Goals &gt; Away Goals.</li>
                    <li><strong>Draw (X):</strong> Sum of diagonal cells (0-0, 1-1, 2-2, 3-3, ...).</li>
                    <li><strong>Away Win (2):</strong> Sum of all cells where Away Goals &gt; Home Goals.</li>
                    <li><strong>Over 2.5 Goals:</strong> Sum of all cells where Home Goals + Away Goals &ge; 3.</li>
                    <li><strong>Both Teams to Score (BTTS):</strong> Sum of all cells where Home Goals &ge; 1 AND Away Goals &ge; 1.</li>
                </ul>
                <p>
                    At MatchPredictor, Dixon-Coles provides our structural baseline. However, mathematical theory alone does not guarantee a publishing edge—which is why this matrix is subsequently blended with margin-removed market odds and calibrated via Brier scoring before any pick reaches our public card.
                </p>
                """
        },
        new GuideArticle
        {
            Slug = "understanding-market-odds-margin-removal-and-shins-method",
            Title = "Why Bookmaker Odds Are Biased: Margin Removal & Shin's Method",
            Category = "Market Analytics",
            Summary = "How the bookmaker overround distorts raw prices, and how Shin's model separates informed insider action from public noise.",
            ReadingTime = "6 min read",
            PublishedDate = "18 Sep 2026",
            ContentHtml = """
                <h2>1. The Myth of "Fair Odds"</h2>
                <p>
                    A common misconception among sports enthusiasts is that bookmaker odds directly represent the true likelihood of a match outcome. In reality, bookmaker odds are commercial product prices designed to guarantee a risk-mitigated profit margin regardless of who wins.
                </p>
                <p>
                    Consider a standard 1X2 market:
                </p>
                <ul>
                    <li>Home Win: 2.10 (Implied: 1 / 2.10 = 47.6%)</li>
                    <li>Draw: 3.30 (Implied: 1 / 3.30 = 30.3%)</li>
                    <li>Away Win: 3.60 (Implied: 1 / 3.60 = 27.8%)</li>
                </ul>
                <p>
                    Summing these implied probabilities gives: <code>47.6% + 30.3% + 27.8% = 105.7%</code>.
                    The extra <strong>5.7%</strong> is the bookmaker's overround, commonly known as the <em>vig</em> or <em>margin</em>. If you evaluate raw bookmaker prices without stripping this margin, your statistical models are systematically calibrated against an artificial bias.
                </p>

                <h2>2. The Flaw of Proportional Margin Removal</h2>
                <p>
                    The naive method of removing overround is proportional normalization: dividing each implied probability by the total sum (1.057). While simple, financial economists have proven that bookmakers do <strong>not</strong> distribute their margin equally across all outcomes.
                </p>
                <p>
                    Instead, sports betting markets exhibit a well-documented behavioral anomaly known as the <strong>Favorite-Longshot Bias</strong>:
                </p>
                <div class="mp-article-callout">
                    <p>
                        Casual bettors systematically overvalue longshots (underdogs and draws) and undervalue heavy favorites. Consequently, bookmakers load a disproportionately large slice of their margin onto underdogs while offering tighter margins on heavy favorites.
                    </p>
                </div>

                <h2>3. Shin's Model: Accounting for Asymmetric Information</h2>
                <p>
                    In 1991 and 1993, economist Hyun Song Shin published a breakthrough model explaining how bookmaker prices are structured when facing two distinct classes of participants:
                </p>
                <ol>
                    <li><strong>Noise Traders:</strong> Uninformed public bettors who bet for entertainment or follow emotional loyalties.</li>
                    <li><strong>Informed Traders:</strong> Sharp syndicates with superior non-public information (lineup leaks, tactical intelligence, or proprietary machine learning models).</li>
                </ol>
                <p>
                    Shin's model introduces a parameter <code>z</code>, representing the probability that a bet comes from an informed insider. When bookmakers protect themselves against informed traders, they depress the odds of all outcomes non-linearly according to the square root of true probability:
                </p>
                <p>
                    <code>&pi;<sub>i</sub> = (&radic;(z² + 4(1 - z) &times; (q<sub>i</sub>² / &sum; q<sub>j</sub>)) - z) / (2(1 - z))</code>
                </p>
                <p>
                    By solving for <code>z</code> using iterative root-finding techniques (such as Brent's method or binary search), Shin's normalization strips away the favorite-longshot bias far more accurately than proportional scaling.
                </p>

                <h2>4. The MatchPredictor Implementation</h2>
                <p>
                    At MatchPredictor, every market feed we ingest first undergoes automated Shin de-vigging. Only after the margin and asymmetric information bias have been removed do we compare the market's consensus probability with our own independent Dixon-Coles projection. A prediction is considered for publication only when our calibrated edge demonstrates statistically significant divergence from this purified market baseline.
                </p>
                """
        },
        new GuideArticle
        {
            Slug = "probability-calibration-and-brier-score-explained",
            Title = "Probability Calibration & Brier Scores: How Models Learn From Errors",
            Category = "Machine Learning",
            Summary = "Why accuracy percentage is a misleading metric in sports forecasting, and how Brier score holdout evaluation guarantees honest probabilities.",
            ReadingTime = "8 min read",
            PublishedDate = "20 Sep 2026",
            ContentHtml = """
                <h2>1. Why "Accuracy Percentage" is Deceptive</h2>
                <p>
                    In public sports discussions, prediction services frequently boast claims like: <em>"Our model has an 82% win rate!"</em>
                    To a statistician, such claims are meaningless without contextual probability distributions.
                </p>
                <p>
                    For instance, a naive model that predicts Paris Saint-Germain to win every single home match in Ligue 1 might achieve an 80% hit rate. Yet if the average market odds for PSG are 1.15, a bettor following that model blindly would suffer a guaranteed catastrophic loss. In probabilistic forecasting, <strong>calibration</strong> is far more critical than raw hit rate.
                </p>

                <h2>2. What is Probability Calibration?</h2>
                <p>
                    A forecast model is well-calibrated if its stated confidence matches empirical reality over a large sample.
                    Specifically:
                </p>
                <div class="mp-article-callout">
                    <p>
                        Across all matches where the model assigns an <strong>70% probability</strong> of a home win, exactly <strong>70 out of 100</strong> of those fixtures must result in a home win.
                    </p>
                </div>
                <p>
                    If only 55 out of 100 actually win, the model is <em>overconfident</em>. If 85 win, the model is <em>underconfident</em>. Uncalibrated machine learning models (such as deep neural networks or raw gradient-boosted trees) are notorious for outputting uncalibrated probabilities near 0.0 and 1.0.
                </p>

                <h2>3. The Brier Score Metric</h2>
                <p>
                    To objectively quantify forecasting quality, statisticians use the <strong>Brier Score</strong>, formulated by Glenn W. Brier in 1950. The Brier score measures the mean squared error between predicted probabilities and actual binary outcomes:
                </p>
                <p>
                    <code>BS = (1 / N) &times; &sum;<sub>t=1</sub><sup>N</sup> (f<sub>t</sub> - o<sub>t</sub>)²</code>
                </p>
                <p>
                    Where:
                </p>
                <ul>
                    <li><code>f<sub>t</sub></code> is the forecast probability (e.g. 0.65).</li>
                    <li><code>o<sub>t</sub></code> is the actual outcome (1 if the event occurred, 0 if it failed).</li>
                    <li>A score of <strong>0.0</strong> represents perfect foreknowledge (predicting 1.0 on winners and 0.0 on losers).</li>
                    <li>A score of <strong>0.25</strong> is the benchmark of pure coin-tossing ignorance on a 50/50 proposition.</li>
                </ul>

                <h2>4. Nightly Recalibration &amp; Promotion Gates</h2>
                <p>
                    At MatchPredictor, our system does not rely on static assumptions. Every night, our background settlement engine matches finished fixtures against our historical forecast records.
                </p>
                <p>
                    We apply two distinct calibration architectures:
                </p>
                <ol>
                    <li><strong>Isotonic Regression (Non-parametric):</strong> Fits a non-decreasing step function to calibrate probability intervals into monotonic bins.</li>
                    <li><strong>Beta Calibration (Parametric):</strong> Transforms probabilities using log-odds ratios to smoothly adjust extreme tails.</li>
                </ol>
                <p>
                    Crucially, candidate calibration models are <strong>never promoted into production</strong> unless they beat the incumbent model on held-out out-of-sample Brier score evaluations. This self-learning mechanism guarantees that if league scoring trends shift mid-season, our thresholds automatically adjust to protect forecasting integrity.
                </p>
                """
        },
        new GuideArticle
        {
            Slug = "both-teams-to-score-btts-statistical-modeling",
            Title = "Both Teams to Score (BTTS): Beyond Simple Win/Loss Records",
            Category = "Market Deep Dive",
            Summary = "Why league standings deceive BTTS analysis, and how possession pace, shot conversion, and defensive vulnerability dictate goal exchange.",
            ReadingTime = "6 min read",
            PublishedDate = "22 Sep 2026",
            ContentHtml = """
                <h2>1. The Independent Nature of the BTTS Market</h2>
                <p>
                    The <strong>Both Teams to Score (BTTS)</strong> market is one of the most popular bet types in modern football analytics. Unlike 1X2 moneyline markets—where the absolute goal superiority of one side determines success—BTTS depends strictly on whether both teams score at least once, completely irrespective of the final margin.
                </p>
                <p>
                    A 1-1 draw, a 5-1 rout, and a 2-1 thriller all win the "BTTS: Yes" selection identically. Consequently, traditional metrics such as league table position, recent win streaks, and goal difference often provide misleading signals for BTTS forecasting.
                </p>

                <h2>2. Key Quantitative Signals for BTTS</h2>
                <p>
                    When modeling BTTS likelihood, our predictive pipeline evaluates four specific indicators:
                </p>

                <h3>A. Pace and Possession Transition Frequency</h3>
                <p>
                    Teams that employ high pressing and rapid counter-pressing styles create chaotic transitions. Fast-break teams allow higher expected goals against per possession even when they dominate play, dramatically increasing the odds of both teams converting.
                </p>

                <h3>B. Clean Sheet Degradation Away From Home</h3>
                <p>
                    Even elite defensive clubs suffer significant clean sheet degradation when traveling. Travel fatigue, unfamiliar pitch dimensions, and hostile crowd dynamics lead to momentary defensive lapses. In top 5 European leagues, home underdogs score in approximately 68% of fixtures against top-4 visitors.
                </p>

                <h3>C. Shot Conversion &amp; Expected Goals on Target (xGoT)</h3>
                <p>
                    Total shots taken is a noisy metric. A team taking 18 shots from outside the 18-yard box poses less scoring probability than a team taking 6 shots from within the 6-yard box. Expected Goals on Target (xGoT) isolates the quality of the finish and goalkeeper positioning.
                </p>

                <h3>D. Scoreline Correlation Matrix</h3>
                <p>
                    Recalling the Dixon-Coles model, the probability of BTTS Yes is:
                    <br><code>P(BTTS = Yes) = 1 - P(Home Goals = 0) - P(Away Goals = 0) + P(0 - 0)</code>
                </p>
                <div class="mp-article-callout">
                    <p>
                        Notice that adding <code>P(0-0)</code> prevents double-counting the scoreless state. When a match has low expected total goals, the probability of a 0-0 draw expands significantly, pulling the BTTS probability downward non-linearly.
                    </p>
                </div>

                <h2>3. Publication Thresholds at MatchPredictor</h2>
                <p>
                    We do not publish BTTS picks simply because both teams scored in their last match. Every candidate fixture must clear a rigorous calibrated confidence gate (typically &ge; 58% to 62% depending on the league's baseline scoring rate). When fixtures fall below this threshold, the card remains empty—honesty in forecasting means rejecting low-conviction matches.
                </p>
                """
        },
        new GuideArticle
        {
            Slug = "managing-variance-in-sports-analytics",
            Title = "Managing Variance in Sports Analytics: A Quantitative View on Value",
            Category = "Bankroll & Strategy",
            Summary = "Mathematical principles of variance, Expected Value (EV), drawdown probability, and the psychological discipline required for long-term forecasting.",
            ReadingTime = "7 min read",
            PublishedDate = "24 Sep 2026",
            ContentHtml = """
                <h2>1. The Reality of Probabilistic Variance</h2>
                <p>
                    One of the hardest psychological realities for newcomers to sports analytics is the inevitability of <strong>variance</strong>. In a coin toss with a known fair probability of 50%, flipping 5 heads in a row has a <code>(0.5)⁵ = 3.125%</code> probability—an event that will happen roughly once in every 32 trials of 5 flips.
                </p>
                <p>
                    In sports prediction, even when a model possesses a genuine, audited 5% edge over the market, long losing streaks and extended winning runs are mathematical certainties. A model that is 60% accurate over 1,000 trials will still encounter stretches of 4, 5, or even 7 consecutive losses over the course of a competitive season.
                </p>

                <h2>2. Calculating Expected Value (EV)</h2>
                <p>
                    Professional quantitative analysts do not measure quality by whether an individual pick wins or loses on Saturday afternoon. They measure quality by <strong>Expected Value (EV)</strong> at the moment of publication:
                </p>
                <div class="mp-article-callout">
                    <p><strong>Expected Value (EV) = (P &times; Decimal Odds) - 1</strong></p>
                </div>
                <p>
                    For example:
                </p>
                <ul>
                    <li>Your calibrated model estimates a true probability <code>P = 0.55</code> (55%).</li>
                    <li>The available bookmaker odds are <code>2.00</code>.</li>
                    <li><code>EV = (0.55 × 2.00) - 1 = +0.10 (+10% expected return)</code>.</li>
                </ul>
                <p>
                    If you take this position 100 times, you will lose 45 times. But over the law of large numbers, the positive expectation compounds into consistent capital growth. If you abandon a sound model because of a 3-match dip, you succumb to the gambler's fallacy.
                </p>

                <h2>3. Bankroll Staking: Kelly Criterion vs. Flat Staking</h2>
                <p>
                    Even an infallible model with positive expected value can bankrupt an analyst who stakes recklessly. In 1956, Bell Labs mathematician John Kelly Jr. formulated the <strong>Kelly Criterion</strong> for optimal capital allocation:
                </p>
                <p>
                    <code>f* = (b &times; p - q) / b</code>
                </p>
                <p>
                    Where <code>b</code> is the net decimal odds minus 1, <code>p</code> is the true probability, and <code>q = 1 - p</code>.
                </p>
                <p>
                    Because full Kelly staking exhibits aggressive bankroll drawdowns (upwards of 40% swings), professional quant syndicates universally employ <strong>Fractional Kelly</strong> (e.g. 0.25x Kelly) or <strong>Conservative Flat Staking</strong> (1% to 2% of total capital per selection).
                </p>

                <h2>4. Structural Maintenance &amp; Public Transparency</h2>
                <p>
                    MatchPredictor was built on the premise that honest track records beat marketing hype. We publish all settled results—including every loss—in our public <a href="/results">Results</a> portal.
                </p>
                <p>
                    True statistical maintenance requires examining failure modes: analyzing whether model misses stemmed from unpredictable events (e.g. 15th-minute red cards, weather disruptions) or systematic bias in parameter weighting. By continually recalibrating our models against real settled data, we maintain an authentic, high-value informational resource.
                </p>
                """
        }
    ];

    public IReadOnlyList<GuideArticle> GetAllGuides() => Guides;

    public GuideArticle? GetGuideBySlug(string slug)
    {
        return Guides.FirstOrDefault(g => string.Equals(g.Slug, slug, StringComparison.OrdinalIgnoreCase));
    }
}
