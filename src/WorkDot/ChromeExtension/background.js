const DAOU_ORIGIN = 'https://alinestudio.daouoffice.com';
const TARGET_ATTENDANCE_URL = `${DAOU_ORIGIN}/ehr/app/attend/my-attendance-status`;
const GADGET_TODAY_API = `${DAOU_ORIGIN}/api/attend/normal/gadget/today`;
const BRIDGE_PORTS = [49812];
const POLL_COMMAND_MS = 2000;
const SYNC_INTERVAL_MS = 15000;
const KEEPALIVE_INTERVAL_MS = 20000;

let activePort = BRIDGE_PORTS[0];
let pollTimer = null;
let isPolling = false;
let isSyncing = false;
let hasSyncedValidStatus = false;
let lastSyncTs = 0;
let lastKeepaliveTs = 0;
let wasBridgeDisconnected = true;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const pad2 = (n) => String(n).padStart(2, '0');

const isLoginUrl = (url) => {
  if (!url) return false;
  const u = url.toLowerCase();
  return (
    u.includes('/login') ||
    u.includes('sso') ||
    u.includes('/auth') ||
    u.includes('/otp') ||
    u.includes('twofactor') ||
    u.includes('2fa') ||
    u.includes('mfa') ||
    u.includes('alliance-v2')
  );
};

const isAttendanceCapableUrl = (url) => {
  if (!url || isLoginUrl(url)) return false;
  const u = url.toLowerCase();
  return u.includes('/ehr/') || u.includes('/home');
};

function installWorkDotPageHook() {
  if (window.__workdotMainHookInstalled) return;
  window.__workdotMainHookInstalled = true;

  const forward = (url, data) => {
    try {
      if (!url || !data) return;
      const u = String(url).toLowerCase();
      if (
        u.includes('ehr') ||
        u.includes('attend') ||
        u.includes('timeline') ||
        u.includes('clock') ||
        u.includes('status')
      ) {
        window.postMessage({ __workdotMsg: 'API_DATA', url: String(url), data }, window.location.origin);
      }
    } catch {}
  };

  const origFetch = window.fetch;
  if (origFetch) {
    window.fetch = async function (...args) {
      const res = await origFetch.apply(this, args);
      try {
        const url = args[0] && args[0].url ? args[0].url : String(args[0] || '');
        res
          .clone()
          .json()
          .then((data) => forward(url, data))
          .catch(() => {});
      } catch {}
      return res;
    };
  }

  const origOpen = XMLHttpRequest.prototype.open;
  const origSend = XMLHttpRequest.prototype.send;
  XMLHttpRequest.prototype.open = function (method, url, ...rest) {
    this.__workdotUrl = url;
    return origOpen.call(this, method, url, ...rest);
  };
  XMLHttpRequest.prototype.send = function (...args) {
    this.addEventListener('load', function () {
      try {
        if (this.responseText && this.responseText.trim().startsWith('{')) {
          const data = JSON.parse(this.responseText);
          forward(this.__workdotUrl, data);
        }
      } catch {}
    });
    return origSend.apply(this, args);
  };
}

async function ensureMainWorldHook(tabId) {
  try {
    await chrome.scripting.executeScript({
      target: { tabId },
      world: 'MAIN',
      func: installWorkDotPageHook
    });
  } catch {}
}

async function bridgeFetch(path, options = {}) {
  const portsToTry = [activePort, ...BRIDGE_PORTS.filter((p) => p !== activePort)];
  let lastErr = null;

  for (const port of portsToTry) {
    try {
      const res = await fetch(`http://127.0.0.1:${port}${path}`, {
        ...options,
        cache: 'no-store'
      });
      activePort = port;
      return res;
    } catch (err) {
      lastErr = err;
    }
  }
  throw lastErr || new Error('Bridge unreachable');
}

async function postProbe(probe) {
  if (!probe) return;
  try {
    await bridgeFetch('/api/probe', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(probe)
    });
  } catch {}
}

async function postCommandResult(resultPayload) {
  try {
    await bridgeFetch('/api/command-result', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(resultPayload)
    });
  } catch {}
}

const isoToLocalHms = (raw) => {
  if (typeof raw !== 'string' || !raw) return null;
  if (raw.includes('T')) {
    const d = new Date(raw);
    if (!isNaN(d.getTime())) {
      return `${pad2(d.getHours())}:${pad2(d.getMinutes())}:${pad2(d.getSeconds())}`;
    }
  }
  const m = raw.match(/\b([01]\d|2[0-3]):[0-5]\d(:[0-5]\d)?\b/);
  return m ? m[0] : null;
};

async function fetchAttendanceFromCookies() {
  try {
    const res = await fetch(GADGET_TODAY_API, {
      method: 'GET',
      credentials: 'include',
      redirect: 'manual',
      headers: {
        Accept: 'application/json',
        'X-Requested-With': 'XMLHttpRequest'
      }
    });

    if (res.type === 'opaqueredirect' || res.status === 401 || res.status === 403 || !res.ok) {
      return null;
    }

    const ct = (res.headers.get('content-type') || '').toLowerCase();
    if (!ct.includes('json')) {
      return null;
    }

    const json = await res.json();
    const gd = json?.data;
    if (!gd) {
      return null;
    }

    const clockIn = isoToLocalHms(gd.startWorkTime);
    const clockOut = isoToLocalHms(gd.endWorkTime);
    const ws = String(gd.workStatusName || '').trim();
    const dwt = String(gd.displayWorkType || '').trim().toUpperCase();

    let state = 'OFF_WORK';
    let badgeText = '퇴근';

    if (ws.includes('휴게') || ws.includes('외출')) {
      state = 'BREAK';
      badgeText = '휴게';
    } else if (ws === '출근' || ws === '업무' || ws.includes('근무') || dwt === 'ON') {
      state = 'WORKING';
      badgeText = '근무중';
    } else if (ws === '퇴근' || ws === '업무종료' || dwt === 'OFF' || clockOut) {
      state = 'OFF_WORK';
      badgeText = '퇴근';
    } else if (clockIn && !clockOut) {
      state = 'WORKING';
      badgeText = '근무중';
    } else {
      state = 'OFF_WORK';
      badgeText = '출근 전';
    }

    const canClockIn = Boolean(gd.clockInWorkStatusId);
    const ableList = Array.isArray(gd.ableWorkStatusList) ? gd.ableWorkStatusList : [];
    const canResumeWork = !canClockIn && ableList.some((it) => (it.workStatusName || '').includes('업무'));

    return {
      isLoginPage: false,
      url: TARGET_ATTENDANCE_URL,
      state,
      clockIn,
      clockOut,
      clockOutExplicitlyUnset: Boolean(clockIn && !clockOut),
      duration: null,
      breakRanges: [],
      userBreakRanges: [],
      activeBreakStart: null,
      badgeText,
      apiStatus: ws || dwt,
      canClockIn,
      canResumeWork
    };
  } catch {
    return null;
  }
}

async function queryDaouTabs() {
  try {
    return await chrome.tabs.query({ url: `${DAOU_ORIGIN}/*` });
  } catch {
    return [];
  }
}

function pickBestAttendanceTab(tabs) {
  if (!tabs || tabs.length === 0) return null;
  return (
    tabs.find((t) => (t.url || '').includes('/ehr/app/attend') && !isLoginUrl(t.url)) ||
    tabs.find((t) => (t.url || '').includes('/ehr/') && !isLoginUrl(t.url)) ||
    tabs.find((t) => (t.url || '').includes('/home') && !isLoginUrl(t.url)) ||
    null
  );
}

async function sendMessageToTabSafe(tabId, msg) {
  try {
    return await chrome.tabs.sendMessage(tabId, msg);
  } catch {
    try {
      await chrome.scripting.executeScript({ target: { tabId }, files: ['content.js'] });
      await sleep(200);
      return await chrome.tabs.sendMessage(tabId, msg);
    } catch {
      return null;
    }
  }
}

async function syncAttendance(forceFetch = false) {
  if (isSyncing) return null;
  isSyncing = true;
  lastSyncTs = Date.now();

  try {
    const tabs = await queryDaouTabs();
    const attendanceTab = pickBestAttendanceTab(tabs);

    if (attendanceTab && attendanceTab.id != null) {
      if (attendanceTab.autoDiscardable !== false) {
        chrome.tabs.update(attendanceTab.id, { autoDiscardable: false }).catch(() => {});
      }
      await ensureMainWorldHook(attendanceTab.id);
      const probe = await sendMessageToTabSafe(attendanceTab.id, {
        type: 'WORKDOT_RUN_PROBE',
        forceFetch
      });

      if (
        probe &&
        !probe.isLoginPage &&
        probe.state !== 'PAGE_OUTDATED_NEEDS_RELOAD' &&
        probe.state !== 'LOADING' &&
        probe.state !== 'UNKNOWN'
      ) {
        hasSyncedValidStatus = true;
        await postProbe(probe);
        return probe;
      }
    }

    const cookieProbe = await fetchAttendanceFromCookies();
    if (cookieProbe && !cookieProbe.isLoginPage) {
      hasSyncedValidStatus = true;
      await postProbe(cookieProbe);
      return cookieProbe;
    }

    if (!hasSyncedValidStatus) {
      const loginTab = tabs.find((t) => isLoginUrl(t.url));
      if (loginTab) {
        const loginProbe = { isLoginPage: true, url: loginTab.url || TARGET_ATTENDANCE_URL };
        await postProbe(loginProbe);
        return loginProbe;
      }
    }

    return null;
  } finally {
    isSyncing = false;
  }
}

async function waitForTabComplete(tabId, timeoutMs = 15000) {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    try {
      const t = await chrome.tabs.get(tabId);
      if (t.status === 'complete') return t;
    } catch {
      return null;
    }
    await sleep(300);
  }
  try {
    return await chrome.tabs.get(tabId);
  } catch {
    return null;
  }
}

async function focusTab(tab) {
  if (!tab || tab.id == null) return;
  try {
    if (tab.windowId != null) {
      await chrome.windows.update(tab.windowId, { focused: true });
    }
    await chrome.tabs.update(tab.id, { active: true });
  } catch {}
}

async function handleOpenPortal(cmd) {
  const tabs = await queryDaouTabs();
  const targetTab =
    tabs.find((t) => (t.url || '').includes('/ehr/app/attend')) ||
    tabs.find((t) => (t.url || '').includes('/home')) ||
    tabs.find((t) => isLoginUrl(t.url));

  if (targetTab && targetTab.id != null) {
    await focusTab(targetTab);
  } else {
    const created = await chrome.tabs.create({ url: TARGET_ATTENDANCE_URL, active: true });
    if (created?.windowId != null) {
      try {
        await chrome.windows.update(created.windowId, { focused: true });
      } catch {}
    }
  }

  await postCommandResult({
    id: cmd.id,
    success: true,
    needsLogin: false,
    message: '브라우저에서 다우오피스를 열었습니다.'
  });
}

async function handleClockAction(cmd) {
  const tabs = await queryDaouTabs();
  let tab = pickBestAttendanceTab(tabs);
  let createdTempTab = false;

  if (tab && tab.id != null) {
    const probeCheck = await sendMessageToTabSafe(tab.id, { type: 'WORKDOT_RUN_PROBE' });
    if (probeCheck?.state === 'PAGE_OUTDATED_NEEDS_RELOAD') {
      await chrome.tabs.reload(tab.id);
      tab = await waitForTabComplete(tab.id, 12000);
      await sleep(1200);
    }
  }

  if (!tab || tab.id == null) {
    const loginTab = tabs.find((t) => isLoginUrl(t.url));
    if (loginTab) {
      await focusTab(loginTab);
      const loginProbe = { isLoginPage: true, url: loginTab.url };
      await postProbe(loginProbe);
      await postCommandResult({
        id: cmd.id,
        success: false,
        needsLogin: true,
        message: '크롬에서 다우오피스 로그인이 필요합니다.',
        probe: loginProbe
      });
      return;
    }

    createdTempTab = true;
    tab = await chrome.tabs.create({ url: TARGET_ATTENDANCE_URL, active: false });
    if (tab?.id != null) {
      tab = await waitForTabComplete(tab.id, 15000);
      await sleep(1500);
    }
  }

  if (!tab || tab.id == null) {
    await postCommandResult({
      id: cmd.id,
      success: false,
      needsLogin: false,
      message: '크롬에서 다우오피스 탭을 열지 못했습니다.'
    });
    return;
  }

  if (isLoginUrl(tab.url)) {
    await focusTab(tab);
    const loginProbe = { isLoginPage: true, url: tab.url };
    await postProbe(loginProbe);
    await postCommandResult({
      id: cmd.id,
      success: false,
      needsLogin: true,
      message: '크롬에서 다우오피스 로그인이 필요합니다.',
      probe: loginProbe
    });
    return;
  }

  await ensureMainWorldHook(tab.id);
  const execRes = await sendMessageToTabSafe(tab.id, {
    type: 'WORKDOT_EXEC_ACTION',
    action: cmd.action
  });

  if (!execRes) {
    await postCommandResult({
      id: cmd.id,
      success: false,
      needsLogin: false,
      message: '다우오피스 페이지 스크립트와 통신하지 못했습니다.'
    });
    return;
  }

  if (execRes.needsLogin) {
    await focusTab(tab);
  }

  if (execRes.probe) {
    await postProbe(execRes.probe);
  }

  await postCommandResult({
    id: cmd.id,
    success: Boolean(execRes.success),
    needsLogin: Boolean(execRes.needsLogin),
    message: execRes.message || '',
    probe: execRes.probe || null
  });

  if (createdTempTab && execRes.success && !execRes.needsLogin) {
    setTimeout(() => {
      try {
        chrome.tabs.remove(tab.id).catch(() => {});
      } catch {}
    }, 800);
  }
}

async function handleCommand(cmd) {
  if (!cmd || !cmd.action || cmd.action === 'NONE') return;

  try {
    if (cmd.action === 'OPEN_PORTAL') {
      await handleOpenPortal(cmd);
    } else if (cmd.action === 'REFRESH') {
      const probe = await syncAttendance(true);
      await postCommandResult({
        id: cmd.id,
        success: true,
        needsLogin: Boolean(probe?.isLoginPage),
        message: '새로고침 완료',
        probe
      });
    } else {
      await handleClockAction(cmd);
    }
  } catch (err) {
    await postCommandResult({
      id: cmd.id,
      success: false,
      needsLogin: false,
      message: err?.message || '명령 처리 중 오류'
    });
  }
}

async function pollCommandOnce() {
  if (isPolling) return;
  isPolling = true;

  try {
    const res = await bridgeFetch('/api/command', { method: 'GET' });
    if (res.ok) {
      const wasDisconnected = wasBridgeDisconnected;
      wasBridgeDisconnected = false;

      if (wasDisconnected) {
        syncAttendance(false).catch(() => {});
      }

      const cmd = await res.json();
      if (cmd && cmd.action && cmd.action !== 'NONE') {
        await handleCommand(cmd);
      }
    }
  } catch {
    wasBridgeDisconnected = true;
  } finally {
    isPolling = false;
  }

  const now = Date.now();
  if (!wasBridgeDisconnected && now - lastSyncTs >= SYNC_INTERVAL_MS) {
    syncAttendance(false).catch(() => {});
  }

  if (now - lastKeepaliveTs >= KEEPALIVE_INTERVAL_MS) {
    lastKeepaliveTs = now;
    try {
      chrome.runtime.getPlatformInfo(() => {});
    } catch {}
  }
}

function ensurePollingLoop() {
  if (pollTimer) return;
  pollCommandOnce();
  pollTimer = setInterval(pollCommandOnce, POLL_COMMAND_MS);
}

function setupAlarms() {
  try {
    chrome.alarms.create('workdot-poll', { periodInMinutes: 0.5 });
  } catch {}
}

chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm?.name === 'workdot-poll') {
    ensurePollingLoop();
    pollCommandOnce();
  }
});

chrome.tabs.onUpdated.addListener((tabId, changeInfo, tab) => {
  if (tab?.url && tab.url.startsWith(DAOU_ORIGIN)) {
    if (changeInfo.status === 'loading' || changeInfo.status === 'complete') {
      ensureMainWorldHook(tabId);
    }
    if (changeInfo.status === 'complete' && isAttendanceCapableUrl(tab.url)) {
      setTimeout(() => syncAttendance(false), 600);
    }
  }
});

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (msg?.type === 'WORKDOT_PROBE' && msg.payload) {
    ensurePollingLoop();
    postProbe(msg.payload).then(() => sendResponse({ ok: true }));
    return true;
  }
  return false;
});

chrome.runtime.onInstalled.addListener(() => {
  setupAlarms();
  ensurePollingLoop();
  syncAttendance(false);
});

chrome.runtime.onStartup.addListener(() => {
  setupAlarms();
  ensurePollingLoop();
  syncAttendance(false);
});

setupAlarms();
ensurePollingLoop();
