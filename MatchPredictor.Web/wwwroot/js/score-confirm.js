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
      '<p id="mpScoreConfirmHint" class="mp-score-confirm-hint">Choose the scraped row that matches this fixture.</p>' +
      '<div id="mpScoreConfirmCandidates" class="mp-score-confirm-candidates"></div>' +
      '<div class="mp-score-confirm-actions">' +
      '<button type="button" id="mpScoreConfirmCancel">Cancel</button>' +
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

  function parseCandidates(button) {
    var raw = button.getAttribute('data-candidates') || '[]';
    try {
      var parsed = JSON.parse(raw);
      return Array.isArray(parsed) ? parsed : [];
    } catch (error) {
      return [];
    }
  }

  function openDialog(button) {
    var dialog = ensureDialog();
    var candidates = parseCandidates(button);
    pendingPayload = {
      predictionId: Number(button.getAttribute('data-prediction-id')),
      home: button.getAttribute('data-home') || '',
      away: button.getAttribute('data-away') || '',
      button: button,
      candidates: candidates
    };

    dialog.querySelector('#mpScoreConfirmPrediction').textContent =
      'Prediction: ' + pendingPayload.home + ' vs ' + pendingPayload.away;

    var list = dialog.querySelector('#mpScoreConfirmCandidates');
    list.innerHTML = '';

    if (candidates.length === 0) {
      var empty = document.createElement('p');
      empty.textContent = 'No scraped candidates were attached to this card.';
      list.appendChild(empty);
    } else {
      candidates.forEach(function (candidate) {
        list.appendChild(createCandidateButton(candidate));
      });
    }

    dialog.classList.add('is-open');
  }

  function createCandidateButton(candidate) {
    var item = document.createElement('button');
    item.type = 'button';
    item.className = 'mp-score-confirm-candidate';

    var title = document.createElement('span');
    title.className = 'mp-score-confirm-candidate-teams';
    title.textContent =
      (candidate.scrapedHomeTeam || '') + ' vs ' + (candidate.scrapedAwayTeam || '') +
      ' — ' + (candidate.score || '');

    var meta = document.createElement('span');
    meta.className = 'mp-score-confirm-candidate-meta';
    var parts = [candidate.sourceName || 'Unknown'];
    if (candidate.scrapedLeague) {
      parts.push(candidate.scrapedLeague);
    }
    if (candidate.isLive) {
      parts.push('Live');
    }
    if (candidate.isFlipped) {
      parts.push('Teams swapped on source');
    }
    meta.textContent = parts.join(' · ');

    item.appendChild(title);
    item.appendChild(meta);
    item.addEventListener('click', function () {
      confirmMatch(candidate, item);
    });
    return item;
  }

  async function confirmMatch(candidate, clickedButton) {
    if (!pendingPayload || !candidate) {
      return;
    }

    var buttons = document.querySelectorAll('.mp-score-confirm-candidate');
    buttons.forEach(function (button) {
      button.disabled = true;
    });
    if (clickedButton) {
      clickedButton.textContent = 'Confirming…';
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
          sourceName: candidate.sourceName,
          sourceRowId: candidate.sourceRowId
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

      applyScoreToCards(payload);
      closeDialog();
    } catch (error) {
      window.alert('Could not confirm score link.');
    } finally {
      if (pendingPayload && pendingPayload.candidates) {
        var list = document.querySelector('#mpScoreConfirmCandidates');
        if (list) {
          list.innerHTML = '';
          pendingPayload.candidates.forEach(function (candidate) {
            list.appendChild(createCandidateButton(candidate));
          });
        }
      }
    }
  }

  function applyScoreToCards(payload) {
    var updatesById = {};
    (payload.updates || []).forEach(function (update) {
      updatesById[String(update.predictionId)] = update;
    });

    var fallbackIds = payload.updatedPredictionIds || [];
    fallbackIds.forEach(function (id) {
      if (!updatesById[String(id)]) {
        updatesById[String(id)] = {
          predictionId: id,
          scoreClass: 'mp-score-incorrect',
          isLive: !!payload.isLive
        };
      }
    });

    var actualScore = payload.actualScore || '';

    document.querySelectorAll('.mp-score-near-miss').forEach(function (button) {
      var id = button.getAttribute('data-prediction-id');
      var update = updatesById[id];
      if (!update) {
        return;
      }

      var scoreClass = update.scoreClass || 'mp-score-incorrect';
      var isLive = !!update.isLive;
      var score = document.createElement('span');
      score.className = 'mp-score ' + scoreClass + ' mp-score-value';
      score.setAttribute('data-prediction-id', id);
      if (isLive) {
        score.innerHTML = '<span class="mp-live-indicator"></span>' + actualScore;
      } else {
        score.textContent = actualScore;
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
