(function () {
  function ensureDialog() {
    var existing = document.getElementById('mpScoreConfirmDialog');
    if (existing) {
      return existing;
    }

    var dialog = document.createElement('div');
    dialog.id = 'mpScoreConfirmDialog';
    dialog.className = 'mp-score-confirm-dialog';
    dialog.setAttribute('role', 'dialog');
    dialog.setAttribute('aria-modal', 'true');
    dialog.innerHTML =
      '<div class="mp-score-confirm-panel">' +
      '<h3>Confirm score match</h3>' +
      '<p id="mpScoreConfirmPrediction"></p>' +
      '<p id="mpScoreConfirmScraped"></p>' +
      '<p id="mpScoreConfirmMeta"></p>' +
      '<div class="mp-score-confirm-actions">' +
      '<button type="button" id="mpScoreConfirmCancel">Cancel</button>' +
      '<button type="button" class="mp-score-confirm-yes" id="mpScoreConfirmYes">Confirm match</button>' +
      '</div>' +
      '</div>';
    document.body.appendChild(dialog);

    dialog.querySelector('#mpScoreConfirmCancel').addEventListener('click', closeDialog);
    dialog.addEventListener('click', function (event) {
      if (event.target === dialog) {
        closeDialog();
      }
    });

    return dialog;
  }

  var pendingPayload = null;

  function closeDialog() {
    var dialog = document.getElementById('mpScoreConfirmDialog');
    if (dialog) {
      dialog.classList.remove('is-open');
    }
    pendingPayload = null;
  }

  function openDialog(button) {
    var dialog = ensureDialog();
    pendingPayload = {
      predictionId: Number(button.getAttribute('data-prediction-id')),
      sourceName: button.getAttribute('data-source-name'),
      sourceRowId: Number(button.getAttribute('data-source-row-id')),
      home: button.getAttribute('data-home') || '',
      away: button.getAttribute('data-away') || '',
      scrapedHome: button.getAttribute('data-scraped-home') || '',
      scrapedAway: button.getAttribute('data-scraped-away') || '',
      scrapedScore: button.getAttribute('data-scraped-score') || '',
      scrapedLeague: button.getAttribute('data-scraped-league') || '',
      isLive: button.getAttribute('data-is-live') === 'true',
      button: button
    };

    dialog.querySelector('#mpScoreConfirmPrediction').textContent =
      'Prediction: ' + pendingPayload.home + ' vs ' + pendingPayload.away;
    dialog.querySelector('#mpScoreConfirmScraped').textContent =
      'Scraped: ' + pendingPayload.scrapedHome + ' vs ' + pendingPayload.scrapedAway +
      ' — ' + pendingPayload.scrapedScore;
    dialog.querySelector('#mpScoreConfirmMeta').textContent =
      'Source: ' + pendingPayload.sourceName +
      (pendingPayload.scrapedLeague ? ' · ' + pendingPayload.scrapedLeague : '');

    var yesBtn = dialog.querySelector('#mpScoreConfirmYes');
    yesBtn.onclick = confirmMatch;
    dialog.classList.add('is-open');
  }

  async function confirmMatch() {
    if (!pendingPayload) {
      return;
    }

    var yesBtn = document.getElementById('mpScoreConfirmYes');
    if (yesBtn) {
      yesBtn.disabled = true;
      yesBtn.textContent = 'Confirming…';
    }

    try {
      var response = await fetch('/admin/api/score-link/confirm', {
        method: 'POST',
        credentials: 'same-origin',
        headers: {
          'Content-Type': 'application/json',
          'Accept': 'application/json'
        },
        body: JSON.stringify({
          predictionId: pendingPayload.predictionId,
          sourceName: pendingPayload.sourceName,
          sourceRowId: pendingPayload.sourceRowId
        })
      });

      if (response.status === 401) {
        window.alert('Admin login required. Open /analytics (or another admin page), sign in, then try again.');
        closeDialog();
        return;
      }

      var payload = await response.json().catch(function () { return null; });
      if (!response.ok || !payload || !payload.success) {
        var message = (payload && payload.error) || 'Could not confirm score link.';
        window.alert(message);
        return;
      }

      applyScoreToCards(payload.updatedPredictionIds || [pendingPayload.predictionId], payload.actualScore, payload.isLive);
      closeDialog();
    } catch (error) {
      window.alert('Could not confirm score link.');
    } finally {
      if (yesBtn) {
        yesBtn.disabled = false;
        yesBtn.textContent = 'Confirm match';
      }
    }
  }

  function applyScoreToCards(predictionIds, actualScore, isLive) {
    var idSet = {};
    (predictionIds || []).forEach(function (id) {
      idSet[String(id)] = true;
    });

    document.querySelectorAll('.mp-score-near-miss').forEach(function (button) {
      var id = button.getAttribute('data-prediction-id');
      if (!idSet[id]) {
        return;
      }

      var score = document.createElement('span');
      score.className = 'mp-score ' + (isLive ? 'mp-score-live' : 'mp-score-correct') + ' mp-score-value';
      score.setAttribute('data-prediction-id', id);
      if (isLive) {
        score.innerHTML = '<span class="mp-live-indicator"></span>' + (actualScore || '');
      } else {
        score.textContent = actualScore || '';
      }
      button.replaceWith(score);
    });
  }

  document.addEventListener('click', function (event) {
    var button = event.target.closest('.mp-score-near-miss');
    if (!button) {
      return;
    }
    event.preventDefault();
    openDialog(button);
  });
})();
