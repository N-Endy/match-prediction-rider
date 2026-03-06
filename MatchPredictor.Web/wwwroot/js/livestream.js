/**
 * livestream.js
 * Handles fetching live stream embeds from our internal LiveStreams API
 * and attaching them to matching live games without blocking other processes.
 */

let availableStreams = [];

document.addEventListener("DOMContentLoaded", async () => {
    // 1. Find all watch buttons
    const watchButtons = document.querySelectorAll(".btn-watch-stream");
    if (watchButtons.length === 0) return; // No live games on this page

    // 2. Fetch the cached live streams from our backend silently
    await fetchLiveStreams();
    if (!availableStreams || availableStreams.length === 0) {
        // Hide all buttons if no streams exist
        watchButtons.forEach(btn => btn.style.display = "none");
        return;
    }

    // 3. Iterate through watch buttons and see if their teams exist in the live streams array
    watchButtons.forEach(btn => {
        const homeTeam = btn.getAttribute("data-home");
        const awayTeam = btn.getAttribute("data-away");
        
        const matchedStream = findStreamForMatch(homeTeam, awayTeam);
        
        if (matchedStream) {
            // Stream is available, configure the button
            btn.style.display = "inline-flex";
            btn.classList.remove("d-none");
            
            // Attach click handler to open the AiScore iframe modal
            btn.addEventListener("click", () => openStreamModal(matchedStream.aiScoreMatchId, homeTeam, awayTeam));
        } else {
            btn.style.display = "none";
            btn.classList.add("d-none");
        }
    });
});

async function fetchLiveStreams() {
    try {
        const response = await fetch("/api/livestreams");
        if (response.ok) {
            availableStreams = await response.json();
            console.log(`[LiveStream] Fetched ${availableStreams.length} available streams from internal cache.`);
        }
    } catch (e) {
        console.warn("[LiveStream] Failed to fetch live streams:", e);
        availableStreams = [];
    }
}

function findStreamForMatch(homeTeam, awayTeam) {
    if (!homeTeam || !awayTeam || !availableStreams.length) return null;

    const normalize = str => str.toLowerCase()
        .replace(/ fc| afc| united| city| real| atletico| sporting| club| deportivo/g, "")
        .replace(/[^a-z0-9]/g, "");

    const normTargetHome = normalize(homeTeam);
    const normTargetAway = normalize(awayTeam);

    for (const stream of availableStreams) {
        if (!stream.homeTeam || !stream.awayTeam) continue;

        const normStreamHome = normalize(stream.homeTeam);
        const normStreamAway = normalize(stream.awayTeam);

        // Fuzzy match logic
        const homeMatch = normTargetHome.includes(normStreamHome) || normStreamHome.includes(normTargetHome);
        const awayMatch = normTargetAway.includes(normStreamAway) || normStreamAway.includes(normTargetAway);

        if (homeMatch && awayMatch) {
            return stream;
        }
    }

    return null;
}

function closeStreamModal() {
    const modal = document.getElementById("streamModal");
    if (modal) {
        modal.style.display = "none";
        const container = document.getElementById("streamIframeContainer");
        if (container) {
            // Reset iframe container to prevent video playing in background
            container.innerHTML = '<div class="mp-stream-loading" id="streamLoadingIndicator"><i class="fas fa-spinner fa-spin" style="font-size: 2rem; color: #3b82f6;"></i><p style="margin-top: 10px; color: #94a3b8; font-size: 0.9rem;">Loading Video...</p></div>';
        }
    }
}

/**
 * Constructs the specific iframe embed URL from AiScore and opens the Modal
 */
function openStreamModal(streamId, homeTeam, awayTeam) {
    const modalTitle = document.getElementById("streamModalLabel");
    const iframeContainer = document.getElementById("streamIframeContainer");

    // Update UI
    modalTitle.innerHTML = `<i class="fas fa-satellite-dish" style="color: #ef4444; margin-right: 8px;"></i> ${homeTeam} vs ${awayTeam} - Live Stream`;
    
    // Show modal instantly
    const modal = document.getElementById('streamModal');
    modal.style.display = 'flex';

    // Set AiScore direct embed iframe
    const embedUrl = `https://m.aiscore.com/match-${streamId}/live`;
    
    iframeContainer.innerHTML = `<iframe id="streamFrame" src="${embedUrl}" allowfullscreen scrolling="yes" allow="encrypted-media" style="position: absolute; top: 0; left: 0; width: 100%; height: 100%; border: none;"></iframe>`;
}
