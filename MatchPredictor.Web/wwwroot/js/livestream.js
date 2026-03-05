/**
 * livestream.js
 * Handles fetching live stream embeds from AiScore and attaching them to matching live games.
 */

document.addEventListener("DOMContentLoaded", () => {
    // Buttons are initially unhidden by the backend if HasStream & MatchId are present.
    // We just need to attach the click listener to open the stream.
    const watchButtons = document.querySelectorAll(".btn-watch-stream");
    if (watchButtons.length === 0) return; // No live games on this page

    watchButtons.forEach(btn => {
        // Find the closest prediction card to grab the AiScore Match ID
        const card = btn.closest(".mp-prediction-card");
        if (!card) return;

        const aiscoreId = card.getAttribute("data-aiscore-id");
        if (!aiscoreId) return;

        const homeTeam = btn.getAttribute("data-home");
        const awayTeam = btn.getAttribute("data-away");

        btn.addEventListener("click", () => openStreamModal(aiscoreId, homeTeam, awayTeam));
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
 * Loads the AiScore iframe embed URL for the matched game and opens the Custom Modal
 */
function openStreamModal(streamId, homeTeam, awayTeam) {
    const modalTitle = document.getElementById("streamModalLabel");
    const iframeContainer = document.getElementById("streamIframeContainer");

    // Update UI
    modalTitle.innerHTML = `<i class="fas fa-satellite-dish" style="color: #ef4444; margin-right: 8px;"></i> ${homeTeam} vs ${awayTeam} - Live Stream`;
    iframeContainer.innerHTML = '<div class="mp-stream-loading" id="streamLoadingIndicator"><i class="fas fa-spinner fa-spin" style="font-size: 2rem; color: #3b82f6;"></i><p style="margin-top: 10px; color: #94a3b8; font-size: 0.9rem;">Loading Video...</p></div>';

    // Show modal instantly
    const modal = document.getElementById('streamModal');
    modal.style.display = 'flex';

    // AiScore Embed URL strategy:
    // https://m.aiscore.com/match-{matchId}/live
    // or standard iframe embed format if available
    const embedUrl = `https://m.aiscore.com/match-${streamId}/live`;

    try {
        iframeContainer.innerHTML = `<iframe id="streamFrame" src="${embedUrl}" allowfullscreen scrolling="yes" allow="encrypted-media" style="position: absolute; top: 0; left: 0; width: 100%; height: 100%; border: none;"></iframe>`;
    } catch (e) {
        console.error("[LiveStream] Failed to load stream detail:", e);
        iframeContainer.innerHTML = '<div style="position: absolute; top: 50%; left: 50%; transform: translate(-50%, -50%); color: #ef4444;">Error loading stream. Please try again later.</div>';
    }
}
