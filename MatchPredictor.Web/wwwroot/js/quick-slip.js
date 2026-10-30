// ─── Quick Slip Natural Language Co-Pilot ───
(function () {
    'use strict';

    let currentStagedPicks = [];

    window.setQuickSlipPrompt = function (text) {
        const input = document.getElementById('quickSlipInput');
        if (input) {
            input.value = text;
            input.focus();
            const form = document.getElementById('quickSlipForm');
            if (form) {
                form.dispatchEvent(new Event('submit', { cancelable: true, bubbles: true }));
            }
        }
    };

    window.dismissQuickSlipStaged = function () {
        const wrap = document.getElementById('quickSlipStagedContainer');
        if (wrap) wrap.classList.add('d-none');
        currentStagedPicks = [];
    };

    window.handleQuickSlipSubmit = async function (event) {
        if (event) event.preventDefault();

        const input = document.getElementById('quickSlipInput');
        const submitBtn = document.getElementById('quickSlipSubmitBtn');
        const errorBanner = document.getElementById('quickSlipError');
        const stagedWrap = document.getElementById('quickSlipStagedContainer');
        const questionsBar = document.getElementById('quickSlipQuestions');

        if (!input) return;
        const text = input.value.trim();
        if (!text) {
            input.focus();
            return;
        }

        // Reset state
        if (errorBanner) {
            errorBanner.classList.add('d-none');
            errorBanner.textContent = '';
        }
        if (questionsBar) {
            questionsBar.classList.add('d-none');
        }

        // Set Loading State
        if (submitBtn) {
            submitBtn.disabled = true;
            const textSpan = submitBtn.querySelector('.btn-text');
            const spinner = submitBtn.querySelector('.btn-spinner');
            if (textSpan) textSpan.textContent = 'Analyzing...';
            if (spinner) spinner.classList.remove('d-none');
        }

        try {
            // Get current slip IDs from cart.js if available
            let currentIds = [];
            if (typeof window.getCart === 'function') {
                currentIds = window.getCart()
                    .map(item => Number(item.predictionId))
                    .filter(id => Number.isFinite(id) && id > 0);
            }

            const payload = {
                input: text,
                currentSlipPredictionIds: currentIds,
                maxLegs: 10
            };

            const response = await fetch('/api/slip/ai-parse', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    'Accept': 'application/json'
                },
                body: JSON.stringify(payload)
            });

            if (!response.ok) {
                throw new Error(`Server returned error ${response.status}`);
            }

            const data = await response.json();

            if (data.success && Array.isArray(data.stagedPicks) && data.stagedPicks.length > 0) {
                currentStagedPicks = data.stagedPicks;
                renderStagedResults(data);
                if (stagedWrap) stagedWrap.classList.remove('d-none');
            } else {
                window.dismissQuickSlipStaged();
                if (errorBanner) {
                    errorBanner.textContent = data.summary || 'Could not find matches for your criteria. Try adjusting leagues or odds.';
                    errorBanner.classList.remove('d-none');
                }

                // Render clarification questions if provided
                if (Array.isArray(data.clarificationQuestions) && data.clarificationQuestions.length > 0 && questionsBar) {
                    renderClarificationQuestions(data.clarificationQuestions);
                    questionsBar.classList.remove('d-none');
                }
            }
        } catch (err) {
            console.error('Quick slip parse failed:', err);
            if (errorBanner) {
                errorBanner.textContent = 'Connection failed. Please check your network and try again.';
                errorBanner.classList.remove('d-none');
            }
        } finally {
            if (submitBtn) {
                submitBtn.disabled = false;
                const textSpan = submitBtn.querySelector('.btn-text');
                const spinner = submitBtn.querySelector('.btn-spinner');
                if (textSpan) textSpan.textContent = 'Build Slip';
                if (spinner) spinner.classList.add('d-none');
            }
        }
    };

    function renderStagedResults(data) {
        const summaryText = document.getElementById('quickSlipSummaryText');
        const oddsBadge = document.getElementById('quickSlipTotalOddsBadge');
        const probBadge = document.getElementById('quickSlipAvgProbBadge');
        const edgeBadge = document.getElementById('quickSlipEdgeBadge');
        const legsList = document.getElementById('quickSlipLegsList');

        if (summaryText) summaryText.textContent = data.summary;
        if (oddsBadge) oddsBadge.innerHTML = `Combined: <strong>${data.totalCombinedOdds.toFixed(2)}x</strong>`;
        if (probBadge) probBadge.innerHTML = `Avg Prob: <strong>${Math.round(data.averageModelProbability * 100)}%</strong>`;

        if (edgeBadge) {
            if (data.overallExpectedEdge !== null && data.overallExpectedEdge !== undefined && data.overallExpectedEdge > 0) {
                edgeBadge.innerHTML = `Edge: <strong>+${(data.overallExpectedEdge * 100).toFixed(1)}%</strong>`;
                edgeBadge.classList.remove('d-none');
            } else {
                edgeBadge.classList.add('d-none');
            }
        }

        if (legsList) {
            legsList.innerHTML = '';
            data.stagedPicks.forEach((pick, idx) => {
                const legEl = document.createElement('div');
                legEl.className = 'mp-qs-leg-card';

                const probPct = pick.modelProbability ? `${Math.round(pick.modelProbability * 100)}%` : '--';
                const oddsText = pick.odds ? pick.odds.toFixed(2) : '--';
                const marketName = formatMarketName(pick.market);

                legEl.innerHTML = `
                    <div class="mp-qs-leg-main">
                        <span class="mp-qs-leg-num">#${idx + 1}</span>
                        <div class="mp-qs-leg-details">
                            <div class="mp-qs-leg-teams">
                                <strong>${escapeHtml(pick.homeTeam)}</strong> vs <strong>${escapeHtml(pick.awayTeam)}</strong>
                            </div>
                            <div class="mp-qs-leg-meta">
                                <span>${escapeHtml(pick.league)}</span> · <span>${escapeHtml(pick.formattedTime || '')}</span>
                            </div>
                        </div>
                    </div>
                    <div class="mp-qs-leg-prediction">
                        <span class="mp-qs-market-tag">${escapeHtml(marketName)}</span>
                        <span class="mp-qs-pick-outcome">${escapeHtml(pick.prediction)}</span>
                        <span class="mp-qs-pick-odds">@${oddsText}</span>
                    </div>
                `;
                legsList.appendChild(legEl);
            });
        }
    }

    function renderClarificationQuestions(questions) {
        const chipsContainer = document.getElementById('quickSlipQuestionChips');
        if (!chipsContainer) return;
        chipsContainer.innerHTML = '';

        const sampleSuggestions = ['BTTS Yes', 'Over 2.5 Goals', 'Straight Win', 'Under 2.5', 'EPL Matches'];
        sampleSuggestions.forEach(opt => {
            const btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'mp-qs-q-chip';
            btn.textContent = opt;
            btn.onclick = () => {
                const input = document.getElementById('quickSlipInput');
                if (input) {
                    input.value = `${input.value} ${opt}`.trim();
                    window.handleQuickSlipSubmit();
                }
            };
            chipsContainer.appendChild(btn);
        });
    }

    window.applyQuickSlip = function (mode) {
        if (!Array.isArray(currentStagedPicks) || currentStagedPicks.length === 0) return;

        if (typeof window.addToCart !== 'function') {
            console.warn('cart.js addToCart is not defined.');
            return;
        }

        if (mode === 'replace' && typeof window.clearCart === 'function') {
            window.clearCart();
        }

        let addedCount = 0;
        currentStagedPicks.forEach(pick => {
            const cartMarket = pick.market || 'Unknown';
            window.addToCart({
                homeTeam: pick.homeTeam,
                awayTeam: pick.awayTeam,
                league: pick.league,
                market: cartMarket,
                prediction: pick.prediction,
                predictionId: pick.predictionId,
                odds: pick.odds ? pick.odds.toString() : '',
                time: pick.formattedTime,
                matchDateTimeUtc: pick.matchDateTimeUtc
            });
            addedCount++;
        });

        window.dismissQuickSlipStaged();

        if (typeof window.showToast === 'function') {
            window.showToast(`${mode === 'replace' ? 'Replaced slip with' : 'Added'} ${addedCount} games`);
        }

        // Pulse cart badge if available
        const badge = document.getElementById('cartBadge');
        if (badge) {
            badge.classList.add('pulse');
            setTimeout(() => badge.classList.remove('pulse'), 800);
        }
    };

    function formatMarketName(market) {
        switch (market) {
            case 'BothTeamsScore': return 'BTTS';
            case 'Over2.5Goals': return 'Over 2.5';
            case 'Under2.5Goals': return 'Under 2.5';
            case 'StraightWin': return '1X2';
            case 'Draw': return 'Draw';
            default: return market || 'Prediction';
        }
    }

    function escapeHtml(str) {
        if (!str) return '';
        return str
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#039;');
    }
})();
