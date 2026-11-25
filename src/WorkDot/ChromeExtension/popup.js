(() => {
  const AUTO_LOGIN_STORAGE_KEY = 'workdotAutoLogin';
  const manifest = chrome.runtime.getManifest();
  const currentVersion = manifest?.version || '2.0.4';

  const appNameEl = document.getElementById('app-name');
  const appVersionEl = document.getElementById('app-version');
  const bridgeDotEl = document.getElementById('bridge-dot');
  const updateSubEl = document.getElementById('update-sub');
  const btnOpenDaou = document.getElementById('btn-open-daou');
  const btnUpdateVersion = document.getElementById('btn-update-version');

  const btnToggleAutoLogin = document.getElementById('btn-toggle-autologin');
  const autoLoginPanel = document.getElementById('auto-login-panel');
  const autoLoginSubEl = document.getElementById('autologin-sub');
  const chkEnabled = document.getElementById('chk-auto-login-enabled');
  const chkAutoSubmit = document.getElementById('chk-auto-login-submit');
  const inputUsername = document.getElementById('input-username');
  const inputPassword = document.getElementById('input-password');
  const btnSaveAutoLogin = document.getElementById('btn-save-autologin');
  const btnClearAutoLogin = document.getElementById('btn-clear-autologin');
  const autoLoginMsgEl = document.getElementById('auto-login-msg');

  if (appNameEl) {
    appNameEl.textContent = 'WorkDot';
  }
  if (appVersionEl) {
    appVersionEl.textContent = `v${currentVersion}`;
  }

  const updateAutoLoginSubText = (cfg) => {
    if (!autoLoginSubEl) return;
    if (cfg && cfg.enabled && cfg.username && cfg.password) {
      const modeLabel = cfg.autoSubmit !== false ? '켜짐' : '자동입력만';
      autoLoginSubEl.textContent = `${modeLabel} · ${cfg.username}`;
      autoLoginSubEl.classList.add('highlight');
    } else if (cfg && cfg.username) {
      autoLoginSubEl.textContent = `꺼짐 · ${cfg.username}`;
      autoLoginSubEl.classList.remove('highlight');
    } else {
      autoLoginSubEl.textContent = '꺼짐';
      autoLoginSubEl.classList.remove('highlight');
    }
  };

  const showPanelMessage = (text, isWarn = false) => {
    if (!autoLoginMsgEl) return;
    autoLoginMsgEl.textContent = text;
    autoLoginMsgEl.classList.toggle('warn', isWarn);
  };

  // Load saved auto-login config
  if (chrome.storage?.local) {
    chrome.storage.local.get([AUTO_LOGIN_STORAGE_KEY], (result) => {
      const cfg = result?.[AUTO_LOGIN_STORAGE_KEY] || {};
      if (chkEnabled) chkEnabled.checked = Boolean(cfg.enabled);
      if (chkAutoSubmit) chkAutoSubmit.checked = cfg.autoSubmit !== false;
      if (inputUsername) inputUsername.value = cfg.username || '';
      if (inputPassword) inputPassword.value = cfg.password || '';
      updateAutoLoginSubText(cfg);
    });
  }

  btnToggleAutoLogin?.addEventListener('click', () => {
    const isOpen = autoLoginPanel?.classList.toggle('open');
    btnToggleAutoLogin.classList.toggle('active', Boolean(isOpen));
    showPanelMessage('');
  });

  btnSaveAutoLogin?.addEventListener('click', () => {
    const username = (inputUsername?.value || '').trim();
    const password = inputPassword?.value || '';
    const enabled = Boolean(chkEnabled?.checked);
    const autoSubmit = Boolean(chkAutoSubmit?.checked);

    if (enabled && (!username || !password)) {
      showPanelMessage('아이디와 비밀번호를 모두 입력하세요.', true);
      return;
    }

    // 처음 계정을 입력하고 저장을 누른 경우 체크박스를 깜빡했더라도 자동으로 활성화
    if (chkEnabled && username && password && !enabled && autoLoginSubEl?.textContent === '꺼짐') {
      chkEnabled.checked = true;
    }

    const cfg = {
      enabled: Boolean(chkEnabled?.checked),
      autoSubmit,
      username,
      password
    };

    chrome.storage.local.set({ [AUTO_LOGIN_STORAGE_KEY]: cfg }, () => {
      updateAutoLoginSubText(cfg);
      showPanelMessage(cfg.enabled ? '저장되었습니다. 로그인 화면에서 자동 적용됩니다.' : '저장되었습니다 (비활성 상태).');

      // If currently on a DaouOffice login tab, trigger auto-login immediately
      if (cfg.enabled) {
        chrome.runtime.sendMessage({ type: 'WORKDOT_TRIGGER_AUTOLOGIN_ON_ACTIVE' });
      }
    });
  });

  btnClearAutoLogin?.addEventListener('click', () => {
    chrome.storage.local.remove([AUTO_LOGIN_STORAGE_KEY], () => {
      if (chkEnabled) chkEnabled.checked = false;
      if (chkAutoSubmit) chkAutoSubmit.checked = true;
      if (inputUsername) inputUsername.value = '';
      if (inputPassword) inputPassword.value = '';
      updateAutoLoginSubText(null);
      showPanelMessage('저장된 계정 정보가 삭제되었습니다.');
    });
  });

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
