/**
 * livestream.js
 * Handles fetching live stream embeds from AiScore using pre-loaded data attributes.
 */

document.addEventListener("DOMContentLoaded", () => {
    // 1. Find all watch buttons
    const watchButtons = document.querySelectorAll(".btn-watch-stream");
    if (watchButtons.length === 0) return; // No live games on this page

    // 2. Iterate through prediction cards to check for stream availability
    document.querySelectorAll('.mp-prediction-card').forEach(card => {
        const hasStream = card.getAttribute('data-has-stream') === 'true';
        const aiScoreId = card.getAttribute('data-aiscore-id');
        
        // Find the button within this specific card
        const btn = card.querySelector('.btn-watch-stream');
        
        if (btn && hasStream && aiScoreId) {
            // Stream is available, configure the button
            const homeTeam = btn.getAttribute("data-home");
            const awayTeam = btn.getAttribute("data-away");
            
            btn.style.display = "inline-flex";
            btn.classList.remove("d-none");
            
            // Attach click handler to open the AiScore iframe modal
            btn.addEventListener("click", () => openStreamModal(aiScoreId, homeTeam, awayTeam));
        }
    });

    // Setup the button initial display state for everything safely just in case
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
    // AiScore provides a public mobile webview URL that can be framed to show live animations/video
    const embedUrl = `https://m.aiscore.com/match-${streamId}/live`;
    
    iframeContainer.innerHTML = `<iframe id="streamFrame" src="${embedUrl}" allowfullscreen scrolling="yes" allow="encrypted-media" style="position: absolute; top: 0; left: 0; width: 100%; height: 100%; border: none;"></iframe>`;
}
