const state = {
  status: null,
  selectedSession: null,
  blocksBySession: new Map(),
};

async function fetchJson(url, options) {
  const response = await fetch(url, options);
  if (!response.ok) {
    const body = await response.text();
    throw new Error(body || `Request failed: ${response.status}`);
  }

  return response.json();
}

async function postJson(url) {
  return fetchJson(url, { method: 'POST' });
}

async function loadStatus() {
  state.status = await fetchJson('/api/status');
  if (!state.selectedSession && state.status.sessions.length > 0) {
    state.selectedSession = state.status.sessions[0].name;
  }

  if (state.selectedSession) {
    await loadSession(state.selectedSession);
  }

  render();
}

async function loadSession(name) {
  const details = await fetchJson(`/api/sessions/${encodeURIComponent(name)}`);
  state.selectedSession = name;
  state.blocksBySession.set(name, details.blocks || []);
  render();
}

function render() {
  renderTopbar();
  renderCredentials();
  renderSessions();
  renderCurrentSession();
  renderBlocks();
}

function renderTopbar() {
  const summary = document.getElementById('daemon-summary');
  const actions = document.getElementById('daemon-actions');
  if (!state.status) {
    summary.textContent = 'Loading…';
    actions.innerHTML = '';
    return;
  }

  const daemon = state.status.daemon;
  summary.textContent = `daemon up · ${state.status.sessions.length} sessions · ${state.status.recentBlocks.length} recent blocks · port ${daemon.webPort}`;
  actions.innerHTML = `
    <button id="pause-toggle">${daemon.paused ? 'Resume' : 'Pause'}</button>
    <span class="tag">${daemon.pipeName}</span>
  `;

  document.getElementById('pause-toggle').onclick = async () => {
    await postJson(daemon.paused ? '/api/daemon/resume' : '/api/daemon/pause');
    await loadStatus();
  };
}

function renderCredentials() {
  const root = document.getElementById('credentials');
  const credentials = state.status?.credentials || [];
  if (credentials.length === 0) {
    root.innerHTML = '<div class="muted">No held credentials.</div>';
    return;
  }

  root.innerHTML = credentials.map(credential => `
    <div class="panel" style="margin:0; padding:10px;">
      <div><strong>${escapeHtml(credential.identity)}</strong></div>
      <div class="muted">expires ${new Date(credential.expiresAt).toLocaleString()}${credential.emulated ? ' · emulated' : ''}</div>
      <div style="margin-top:8px;">
        <button data-drop="${escapeAttribute(credential.identity)}">Drop</button>
      </div>
    </div>
  `).join('');

  root.querySelectorAll('[data-drop]').forEach(button => {
    button.onclick = async () => {
      await postJson(`/api/credentials/${encodeURIComponent(button.dataset.drop)}/drop`);
      await loadStatus();
    };
  });
}

function renderSessions() {
  const root = document.getElementById('sessions');
  const sessions = state.status?.sessions || [];
  if (sessions.length === 0) {
    root.innerHTML = '<div class="muted">No sessions yet.</div>';
    return;
  }

  root.innerHTML = sessions.map(session => `
    <button class="session-button ${session.name === state.selectedSession ? 'active' : ''}" data-session="${escapeAttribute(session.name)}">
      <div><strong>${escapeHtml(session.name)}</strong></div>
      <div class="muted">${escapeHtml(session.identity)} · ${escapeHtml(session.state)} · ${session.blockCount} blocks</div>
    </button>
  `).join('');

  root.querySelectorAll('[data-session]').forEach(button => {
    button.onclick = () => loadSession(button.dataset.session);
  });
}

function renderCurrentSession() {
  const title = document.getElementById('session-title');
  const meta = document.getElementById('session-meta');
  const actions = document.getElementById('session-actions');
  const session = (state.status?.sessions || []).find(item => item.name === state.selectedSession);

  if (!session) {
    title.textContent = 'No session selected';
    meta.textContent = '';
    actions.innerHTML = '';
    return;
  }

  title.textContent = session.name;
  meta.textContent = `${session.identity} · ${session.persistent ? 'persistent' : 'one-shot'} · ${session.state}`;
  actions.innerHTML = `<button id="destroy-session">Destroy session</button>`;
  document.getElementById('destroy-session').onclick = async () => {
    await postJson(`/api/sessions/${encodeURIComponent(session.name)}/destroy`);
    state.blocksBySession.delete(session.name);
    state.selectedSession = null;
    await loadStatus();
  };
}

function renderBlocks() {
  const root = document.getElementById('blocks');
  const blocks = state.blocksBySession.get(state.selectedSession) || [];
  if (blocks.length === 0) {
    root.innerHTML = '<div class="muted">No blocks for this session yet.</div>';
    return;
  }

  root.innerHTML = blocks.map(block => `
    <article class="block ${escapeAttribute((block.state || '').toLowerCase())}">
      <header>
        <div>
          <div><strong>${escapeHtml(block.id)}</strong> <span class="tag">${escapeHtml(block.state)}</span> <span class="tag">exit ${block.exitCode ?? 'n/a'}</span></div>
          <div class="muted">${escapeHtml(block.identityUsed)}</div>
        </div>
        <div>${block.state === 'running' ? `<button data-cancel="${escapeAttribute(block.id)}">Cancel</button>` : ''}</div>
      </header>
      <div><code>${escapeHtml(block.scriptPreview || '')}</code></div>
      <div class="muted" style="margin-top:8px;">stdout ${block.stdoutLength} bytes · stderr ${block.stderrLength} bytes</div>
    </article>
  `).join('');

  root.querySelectorAll('[data-cancel]').forEach(button => {
    button.onclick = async () => {
      await postJson(`/api/blocks/${encodeURIComponent(button.dataset.cancel)}/cancel`);
      await loadStatus();
      if (state.selectedSession) {
        await loadSession(state.selectedSession);
      }
    };
  });
}

function escapeHtml(value) {
  return String(value ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}

function escapeAttribute(value) {
  return escapeHtml(value);
}

function connectSocket() {
  const protocol = location.protocol === 'https:' ? 'wss' : 'ws';
  const socket = new WebSocket(`${protocol}://${location.host}/ws`);
  socket.onmessage = async () => {
    await loadStatus();
    if (state.selectedSession) {
      await loadSession(state.selectedSession);
    }
  };
  socket.onclose = () => setTimeout(connectSocket, 1500);
}

loadStatus().catch(error => {
  document.getElementById('daemon-summary').textContent = error.message;
});
connectSocket();
