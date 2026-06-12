// ── Cart State Management ──
const CART_KEY = 'mp_cart';

function getMaxBookingSelections() {
    const raw = document.body?.dataset?.maxBookingSelections;
    const parsed = Number.parseInt(raw ?? '', 10);
    return Number.isFinite(parsed) && parsed > 0 ? parsed : 50;
}

function getCart() {
    try {
        return JSON.parse(localStorage.getItem(CART_KEY) || '[]');
    } catch {
        return [];
    }
}

function saveCart(cart) {
    localStorage.setItem(CART_KEY, JSON.stringify(cart));
    updateCartBadge();
}

function addToCart(match) {
    const cart = getCart();
    const maxSelections = getMaxBookingSelections();
    if (cart.length >= maxSelections) {
        showToast(`Betslip full (${maxSelections} max)`);
        return;
    }
    const exists = cart.some(m => getCartIdentity(m) === getCartIdentity(match));
    if (exists) {
        showToast('Already in betslip');
        return;
    }
    cart.push(match);
    saveCart(cart);
    window.matchPredictorTracking?.track('add_to_cart', {
        market: match.market || '',
        prediction: match.prediction || '',
        league: match.league || ''
    });
    showToast('Added to betslip');
}

function removeFromCart(index) {
    const cart = getCart();
    cart.splice(index, 1);
    saveCart(cart);
    renderCartItems();
}

function clearCart() {
    const count = getCart().length;
    localStorage.removeItem(CART_KEY);
    updateCartBadge();
    renderCartItems();
    if (count > 0) {
        window.matchPredictorTracking?.track('clear_cart', { count: String(count) });
    }
}

// ── Badge ──
function updateCartBadge() {
    const badge = document.getElementById('cartBadge');
    const count = getCart().length;
    if (badge) {
        badge.textContent = count;
        badge.style.display = count > 0 ? 'flex' : 'none';
    }
}

// ── Toast ──
function showToast(msg) {
    let toast = document.getElementById('cartToast');
    if (!toast) {
        toast = document.createElement('div');
        toast.id = 'cartToast';
        toast.className = 'mp-cart-toast';
        document.body.appendChild(toast);
    }
    toast.textContent = msg;
    toast.classList.add('show');
    setTimeout(() => toast.classList.remove('show'), 2000);
}

// ── Modal ──
function openCartModal() {
    const modal = document.getElementById('cartModal');
    if (modal) {
        modal.classList.add('open');
        renderCartItems();
        window.matchPredictorTracking?.track('open_betslip', {
            count: String(getCart().length)
        });
    }
}

function closeCartModal() {
    const modal = document.getElementById('cartModal');
    if (modal) modal.classList.remove('open');
}

function renderCartItems() {
    const container = document.getElementById('cartItemsList');
    const emptyState = document.getElementById('cartEmpty');
    const footer = document.getElementById('cartFooter');
    if (!container) return;

    const cart = getCart();
    container.innerHTML = '';

    if (cart.length === 0) {
        if (emptyState) emptyState.style.display = 'block';
        if (footer) footer.style.display = 'none';
        return;
    }

    if (emptyState) emptyState.style.display = 'none';
    if (footer) footer.style.display = 'flex';

    cart.forEach((item, index) => {
        const div = document.createElement('div');
        div.className = 'mp-cart-item';
        div.innerHTML = `
            <div class="mp-cart-item-info">
                <div class="mp-cart-item-teams">${item.homeTeam} vs ${item.awayTeam}</div>
                <div class="mp-cart-item-meta">
                    <span class="mp-cart-item-league">${item.league}</span>
                    <span class="mp-cart-item-prediction">${item.prediction}</span>
                </div>
            </div>
            <button class="mp-cart-item-remove" onclick="removeFromCart(${index})" title="Remove">✕</button>
        `;
        container.appendChild(div);
    });
}

// ── Booking ──
async function bookGames() {
    const cart = getCart();
    const maxSelections = getMaxBookingSelections();
    if (cart.length === 0) {
        showToast('Cart is empty');
        return;
    }
    if (cart.length > maxSelections) {
        showToast(`A maximum of ${maxSelections} selections is allowed.`);
        return;
    }

    const bookBtn = document.getElementById('bookGamesBtn');
    const resultDiv = document.getElementById('bookingResult');
    if (bookBtn) {
        bookBtn.disabled = true;
        bookBtn.textContent = 'Booking...';
    }

    try {
        const selections = cart.map(item => ({
            homeTeam: item.homeTeam,
            awayTeam: item.awayTeam,
            league: item.league,
            market: item.market || 'Unknown',
            prediction: item.prediction,
            predictionId: Number.isFinite(Number(item.predictionId)) ? Number(item.predictionId) : null,
            matchDateTimeUtc: item.matchDateTimeUtc || null
        }));

        const response = await fetch('/api/booking/book', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ selections })
        });

        const result = await response.json();

        if (resultDiv) {
            if (result.success) {
                const urlHtml = result.bookingUrl
                    ? `<button type="button" class="mp-booking-url-btn" onclick='openSportyBetBooking(${JSON.stringify(result.bookingUrl)})'>🔗 Open in SportyBet</button>`
                    : '';
                const warningHtml = renderBookingWarnings(result.warnings);
                const summaryHtml = renderBookingSummary(result);

                resultDiv.innerHTML = `
                    <div class="mp-booking-success">
                        <button class="mp-booking-close" onclick="this.closest('.mp-booking-success').parentElement.style.display='none'">&times;</button>
                        <div class="mp-booking-code-label">Booking Code</div>
                        <div class="mp-booking-code">${result.bookingCode}</div>
                        <div class="mp-booking-actions">
                            <button class="mp-copy-code-btn" onclick="copyBookingCode('${result.bookingCode}')">📋 Copy</button>
                            ${urlHtml}
                        </div>
                        ${summaryHtml}
                        <p class="mp-booking-msg">${escapeHtml(result.message || '')}</p>
                        ${warningHtml}
                    </div>
                `;
            } else {
                const warningHtml = renderBookingWarnings(result.warnings);
                const summaryHtml = renderBookingSummary(result);
                resultDiv.innerHTML = `
                    <div class="mp-booking-error">
                        <button class="mp-booking-close" onclick="this.closest('.mp-booking-error').parentElement.style.display='none'">&times;</button>
                        ${summaryHtml}
                        <p>❌ ${escapeHtml(result.message || 'Booking failed.')}</p>
                        ${warningHtml}
                    </div>
                `;
            }
            resultDiv.style.display = 'block';
        }
    } catch (err) {
        if (resultDiv) {
            resultDiv.innerHTML = `<div class="mp-booking-error"><p>❌ Network error. Please try again.</p></div>`;
            resultDiv.style.display = 'block';
        }
    } finally {
        if (bookBtn) {
            bookBtn.disabled = false;
            bookBtn.textContent = '🎫 Book Games';
        }
    }
}

function copyBookingCode(code) {
    navigator.clipboard.writeText(code)
        .then(() => {
            window.matchPredictorTracking?.track('copy_booking_code', {
                hasCode: String(Boolean(code))
            });
            showToast('Copied!');
        })
        .catch(() => showToast('Copy failed'));
}

function openSportyBetBooking(url) {
    if (!url) {
        return;
    }

    clearCart();
    closeCartModal();
    showToast('Betslip cleared');

    try {
        const popup = window.open(url, '_blank', 'noopener,noreferrer');
        if (popup) {
            popup.opener = null;
            return;
        }
    } catch {
        // Fall back to same-tab navigation below.
    }

    window.location.href = url;
}

// ── Init ──
document.addEventListener('DOMContentLoaded', () => {
    updateCartBadge();

    // Event delegation for "Add Bet" buttons using data-attributes
    document.addEventListener('click', (e) => {
        const btn = e.target.closest('.mp-add-cart-btn');
        if (!btn) return;

        e.preventDefault();
        addToCart({
            homeTeam: btn.dataset.home || '',
            awayTeam: btn.dataset.away || '',
            league: btn.dataset.league || '',
            market: btn.dataset.market || '',
            prediction: btn.dataset.prediction || '',
            predictionId: btn.dataset.predictionId ? Number(btn.dataset.predictionId) : null,
            matchDateTimeUtc: btn.dataset.matchDatetimeUtc || null
        });
    });
});

function getCartIdentity(match) {
    const predictionId = Number(match?.predictionId);
    if (Number.isFinite(predictionId) && predictionId > 0) {
        return `prediction:${predictionId}`;
    }

    return [
        match?.homeTeam || '',
        match?.awayTeam || '',
        match?.league || '',
        match?.market || '',
        match?.prediction || '',
        match?.matchDateTimeUtc || ''
    ].join('|');
}

function escapeHtml(value) {
    return String(value ?? '')
        .replaceAll('&', '&amp;')
        .replaceAll('<', '&lt;')
        .replaceAll('>', '&gt;')
        .replaceAll('"', '&quot;')
        .replaceAll("'", '&#39;');
}

function renderBookingSummary(result) {
    const bookedCount = Number(result?.bookedCount || 0);
    const skippedCount = Number(result?.skippedCount || 0);
    if (bookedCount === 0 && skippedCount === 0) {
        return '';
    }

    return `
        <div style="display:flex; justify-content:center; gap:12px; flex-wrap:wrap; margin-bottom:12px; color:var(--text-secondary); font-size:0.9rem;">
            <span><strong>${bookedCount}</strong> booked</span>
            <span><strong>${skippedCount}</strong> skipped</span>
        </div>
    `;
}

function renderBookingWarnings(warnings) {
    const items = Array.isArray(warnings) ? warnings.filter(Boolean) : [];
    if (items.length === 0) {
        return '';
    }

    return `
        <div class="mp-booking-warnings">
            <div class="mp-booking-warnings-title">Skipped selections</div>
            <ul class="mp-booking-warnings-list">
                ${items.map(item => `<li class="mp-booking-warnings-item">${escapeHtml(item)}</li>`).join('')}
            </ul>
        </div>
    `;
}
