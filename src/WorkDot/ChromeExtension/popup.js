(() => {
  const manifest = chrome.runtime.getManifest();
  const currentVersion = manifest?.version || '2.0.2';

  const appNameEl = document.getElementById('app-name');
  const appVersionEl = document.getElementById('app-version');
  const bridgeDotEl = document.getElementById('bridge-dot');
  const updateSubEl = document.getElementById('update-sub');
  const btnOpenDaou = document.getElementById('btn-open-daou');
  const btnUpdateVersion = document.getElementById('btn-update-version');

  if (appNameEl) {
    appNameEl.textContent = 'WorkDot';
  }
  if (appVersionEl) {
    appVersionEl.textContent = `v${currentVersion}`;
  }

  chrome.runtime.sendMessage({ type: 'WORKDOT_GET_STATUS' }, (res) => {
    if (chrome.runtime.lastError || !res) {
      if (bridgeDotEl) {
        bridgeDotEl.className = 'status-dot disconnected';
        bridgeDotEl.title = 'WorkDot 위젯 연결 대기 중';
      }
      return;
    }

    if (bridgeDotEl) {
      if (res.connected) {
        bridgeDotEl.className = 'status-dot connected';
        bridgeDotEl.title = 'WorkDot 위젯 연결됨';
      } else {
        bridgeDotEl.className = 'status-dot disconnected';
        bridgeDotEl.title = 'WorkDot 위젯 연결 대기 중';
      }
    }

    if (updateSubEl && res.appVersion && res.appVersion !== currentVersion) {
      updateSubEl.textContent = `v${res.appVersion} 업데이트 적용`;
      updateSubEl.classList.add('highlight');
    }
  });

  btnOpenDaou?.addEventListener('click', () => {
    chrome.runtime.sendMessage({ type: 'WORKDOT_OPEN_DAOU' }, () => {
      window.close();
    });
  });

  btnUpdateVersion?.addEventListener('click', () => {
    if (updateSubEl) {
      updateSubEl.textContent = '업데이트 중...';
    }
    chrome.runtime.reload();
  });
})();
