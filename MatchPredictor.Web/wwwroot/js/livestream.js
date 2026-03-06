/**
 * livestream.js
 * Handles fetching live stream embeds from api.sportsrc.org and attaching them to matching live games.
 */

// Cache the currently available streams so we only fetch them once per page load
let availableStreams = null;

document.addEventListener("DOMContentLoaded", async () => {
    // Note: We changed the default class from "d-none" to inline style "display: none" 
    // because Bootstrap's d-none was unhidding it, but since we removed bootstrap, we need inline logic.
    const watchButtons = document.querySelectorAll(".btn-watch-stream");
    if (watchButtons.length === 0) return; // No live games on this page

    // Fetch the live schedule from SportSRC
    await fetchLiveStreams();

    if (!availableStreams || availableStreams.length === 0) return;

    // Cross-reference UI buttons with the API data
    watchButtons.forEach(btn => {
        const homeTeam = btn.getAttribute("data-home");
        const awayTeam = btn.getAttribute("data-away");

        const matchedStream = findStreamForMatch(homeTeam, awayTeam);

        if (matchedStream) {
            // Unhide the button and attach the click event to open the modal
            btn.style.display = "inline-flex";
            btn.classList.remove("d-none"); // in case any old classes stick around
            btn.addEventListener("click", () => openStreamModal(matchedStream.id, homeTeam, awayTeam));
        } else {
            btn.style.display = "none";
        }
    });

    // Setup the button initial display state for everything safely
    document.querySelectorAll(".btn-watch-stream.d-none").forEach(el => {
        el.style.display = "none";
    });
});

function closeStreamModal() {
    const modal = document.getElementById("streamModal");
    if (modal) {
        modal.style.display = "none";
        const container = document.getElementById("streamIframeContainer");
        if (container) {
            container.innerHTML = '<div class="mp-stream-loading" id="streamLoadingIndicator"><i class="fas fa-spinner fa-spin" style="font-size: 2rem; color: #3b82f6;"></i><p style="margin-top: 10px; color: #94a3b8; font-size: 0.9rem;">Loading Video...</p></div>';
        }
    }
}

/**
 * Fetches the master list of all football matches currently available on SportSRC
 */
async function fetchLiveStreams() {
    try {
        const response = await fetch("https://api.sportsrc.org/?data=matches&category=football");
        if (response.ok) {
            availableStreams = await response.json();
            console.log(`[LiveStream] Fetched ${availableStreams.length} available streams from SportSRC.`);
        }
    } catch (e) {
        console.warn("[LiveStream] Failed to fetch live streams:", e);
        availableStreams = [];
    }
}

/**
 * Uses a basic fuzzy matching strategy to find a match.
 * AiScore and SportSRC team names often differ slightly (e.g. "Man United" vs "Manchester United FC").
 */
function findStreamForMatch(homeTeam, awayTeam) {
    if (!homeTeam || !awayTeam || !availableStreams) return null;

    const normalize = str => str.toLowerCase()
        .replace(/ fc| afc| united| city| real| atletico| sporting| club| deportivo/g, "")
        .replace(/[^a-z0-9]/g, "");

    const normTargetHome = normalize(homeTeam);
    const normTargetAway = normalize(awayTeam);

    for (const stream of availableStreams) {
        if (!stream.teams || !stream.teams.home || !stream.teams.away) continue;

        const normStreamHome = normalize(stream.teams.home.name);
        const normStreamAway = normalize(stream.teams.away.name);

        // Check if normalized strings are substantially similar (one contains the other)
        const homeMatch = normTargetHome.includes(normStreamHome) || normStreamHome.includes(normTargetHome);
        const awayMatch = normTargetAway.includes(normStreamAway) || normStreamAway.includes(normTargetAway);

        if (homeMatch && awayMatch) {
            return stream;
        }
    }

    return null;
}

/**
 * Fetches the specific iframe embed URL for the matched game and opens the Custom Modal
 */
async function openStreamModal(streamId, homeTeam, awayTeam) {
    const modalTitle = document.getElementById("streamModalLabel");
    const iframeContainer = document.getElementById("streamIframeContainer");

    // Update UI
    modalTitle.innerHTML = `<i class="fas fa-satellite-dish" style="color: #ef4444; margin-right: 8px;"></i> ${homeTeam} vs ${awayTeam} - Live Stream`;
    iframeContainer.innerHTML = '<div class="mp-stream-loading" id="streamLoadingIndicator"><i class="fas fa-spinner fa-spin" style="font-size: 2rem; color: #3b82f6;"></i><p style="margin-top: 10px; color: #94a3b8; font-size: 0.9rem;">Loading Video...</p></div>';

    // Show modal instantly
    const modal = document.getElementById('streamModal');
    modal.style.display = 'flex';

    // Fetch embed URL
    try {
        const response = await fetch(`https://api.sportsrc.org/?data=detail&category=football&id=${streamId}`);
        const result = await response.json();

        if (result.success && result.data && result.data.sources && result.data.sources.length > 0) {
            // Pick the first English or HD source if available, otherwise just grab the first one
            let bestSource = result.data.sources.find(s => s.language === "English" && s.hd)
                || result.data.sources.find(s => s.language === "English")
                || result.data.sources[0];

            iframeContainer.innerHTML = `<iframe id="streamFrame" src="${bestSource.embedUrl}" allowfullscreen scrolling="no" allow="encrypted-media" style="position: absolute; top: 0; left: 0; width: 100%; height: 100%; border: none;"></iframe>`;
        } else {
            iframeContainer.innerHTML = '<div style="position: absolute; top: 50%; left: 50%; transform: translate(-50%, -50%); color: #f59e0b;">Stream is currently offline or unavailable.</div>';
        }
    } catch (e) {
        console.error("[LiveStream] Failed to load stream detail:", e);
        iframeContainer.innerHTML = '<div style="position: absolute; top: 50%; left: 50%; transform: translate(-50%, -50%); color: #ef4444;">Error loading stream. Please try again later.</div>';
    }
}
