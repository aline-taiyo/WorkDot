(() => {
  if (window.__workdotContentInitialized) return;
  window.__workdotContentInitialized = true;

  const apiLogs = [];
  let probeTimer = null;
  let lastGadgetFetchTs = 0;

  const pad2 = (n) => String(n).padStart(2, '0');
  const clean = (s) => (s || '').replace(/\s+/g, ' ').trim();

  const recordApiData = (url, data) => {
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
        apiLogs.push({ url: String(url), data, ts: Date.now() });
        if (apiLogs.length > 30) apiLogs.shift();
        scheduleProbe(250);
      }
    } catch {}
  };

  window.addEventListener('message', (event) => {
    if (event.source !== window) return;
    const msg = event.data;
    if (msg && msg.__workdotMsg === 'API_DATA') {
      recordApiData(msg.url, msg.data);
    }
  });

  const ensureTodayGadgetApiData = async (force = false) => {
    const nowTs = Date.now();
    const hasRecent = apiLogs.some(
      (l) => (l.url || '').includes('gadget/today') && nowTs - l.ts < 15000
    );
    if (!force && (hasRecent || nowTs - lastGadgetFetchTs < 4000)) {
      return;
    }

    lastGadgetFetchTs = nowTs;
    try {
      const res = await fetch('/api/attend/normal/gadget/today', {
        method: 'GET',
        credentials: 'include',
        redirect: 'manual',
        headers: { Accept: 'application/json' }
      });
      if (res.type === 'opaqueredirect' || res.status === 401 || res.status === 403 || !res.ok) {
        return;
      }
      const ct = (res.headers.get('content-type') || '').toLowerCase();
      if (!ct.includes('json')) return;
      const json = await res.json();
      if (json && json.data) {
        apiLogs.push({ url: '/api/attend/normal/gadget/today', data: json, ts: Date.now() });
        if (apiLogs.length > 30) apiLogs.shift();
      }
    } catch {}
  };

  const toLocalTimeHms = (raw) => {
    if (typeof raw !== 'string') return raw;
    if (raw.includes('T')) {
      try {
        const d = new Date(raw);
        if (!isNaN(d.getTime())) {
          return `${pad2(d.getHours())}:${pad2(d.getMinutes())}:${pad2(d.getSeconds())}`;
        }
      } catch {}
    }
    return raw;
  };

  const runProbe = () => {
    const url = location.href;
    const urlLower = url.toLowerCase();

    const hasEhrAttendUi = Boolean(
      document.querySelector('.lnb_gadget.gadget_attend, .attend_card, #timelineClock, .wrap_today_time')
    );

    const isAuthUrl =
      urlLower.includes('/login') ||
      urlLower.includes('/auth') ||
      urlLower.includes('/otp') ||
      urlLower.includes('/twofactor') ||
      urlLower.includes('2fa') ||
      urlLower.includes('mfa') ||
      urlLower.includes('sso') ||
      urlLower.includes('alliance-v2');

    if (!hasEhrAttendUi) {
      if (isAuthUrl) {
        return { isLoginPage: true, url };
      }
      return { isLoginPage: false, state: 'LOADING', url };
    }

    const timeOnlyRe = /\b([01]\d|2[0-3]):[0-5]\d(:[0-5]\d)?\b/;
    const unsetRe = /(미등록|미체크|퇴근전|출근전|--:--|^-$|^-\s*:\s*-)/;

    const docs = [document];
    document.querySelectorAll('iframe').forEach((ifr) => {
      try {
        if (ifr.contentDocument) docs.push(ifr.contentDocument);
      } catch {}
    });

    const liveClockTimes = new Set();
    const now = new Date();
    for (let diff = -2; diff <= 2; diff++) {
      const d = new Date(now.getTime() + diff * 1000);
      liveClockTimes.add(`${pad2(d.getHours())}:${pad2(d.getMinutes())}:${pad2(d.getSeconds())}`);
    }
    for (const doc of docs) {
      doc.querySelectorAll('#timelineClock, .time_info, .clock, #clock, .now_time, .real_time').forEach((el) => {
        const m = clean(el.innerText).match(timeOnlyRe);
        if (m) liveClockTimes.add(m[0]);
      });
    }

    const isLiveClockValue = (t) => t && liveClockTimes.has(t);

    let clockIn = null;
    let clockOut = null;
    let clockOutExplicitlyUnset = false;
    let duration = null;
    let badgeText = null;
    let apiStatus = null;
    const breakRangesSet = new Set();

    const addBreakRange = (startStr, endStr) => {
      if (!startStr || !endStr) return;
      const sm = String(startStr).match(/([01]\d|2[0-3]):[0-5]\d/);
      const em = String(endStr).match(/([01]\d|2[0-3]):[0-5]\d/);
      if (sm && em) {
        breakRangesSet.add(`${sm[0]}~${em[0]}`);
      }
    };

    const yyyy = now.getFullYear();
    const mm = pad2(now.getMonth() + 1);
    const dd = pad2(now.getDate());
    const todayIso = `${yyyy}-${mm}-${dd}`;
    const todayDot = `${yyyy}.${mm}.${dd}`;
    const todayCompact = `${yyyy}${mm}${dd}`;

    // 전략 0: 다우오피스 EHR 좌측 실시간 근태 가젯 DOM
    for (const doc of docs) {
      const gadget = doc.querySelector('.lnb_gadget.gadget_attend, .attend_card');
      if (!gadget) continue;

      const timeEl = gadget.querySelector('.wrap_today_time .today_time, .today_time');
      const dateTxt = clean(timeEl?.innerText || '');
      if (dateTxt) {
        const mMonth = dateTxt.match(/(\d{1,2})월/);
        const mDay = dateTxt.match(/(\d{1,2})일/);
        if (mMonth && mDay) {
          const gMonth = pad2(parseInt(mMonth[1], 10));
          const gDay = pad2(parseInt(mDay[1], 10));
          if (gMonth !== mm || gDay !== dd) {
            return { state: 'PAGE_OUTDATED_NEEDS_RELOAD', url };
          }
        }
      }

      const tag = gadget.querySelector('.gadget_header .basic_tag, .card_header .basic_tag');
      if (tag) {
        const tagCls = (tag.className || '').toLowerCase();
        const tagTxt = clean(tag.innerText);
        if (tagTxt === '휴게' || tagTxt === '외출') {
          badgeText = '휴게';
          apiStatus = tagTxt;
        } else if (tagCls.includes('positive') || tagTxt === '출근' || tagTxt === '업무' || tagTxt.includes('근무')) {
          badgeText = '근무중';
          apiStatus = tagTxt || 'ON';
        } else if (tagTxt === '퇴근' || tagTxt === '업무종료') {
          badgeText = '퇴근';
          apiStatus = tagTxt || 'OFF';
        } else if (tagCls.includes('neutral')) {
          badgeText = '퇴근';
          apiStatus = tagTxt || 'OFF';
        }
      }

      const workTypes = Array.from(gadget.querySelectorAll('.working_hours .work_type'));
      for (const wt of workTypes) {
        const tit = clean(wt.querySelector('.tit')?.innerText || '');
        const data = clean(wt.querySelector('.data')?.innerText || '');
        if (tit.includes('출근')) {
          const m = data.match(timeOnlyRe);
          if (m && !isLiveClockValue(m[0])) clockIn = m[0];
        } else if (tit.includes('퇴근')) {
          const m = data.match(timeOnlyRe);
          if (m && !isLiveClockValue(m[0])) {
            clockOut = m[0];
            clockOutExplicitlyUnset = false;
          } else if (unsetRe.test(data) && !clockOut) {
            clockOutExplicitlyUnset = true;
          }
        }
      }
    }

    // 전략 1: API 로그 분석
    if (apiLogs.length > 0) {
      let lockedLatestAttendRecord = Boolean(clockIn || clockOut);
      for (let i = apiLogs.length - 1; i >= 0; i--) {
        const entry = apiLogs[i];
        if (!entry || !entry.data) continue;

        if (entry.url && entry.url.includes('gadget/today') && entry.data?.data && !badgeText) {
          const gd = entry.data.data;
          const ws = clean(gd.workStatusName || '');
          const dwt = clean(gd.displayWorkType || '').toUpperCase();
          if (ws === '휴게' || ws === '외출') {
            badgeText = '휴게';
            apiStatus = ws;
          } else if (ws === '출근' || ws === '업무' || ws.includes('근무') || dwt === 'ON') {
            badgeText = '근무중';
            apiStatus = ws || dwt;
          } else if (ws === '퇴근' || ws === '업무종료' || dwt === 'OFF') {
            badgeText = '퇴근';
            apiStatus = ws || dwt;
          }
        }

        let entryHadAttendRecord = false;
        const visit = (obj) => {
          if (!obj || typeof obj !== 'object') return;
          if (Array.isArray(obj)) {
            obj.forEach(visit);
            return;
          }

          if (Array.isArray(obj.fixedBreakTimeList)) {
            obj.fixedBreakTimeList.forEach((b) => {
              if (b && b.breakStartTime && b.breakEndTime) {
                addBreakRange(b.breakStartTime, b.breakEndTime);
              }
            });
          }

          const dVal =
            obj.date ||
            obj.accrualDate ||
            obj.logDate ||
            obj.workDate ||
            obj.baseDate ||
            obj.targetDate ||
            obj.attndDate ||
            obj.solarDate;
          if (typeof dVal === 'string') {
            const dClean = dVal.slice(0, 10);
            if (
              /^\d{4}[-.]?\d{2}[-.]?\d{2}/.test(dClean) &&
              !dClean.includes(todayIso) &&
              !dClean.includes(todayDot) &&
              !dClean.includes(todayCompact)
            ) {
              return;
            }
          }

          if (
            obj.dailyWorkHoursDto &&
            typeof obj.dailyWorkHoursDto.sumWorkingHours === 'number' &&
            obj.dailyWorkHoursDto.sumWorkingHours > 0 &&
            !duration
          ) {
            const totalSec = obj.dailyWorkHoursDto.sumWorkingHours;
            const h = Math.floor(totalSec / 3600);
            const m = Math.floor((totalSec % 3600) / 60);
            duration = `${pad2(h)}:${pad2(m)}`;
          }

          if (!lockedLatestAttendRecord) {
            let inRaw =
              obj.startWorkTime ??
              obj.clockInTime ??
              obj.workInTime ??
              obj.attndInTime ??
              obj.inTime ??
              obj.checkInTime ??
              obj.realClockInTime;
            let outRaw =
              obj.endWorkTime ??
              obj.clockOutTime ??
              obj.workOutTime ??
              obj.attndOutTime ??
              obj.outTime ??
              obj.checkOutTime ??
              obj.realClockOutTime;
            const stRaw =
              obj.displayWorkType ??
              obj.workStatusName ??
              obj.workStatus ??
              obj.timelineStatus ??
              obj.attndStatus ??
              obj.workingStatus ??
              obj.statusName ??
              obj.stateName;

            inRaw = toLocalTimeHms(inRaw);
            outRaw = toLocalTimeHms(outRaw);

            if (typeof inRaw === 'string' && timeOnlyRe.test(inRaw) && !clockIn) {
              const m = inRaw.match(timeOnlyRe)[0];
              if (!isLiveClockValue(m)) {
                clockIn = m;
                entryHadAttendRecord = true;
              }
            }
            if (typeof outRaw === 'string') {
              if (timeOnlyRe.test(outRaw) && !outRaw.startsWith('00:00') && !clockOut) {
                const m = outRaw.match(timeOnlyRe)[0];
                if (!isLiveClockValue(m)) {
                  clockOut = m;
                  clockOutExplicitlyUnset = false;
                  entryHadAttendRecord = true;
                }
              } else if ((!outRaw || unsetRe.test(outRaw)) && !clockOut) {
                clockOutExplicitlyUnset = true;
              }
            } else if (inRaw && (outRaw === null || outRaw === undefined || outRaw === '') && !clockOut) {
              clockOutExplicitlyUnset = true;
            }

            if (typeof stRaw === 'string' && !apiStatus) {
              apiStatus = stRaw;
            }
          }

          for (const k of Object.keys(obj)) {
            if (typeof obj[k] === 'object') visit(obj[k]);
          }
        };
        try {
          visit(entry.data);
        } catch {}
        if (entryHadAttendRecord) {
          lockedLatestAttendRecord = true;
        }
      }
    }

    // 전략 2: DOM 탐색
    const isInsidePastTableRow = (el) => {
      const tr = el.closest('tr, .tb_row, .timeline_row, [class*="row"]');
      if (!tr) return false;
      const trText = clean(tr.innerText);
      const hasDatePrefix = /\b\d{2}\s*\([월화수목금토일]\)|\b\d{4}[-.]\d{2}[-.]\d{2}\b/.test(trText);
      if (!hasDatePrefix) return false;

      const isTodayRow =
        tr.classList.contains('today') ||
        tr.querySelector('.today, .badge_today, [class*="today"]') !== null ||
        trText.includes(todayIso) ||
        trText.includes(todayDot) ||
        trText.includes(`${mm}.${dd}`) ||
        trText.includes(`${mm}-${dd}`) ||
        new RegExp(`\\b${dd}\\s*\\([월화수목금토일]\\)`).test(trText);

      return !isTodayRow;
    };

    for (const doc of docs) {
      const inSelectors = [
        '#workInTime',
        '#clockInTime',
        '.work_in_time',
        '.clock_in_time',
        '[data-role="clockInTime"]',
        '[data-id="clockInTime"]'
      ];
      for (const sel of inSelectors) {
        const el = doc.querySelector(sel);
        if (el) {
          const m = clean(el.innerText).match(timeOnlyRe);
          if (m && !isLiveClockValue(m[0]) && !clockIn) clockIn = m[0];
        }
      }

      const outSelectors = [
        '#workOutTime',
        '#clockOutTime',
        '.work_out_time',
        '.clock_out_time',
        '[data-role="clockOutTime"]',
        '[data-id="clockOutTime"]'
      ];
      for (const sel of outSelectors) {
        const el = doc.querySelector(sel);
        if (el) {
          const t = clean(el.innerText);
          const m = t.match(timeOnlyRe);
          if (m && !isLiveClockValue(m[0]) && !clockOut) clockOut = m[0];
          else if (unsetRe.test(t)) clockOutExplicitlyUnset = true;
        }
      }

      const allNodes = Array.from(doc.querySelectorAll('dt, th, span, div, p, strong, label, li'));
      for (const el of allNodes) {
        if (isInsidePastTableRow(el)) continue;

        const ownText = clean(el.innerText);
        if (!ownText || ownText.length > 40) continue;

        if (/^(출근시간|출근시각|출근|업무시작)$/.test(ownText)) {
          const sib = el.nextElementSibling;
          if (sib) {
            const sibText = clean(sib.innerText);
            const m = sibText.match(timeOnlyRe);
            if (m && !isLiveClockValue(m[0]) && !clockIn) clockIn = m[0];
          }
        } else if (!clockIn) {
          const afterInMatch = ownText.match(
            /(?:출근시간|출근시각|출근|업무시작)\s*[:：]?\s*([01]\d|2[0-3]):[0-5]\d(:[0-5]\d)?/
          );
          if (afterInMatch) {
            const m = afterInMatch[0].match(timeOnlyRe);
            if (m && !isLiveClockValue(m[0])) clockIn = m[0];
          }
        }

        if (/^(퇴근시간|퇴근시각|퇴근|업무종료)$/.test(ownText)) {
          const sib = el.nextElementSibling;
          if (sib) {
            const sibText = clean(sib.innerText);
            const m = sibText.match(timeOnlyRe);
            if (m && !isLiveClockValue(m[0]) && !clockOut) clockOut = m[0];
            else if (unsetRe.test(sibText)) clockOutExplicitlyUnset = true;
          }
        } else if (!clockOut) {
          const afterOutMatch = ownText.match(
            /(?:퇴근시간|퇴근시각|퇴근|업무종료)\s*[:：]?\s*([01]\d|2[0-3]):[0-5]\d(:[0-5]\d)?/
          );
          if (afterOutMatch) {
            const m = afterOutMatch[0].match(timeOnlyRe);
            if (m && !isLiveClockValue(m[0])) clockOut = m[0];
          } else if (/(?:퇴근시간|퇴근시각|퇴근)\s*[:：]?\s*(미등록|--|-)/.test(ownText)) {
            clockOutExplicitlyUnset = true;
          }
        }

        if (/^(휴게시간|휴게)$/.test(ownText)) {
          const sib = el.nextElementSibling;
          if (sib) {
            const sibText = clean(sib.innerText);
            const rangeMatches = sibText.matchAll(/([01]\d|2[0-3]):[0-5]\d\s*~\s*([01]\d|2[0-3]):[0-5]\d/g);
            for (const rm of rangeMatches) {
              addBreakRange(rm[0].split('~')[0], rm[0].split('~')[1]);
            }
          }
        }

        if (/^(총 근무시간|총근무시간|인정근무시간)$/.test(ownText) && !duration) {
          const sib = el.nextElementSibling;
          if (sib) {
            const sibText = clean(sib.innerText);
            const hmMatch = sibText.match(/(\d+)\s*h\s*(\d+)\s*m/i) || sibText.match(/(\d+)\s*시간\s*(\d+)\s*분/);
            if (hmMatch) {
              const h = parseInt(hmMatch[1], 10);
              const m = parseInt(hmMatch[2], 10);
              if (h > 0 || m > 0) {
                duration = `${pad2(h)}:${pad2(m)}`;
              }
            }
          }
        }
      }

      const todayRows = Array.from(doc.querySelectorAll('tr.today, .today_row, .tb_attend .today'));
      for (const row of todayRows) {
        const rowToCheck = row.closest('tr') || row;
        const times = (clean(rowToCheck.innerText).match(/\b([01]\d|2[0-3]):[0-5]\d(:[0-5]\d)?\b/g) || []).filter(
          (t) => !isLiveClockValue(t)
        );
        if (times.length === 1 && !clockIn) {
          clockIn = times[0];
          clockOutExplicitlyUnset = true;
        } else if (times.length >= 2) {
          if (!clockIn) clockIn = times[0];
          if (!clockOut && !clockOutExplicitlyUnset) clockOut = times[1];
        }
      }

      const statusEls = Array.from(
        doc.querySelectorAll(
          'select option:checked, .gadget_header .basic_tag .txt, .card_header .basic_tag .txt, .basic_tag .txt, .state, .status, .badge, .chip, .txt_state, .btn_state'
        )
      );
      for (const el of statusEls) {
        if (el.tagName !== 'OPTION' && el.offsetParent === null) continue;
        if (el.closest('ul, .layer_transition, .dropdown, [style*="display: none"], [style*="display:none"]')) continue;
        if (isInsidePastTableRow(el)) continue;

        const t = clean(el.innerText);
        if (!t || t.length > 12) continue;

        if (['근무중', '근무 중', '업무중', '업무 중', '정상근무', '연장근무', '외근', '출장', '출근'].includes(t)) {
          badgeText = t;
          break;
        }
        if (['퇴근', '업무종료', '업무 종료', '출근전', '출근 전', '미출근'].includes(t)) {
          badgeText = t;
        }
      }
    }

    let clockInBtnFound = false;
    let clockInBtnDisabled = false;
    let clockOutBtnFound = false;
    let clockOutBtnDisabled = false;

    for (const doc of docs) {
      const buttons = Array.from(
        doc.querySelectorAll('button, a, span[role="button"], div[role="button"], #workIn, #workOut, #clockIn, #clockOut')
      );
      for (const btn of buttons) {
        const t = clean(btn.innerText);
        const id = (btn.id || '').toLowerCase();
        const cls = ((btn.className || '') + ' ' + (btn.parentElement?.className || '')).toLowerCase();
        const isDisabled =
          btn.disabled ||
          cls.includes('disabled') ||
          cls.includes('disable') ||
          cls.includes('off') ||
          cls.includes('done') ||
          btn.getAttribute('aria-disabled') === 'true';

        if (id === 'workin' || id === 'clockin' || t.startsWith('출근하기') || t === '출근') {
          clockInBtnFound = true;
          if (isDisabled) clockInBtnDisabled = true;
          const m = t.match(timeOnlyRe);
          if (m && !isLiveClockValue(m[0]) && !clockIn) clockIn = m[0];
        }
        if (id === 'workout' || id === 'clockout' || t.startsWith('퇴근하기') || t === '퇴근') {
          clockOutBtnFound = true;
          if (isDisabled) clockOutBtnDisabled = true;
          const m = t.match(timeOnlyRe);
          if (m && !isLiveClockValue(m[0]) && !clockOut) clockOut = m[0];
        }
      }
    }

    if (clockOutExplicitlyUnset && !clockOut) {
      clockOut = null;
    }

    const hasValidClockIn = Boolean(clockIn && !clockIn.startsWith('00:00'));
    const hasValidClockOut = Boolean(clockOut && !clockOut.startsWith('00:00'));

    let state = 'UNKNOWN';

    const combinedStatus = ((apiStatus || '') + ' ' + (badgeText || '')).toUpperCase();
    const isBreakStatus =
      combinedStatus.includes('휴게') || combinedStatus.includes('BREAK') || combinedStatus.includes('외출');
    const isWorkStatus =
      combinedStatus.includes('WORKING') ||
      combinedStatus.includes('WORK_IN') ||
      combinedStatus.includes('ON_WORK') ||
      combinedStatus.includes('근무중') ||
      combinedStatus.includes('근무 중') ||
      combinedStatus.includes('업무중') ||
      combinedStatus.includes('업무 중') ||
      combinedStatus.includes('업무') ||
      combinedStatus === 'ON' ||
      combinedStatus.includes(' ON') ||
      combinedStatus.includes('ON ') ||
      combinedStatus.includes('출근');

    if (isBreakStatus) {
      state = 'BREAK';
    } else if (isWorkStatus && !combinedStatus.includes('출근전') && !combinedStatus.includes('출근 전')) {
      state = 'WORKING';
    } else if (clockInBtnFound && clockInBtnDisabled && clockOutBtnFound && !clockOutBtnDisabled && !hasValidClockOut) {
      state = 'WORKING';
    } else if (hasValidClockIn && !hasValidClockOut) {
      state = 'WORKING';
    } else if (hasValidClockOut || (clockInBtnFound && clockInBtnDisabled && clockOutBtnFound && clockOutBtnDisabled)) {
      state = 'OFF_WORK';
    } else if (clockInBtnFound && !clockInBtnDisabled) {
      state = 'OFF_WORK';
    } else {
      if (
        combinedStatus.includes('WORK_OUT') ||
        combinedStatus.includes('OFF_WORK') ||
        combinedStatus.includes('OFF') ||
        combinedStatus.includes('업무종료') ||
        combinedStatus.includes('출근전') ||
        combinedStatus.includes('퇴근')
      ) {
        state = 'OFF_WORK';
      } else {
        state = 'LOADING';
      }
    }

    const todayGadgetDataObj = [...apiLogs].reverse().find((l) => (l.url || '').includes('gadget/today'))?.data || null;
    const canClockIn = Boolean(
      todayGadgetDataObj?.data?.clockInWorkStatusId || (clockInBtnFound && !clockInBtnDisabled)
    );

    let canResumeWork = false;
    const ableList = todayGadgetDataObj?.data?.ableWorkStatusList || [];
    if (Array.isArray(ableList) && ableList.some((item) => (item.workStatusName || '').includes('업무'))) {
      canResumeWork = true;
    }

    if (!canResumeWork) {
      for (const doc of docs) {
        const items = Array.from(
          doc.querySelectorAll('.wrap_lnb_btn .menu_item, .select_list .menu_item, .list_menu button, .attend_btn button')
        );
        for (const it of items) {
          const t = clean(it.innerText);
          if ((t === '업무' || t.startsWith('업무') || t === '정상근무' || t.includes('복귀')) && !it.disabled) {
            canResumeWork = true;
            break;
          }
        }
        if (canResumeWork) break;
      }
    }

    if (!canResumeWork && clockInBtnFound && clockInBtnDisabled) {
      const hasChangeBtn = Boolean(document.querySelector('.wrap_lnb_btn button, .attend_btn .wrap_lnb_btn button'));
      if (hasChangeBtn) {
        canResumeWork = true;
      }
    }

    if (canClockIn) {
      canResumeWork = false;
    }

    // 사용자 휴게 구간 계산
    const historyEvents = [];
    const addHistoryEvent = (hm, label) => {
      if (!hm || !label) return;
      let kind = 'WORK';
      if (label.includes('휴게') || label.includes('외출')) {
        kind = 'BREAK';
      } else if (label.includes('퇴근') || label.includes('업무종료') || label.includes('종료')) {
        kind = 'OUT';
      }
      if (!historyEvents.some((e) => e.time === hm && e.kind === kind)) {
        historyEvents.push({ time: hm, kind, label });
      }
    };

    const histRe =
      /^([01]\d|2[0-3]):([0-5]\d)(?::[0-5]\d)?\s*(출근|업무시작|업무|정상근무|근무|복귀|연장근무|외근|출장|휴게|외출|퇴근|업무종료)$/;
    for (const doc of docs) {
      const histNodes = Array.from(doc.querySelectorAll('li, .work_stop, div.txt'));
      for (const el of histNodes) {
        if (isInsidePastTableRow(el)) continue;
        const t = clean(el.innerText);
        const m = t.match(histRe);
        if (m) {
          addHistoryEvent(`${m[1]}:${m[2]}`, m[3]);
        }
      }
    }

    for (let i = apiLogs.length - 1; i >= 0; i--) {
      const entry = apiLogs[i];
      if (!entry || !entry.url || !entry.url.includes(`timeline/${todayIso}`)) continue;
      const visitTimeline = (obj) => {
        if (!obj || typeof obj !== 'object') return;
        if (Array.isArray(obj)) {
          obj.forEach(visitTimeline);
          return;
        }
        const rawTime = obj.checkTime ?? obj.logTime ?? obj.changeTime ?? obj.startTime ?? obj.attndTime;
        const rawLabel = obj.workStatusName ?? obj.statusName ?? obj.displayWorkType ?? obj.stateName;
        if (typeof rawTime === 'string' && typeof rawLabel === 'string') {
          const tm = rawTime.match(/([01]\d|2[0-3]):([0-5]\d)/);
          if (
            tm &&
            /^(출근|업무시작|업무|정상근무|근무|복귀|연장근무|외근|출장|휴게|외출|퇴근|업무종료)/.test(
              rawLabel.trim()
            )
          ) {
            addHistoryEvent(`${tm[1]}:${tm[2]}`, rawLabel.trim());
          }
        }
        for (const k of Object.keys(obj)) {
          if (typeof obj[k] === 'object') visitTimeline(obj[k]);
        }
      };
      try {
        visitTimeline(entry.data);
      } catch {}
      break;
    }

    historyEvents.sort((a, b) => a.time.localeCompare(b.time));

    const userBreakRangesSet = new Set();
    let activeBreakStart = null;
    let openBreakStart = null;
    let lastOutTime = null;

    for (const ev of historyEvents) {
      if (ev.kind === 'BREAK') {
        if (!openBreakStart) {
          openBreakStart = ev.time;
        }
      } else if (ev.kind === 'WORK') {
        if (openBreakStart) {
          if (ev.time > openBreakStart) {
            userBreakRangesSet.add(`${openBreakStart}~${ev.time}`);
          }
          openBreakStart = null;
        } else if (lastOutTime) {
          if (ev.time > lastOutTime) {
            userBreakRangesSet.add(`${lastOutTime}~${ev.time}`);
          }
          lastOutTime = null;
        }
      } else if (ev.kind === 'OUT') {
        if (openBreakStart) {
          if (ev.time > openBreakStart) {
            userBreakRangesSet.add(`${openBreakStart}~${ev.time}`);
          }
          openBreakStart = null;
        }
        lastOutTime = ev.time;
      }
    }

    if (openBreakStart) {
      if (state === 'OFF_WORK' && clockOut) {
        const outHm = clockOut.slice(0, 5);
        if (outHm > openBreakStart) {
          userBreakRangesSet.add(`${openBreakStart}~${outHm}`);
        }
        openBreakStart = null;
      } else if (state === 'BREAK') {
        activeBreakStart = openBreakStart;
      }
    }

    return {
      isLoginPage: false,
      url,
      state,
      clockIn,
      clockOut,
      clockOutExplicitlyUnset,
      duration,
      breakRanges: Array.from(breakRangesSet),
      userBreakRanges: Array.from(userBreakRangesSet),
      activeBreakStart,
      badgeText,
      apiStatus,
      canClockIn,
      canResumeWork,
      clockInBtnFound,
      clockInBtnDisabled,
      clockOutBtnFound,
      clockOutBtnDisabled
    };
  };

  const sendProbeToBackground = (probe) => {
    try {
      if (!probe) return;
      if (probe.state === 'LOADING' || probe.state === 'UNKNOWN') return;
      chrome.runtime.sendMessage({ type: 'WORKDOT_PROBE', payload: probe }).catch(() => {});
    } catch {}
  };

  const scheduleProbe = (delayMs = 400) => {
    if (probeTimer) clearTimeout(probeTimer);
    probeTimer = setTimeout(async () => {
      await ensureTodayGadgetApiData(false);
      const probe = runProbe();
      sendProbeToBackground(probe);
    }, delayMs);
  };

  const dismissBlockingPopups = () => {
    let count = 0;
    const closeBtnSelectors = [
      '.layer_notice .btn_layer_x',
      '.notice_popup .close',
      '.notice_popup .btn_layer_x',
      '.survey_popup .close',
      '.survey_popup .btn_layer_x',
      '[data-role="surveyClose"]',
      '.btn_popup_close'
    ];
    for (const sel of closeBtnSelectors) {
      document.querySelectorAll(sel).forEach((el) => {
        if (
          el.closest('.layer_appr, [data-app="approval"], [id*="approval"], .appr_line_wrap, .approval_modal, .org_tree')
        ) {
          return;
        }
        try {
          el.click();
          count++;
        } catch {}
      });
    }
    document.querySelectorAll('.layer_dimm, .dimm, .layer_backdrop, .modal-backdrop, [class*="dimm"]').forEach((dim) => {
      if (!document.querySelector('.layer_normal, .layer_doc, .layer_appr, .approval_modal, [data-app="approval"]')) {
        try {
          dim.remove();
          count++;
        } catch {}
      }
    });
    return count;
  };

  const triggerClockIn = () => {
    const gadgetBtns = Array.from(
      document.querySelectorAll('.lnb_gadget.gadget_attend .attend_btn button, .attend_card .attend_btn button, .attend_btn button')
    );
    for (const btn of gadgetBtns) {
      const t = clean(btn.innerText);
      if (t === '출근하기' || t.startsWith('출근하기') || t === '출근' || t === '업무시작') {
        if (btn.disabled || btn.classList.contains('disabled')) return 'DISABLED:' + t;
        btn.click();
        return 'OK:GADGET:' + t;
      }
    }

    const direct = document.querySelector('#workIn, #clockIn, [data-role="clockIn"], [data-action="clockIn"], .btn_work_in');
    if (direct) {
      if (direct.disabled || direct.classList.contains('disabled')) return 'DISABLED:DIRECT';
      direct.click();
      return 'OK:DIRECT';
    }

    const candidates = Array.from(document.querySelectorAll('button, a, [role="button"]'));
    for (const el of candidates) {
      if (el.offsetParent === null) continue;
      const t = clean(el.innerText);
      if (t === '출근하기' || t.startsWith('출근하기') || t === '업무시작') {
        if (el.disabled || el.classList.contains('disabled')) return 'DISABLED:' + t;
        el.click();
        return 'OK:' + t;
      }
    }
    return 'NOT_FOUND';
  };

  const triggerClockOut = () => {
    const gadgetBtns = Array.from(
      document.querySelectorAll('.lnb_gadget.gadget_attend .attend_btn button, .attend_card .attend_btn button, .attend_btn button')
    );
    for (const btn of gadgetBtns) {
      const t = clean(btn.innerText);
      if (t === '퇴근하기' || t.startsWith('퇴근하기') || t === '퇴근' || t === '업무종료') {
        if (btn.disabled || btn.classList.contains('disabled')) return 'DISABLED:' + t;
        btn.click();
        return 'OK:GADGET:' + t;
      }
    }

    const direct = document.querySelector('#workOut, #clockOut, [data-role="clockOut"], [data-action="clockOut"], .btn_work_out');
    if (direct) {
      direct.click();
      return 'OK:DIRECT';
    }

    const candidates = Array.from(document.querySelectorAll('button, a, [role="button"]'));
    for (const el of candidates) {
      if (el.offsetParent === null) continue;
      const t = clean(el.innerText);
      if (t === '퇴근하기' || t.startsWith('퇴근하기') || t === '업무종료') {
        if (el.disabled || el.classList.contains('disabled')) return 'DISABLED:' + t;
        el.click();
        return 'OK:' + t;
      }
    }
    return 'NOT_FOUND';
  };

  const triggerResumeWork = () => {
    const gadget = document.querySelector('.lnb_gadget.gadget_attend, .attend_card');
    const curTag = gadget ? gadget.querySelector('.gadget_header .basic_tag .txt, .card_header .basic_tag .txt') : null;
    if (curTag && (clean(curTag.innerText) === '업무' || clean(curTag.innerText) === '근무중')) {
      return 'ALREADY_WORKING';
    }

    const directBtns = Array.from(document.querySelectorAll('.attend_btn button, .lnb_gadget button, button'));
    for (const btn of directBtns) {
      const t = clean(btn.innerText);
      if (t === '업무복귀' || t === '업무 시작' || t === '근무재개' || t === '업무재개' || t === '복귀') {
        if (!btn.disabled) {
          btn.click();
          try {
            btn.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
          } catch {}
          return 'OK:DIRECT_RETURN:' + t;
        }
      }
    }

    const findWorkItem = () => {
      const items = Array.from(
        document.querySelectorAll('.wrap_lnb_btn .menu_item, .select_list .menu_item, .list_menu button, [aria-controls="settings-modal"]')
      );
      return items.find((el) => {
        const txtSpan = el.querySelector('.critical .txt, .txt');
        const spanText = clean(txtSpan ? txtSpan.innerText : '');
        if (spanText === '업무' || spanText.startsWith('업무')) return true;
        const fullText = clean(el.innerText);
        return fullText.includes('업무') || fullText.includes('정상근무') || fullText.includes('복귀');
      });
    };

    let workItem = findWorkItem();
    if (!workItem || workItem.offsetParent === null) {
      const changeBtn = document.querySelector('.wrap_lnb_btn button, .attend_btn .wrap_lnb_btn button');
      if (changeBtn) changeBtn.click();
      workItem = findWorkItem();
    }

    if (workItem) {
      if (workItem.disabled) return 'DISABLED:업무';
      workItem.click();
      try {
        workItem.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
      } catch {}
      return 'OK:WORK_CLICKED';
    }
    return 'NOT_FOUND:WORK_BTN';
  };

  const triggerBreakIn = () => {
    const gadget = document.querySelector('.lnb_gadget.gadget_attend, .attend_card');
    const curTag = gadget ? gadget.querySelector('.gadget_header .basic_tag .txt, .card_header .basic_tag .txt') : null;
    if (curTag && clean(curTag.innerText) === '휴게') {
      return 'ALREADY_BREAK';
    }

    const findBreakBtn = () =>
      Array.from(
        document.querySelectorAll('.wrap_lnb_btn .menu_item, .select_list .menu_item, .list_menu button, .attend_btn button')
      ).find((el) => {
        const t = clean(el.innerText);
        return t === '휴게' || t.startsWith('휴게');
      });

    let breakBtn = findBreakBtn();
    if (!breakBtn) {
      const triggerBtn = document.querySelector('.wrap_lnb_btn button, .attend_btn .wrap_lnb_btn button');
      if (triggerBtn) triggerBtn.click();
      breakBtn = findBreakBtn();
    }

    if (breakBtn) {
      if (breakBtn.disabled) return 'DISABLED:휴게';
      breakBtn.click();
      return 'OK:BREAK_CLICKED';
    }
    return 'NOT_FOUND:BREAK_BTN';
  };

  const triggerBreakOut = () => {
    const gadget = document.querySelector('.lnb_gadget.gadget_attend, .attend_card');
    const curTag = gadget ? gadget.querySelector('.gadget_header .basic_tag .txt, .card_header .basic_tag .txt') : null;
    if (curTag && (clean(curTag.innerText) === '업무' || clean(curTag.innerText) === '근무중')) {
      return 'ALREADY_WORKING';
    }

    const directBtns = Array.from(document.querySelectorAll('.attend_btn button, .lnb_gadget button, button'));
    for (const btn of directBtns) {
      const t = clean(btn.innerText);
      if (t === '업무복귀' || t === '업무 시작' || t === '근무재개' || t === '업무재개' || t === '복귀') {
        if (!btn.disabled) {
          btn.click();
          return 'OK:DIRECT_RETURN:' + t;
        }
      }
    }

    const changeBtn = document.querySelector('.wrap_lnb_btn button, .attend_btn .wrap_lnb_btn button');
    if (changeBtn) changeBtn.click();

    const items = Array.from(
      document.querySelectorAll('.wrap_lnb_btn .menu_item, .select_list .menu_item, .list_menu button, .attend_btn button')
    );
    const workItem = items.find((el) => {
      const t = clean(el.innerText);
      return t === '업무' || t.startsWith('업무') || t === '정상근무' || t.includes('복귀');
    });

    if (workItem) {
      if (workItem.disabled) return 'DISABLED:업무';
      workItem.click();
      return 'OK:WORK_CLICKED';
    }
    return 'NOT_FOUND:WORK_BTN';
  };

  const clickConfirmModal = () => {
    const dopDialog = document.querySelector('.dop_dialog.open, .dop_dialog, [role="dialog"], #settings-modal, .go_popup');
    if (dopDialog) {
      const radios = Array.from(dopDialog.querySelectorAll('.dop_radio input[type="radio"], input[type="radio"]'));
      if (radios.length > 0 && !radios.some((r) => r.checked)) {
        radios[0].click();
      }

      const majorBtn = dopDialog.querySelector(
        '.popup_btn_area button.solid.major, button.solid.major, .btn_major, .btn_major_s'
      );
      if (majorBtn && !majorBtn.disabled) {
        majorBtn.click();
        try {
          majorBtn.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
        } catch {}
        return 'CONFIRMED:DOP_DIALOG';
      }

      const btns = Array.from(dopDialog.querySelectorAll('.popup_btn_area button, button, a.btn'));
      for (const btn of btns) {
        const t = clean(btn.innerText);
        if (
          (t === '확인' || t === '예' || t === '변경' || t === '출근' || t === '퇴근' || t === '휴게' || t === '업무') &&
          !btn.disabled
        ) {
          btn.click();
          try {
            btn.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
          } catch {}
          return 'CONFIRMED:' + t;
        }
      }
    }

    const modalBtns = Array.from(
      document.querySelectorAll(
        '.go_popup button, .go_popup a.btn_major_s, .modal button, .layer_normal a.btn_major_s, button.btn_major, a.btn_major'
      )
    );
    for (const btn of modalBtns) {
      const t = clean(btn.innerText);
      if (t === '확인' || t === '예' || t === '변경' || t === '출근' || t === '퇴근' || t === '휴게' || t === '업무') {
        btn.click();
        return 'CONFIRMED';
      }
    }
    return 'NO_MODAL';
  };

  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

  const executeActionAsync = async (action) => {
    const initialProbe = runProbe();
    if (initialProbe.isLoginPage) {
      return {
        success: false,
        needsLogin: true,
        message: '크롬에서 다우오피스 로그인이 필요합니다.',
        probe: initialProbe
      };
    }

    dismissBlockingPopups();

    let result = 'UNKNOWN';
    if (action === 'CLOCK_IN') result = triggerClockIn();
    else if (action === 'CLOCK_OUT') result = triggerClockOut();
    else if (action === 'RESUME_WORK') result = triggerResumeWork();
    else if (action === 'BREAK_IN') result = triggerBreakIn();
    else if (action === 'BREAK_OUT') result = triggerBreakOut();
    else return { success: false, needsLogin: false, message: `알 수 없는 명령: ${action}` };

    if (result.startsWith('OK:')) {
      await sleep(600);
      const modalRes = clickConfirmModal();
      if (modalRes.startsWith('NEEDS_USER_INPUT')) {
        return {
          success: false,
          needsLogin: false,
          message: '다우오피스 추가 확인(근무지 선택 등)이 필요합니다.'
        };
      }
      if (action === 'RESUME_WORK') {
        await sleep(500);
        clickConfirmModal();
      }
      await sleep(1000);
      await ensureTodayGadgetApiData(true);
      const probe = runProbe();
      return { success: true, needsLogin: false, message: '처리 완료', probe };
    }

    if (result.startsWith('DISABLED:') || result.startsWith('ALREADY_')) {
      await ensureTodayGadgetApiData(true);
      const probe = runProbe();
      return { success: true, needsLogin: false, message: result, probe };
    }

    return {
      success: false,
      needsLogin: false,
      message: `다우오피스 페이지에서 버튼을 찾지 못했습니다 (${result}).`
    };
  };

  try {
    chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
      if (!msg || typeof msg !== 'object') return false;

      if (msg.type === 'WORKDOT_RUN_PROBE') {
        (async () => {
          await ensureTodayGadgetApiData(Boolean(msg.forceFetch));
          sendResponse(runProbe());
        })();
        return true;
      }

      if (msg.type === 'WORKDOT_EXEC_ACTION') {
        executeActionAsync(msg.action)
          .then((res) => sendResponse(res))
          .catch((err) =>
            sendResponse({ success: false, needsLogin: false, message: err?.message || '실행 오류' })
          );
        return true;
      }

      return false;
    });
  } catch {}

  window.addEventListener('DOMContentLoaded', () => {
    if (document.body) {
      const obs = new MutationObserver(() => scheduleProbe(500));
      obs.observe(document.body, { childList: true, subtree: true, characterData: true });
    }
    scheduleProbe(700);
  });
})();
