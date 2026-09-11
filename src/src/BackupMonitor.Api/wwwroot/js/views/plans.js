/* js/views/plans.js —— 备份计划（多任务编组，按顺序执行）

   计划解决的是客户端调度做不到的那件事：跨机器的顺序执行。
   Agent 只知道自己那台机器的 cron，A 机器无从知道 B 机器传完没有，
   所以「先备财务库、再备 ERP」必须由服务端排队。 */
import { api } from '../api.js';
import { App, ACTIONS, LOADERS } from '../state.js';
import {
  $, esc, fmtDT, relTime, tableHtml, emptyState, toast, errToast,
  confirmModal, formModal, openModal
} from '../ui.js';
import { shell, loading } from '../app.js';
// 执行详情与运行记录页共用同一份实现：同一次执行在两个页面上长得不一样，
// 人没有办法判断哪一边是真的。
import { openRunModal, RUN_STATUS, statusPill } from './run-detail.js';

const WEEK = ['一', '二', '三', '四', '五', '六', '日'];

function scheduleText(p) {
  const when = p.scheduleKind === 'weekly'
    ? `每周${(p.daysOfWeek || []).map(d => WEEK[d - 1]).join('、') || '—'}`
    : '每天';
  return `${when} ${esc(p.runAt)}`;
}

export async function vPlans() {
  $('#app').innerHTML = shell('plans', '备份计划', `
    <div class="toolbar">
      <div class="hint">把多个任务编成一组，到点由服务端按顺序驱动：前一个跑完（成功 / 失败 / 超时）才放下一个。</div>
      <div class="spacer"></div>
      <button class="primary" data-ui-action="act" data-view="plans" data-action="create">＋ 新建计划</button>
    </div>
    <div id="vwrap">${loading()}</div>`);
  await LOADERS.plans();
}

LOADERS.plans = async function () {
  const wrap = $('#vwrap'); if (!wrap) return;
  try {
    const list = await api('/api/v1/admin/backup-plans');
    App.state.planItems = list;
    wrap.innerHTML = tableHtml([
      { l: '计划名', render: p => `<b>${esc(p.name)}</b>${p.enabled ? '' : ' <span class="status status--mut pill">已停用</span>'}` },
      { l: '执行时间', render: p => esc(scheduleText(p)) },
      { l: '任务数', num: true, render: p => (p.items || []).length },
      { l: '并发', num: true, render: p => p.maxConcurrent === 1 ? '顺序' : `${esc(p.maxConcurrent)} 并行` },
      { l: '下次执行', render: p => p.enabled ? fmtDT(p.nextRunAt) : '—' },
      { l: '上次执行', render: p => p.lastRunAt ? relTime(p.lastRunAt) : '—' },
      {
        l: '当前状态',
        render: p => p.activeRun
          ? `${statusPill(RUN_STATUS, p.activeRun.status)} <button class="small" data-ui-action="act" data-view="plans" data-action="run" data-id="${esc(p.activeRun.id)}">查看</button>`
          : '<span class="status status--mut pill">空闲</span>'
      },
      {
        l: '操作',
        // 正在跑的时候「立即执行」必须是灰的。服务端本来就会 409 挡下来
        // （同一个计划同时只能有一次执行），但那条路径的表现是：确认框问一遍
        // 「现在就执行一次？」，人点了是，再弹一个红字说「要么正在执行、要么一个任务都没有」——
        // 两种原因搅在一句话里，看完不知道自己到底做成了什么。
        render: p => `<button class="small" data-ui-action="act" data-view="plans" data-action="trigger" data-id="${esc(p.id)}"
            ${p.activeRun ? 'disabled title="这个计划上一次执行还没跑完。同一个计划同时只跑一次——要重来先在执行详情里取消它。"' : ''}>立即执行</button>
          <button class="small" data-ui-action="act" data-view="plans" data-action="history" data-id="${esc(p.id)}">历史</button>
          <button class="small" data-ui-action="act" data-view="plans" data-action="edit" data-id="${esc(p.id)}">编辑</button>
          <button class="small danger" data-ui-action="act" data-view="plans" data-action="del" data-id="${esc(p.id)}">删除</button>`
      }
    ], list, {
      empty: emptyState('first', {
        glyph: '≣', title: '还没有备份计划',
        sub: '需要「先备这个、跑完再备那个」时才用得上；单个任务自己的扫描计划就够了'
      })
    });
  } catch (e) { wrap.innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`; }
};

/* ── 表单 ── */

// 任务选择器：勾选 + 上下调序。计划的全部意义在于顺序，所以顺序必须能看见、能改。
function taskPickerHtml() {
  return `<div class="planpick">
    <div class="planpick-head"><b>已选任务（按执行顺序）</b></div>
    <ol id="planChosen" class="planpick-chosen"></ol>
    <div class="planpick-head"><b>可选任务</b><span class="hint">已经属于别的计划的任务不能再选</span></div>
    <div id="planPool" class="planpick-pool">${loading()}</div>
  </div>`;
}

function renderChosen(ov, state) {
  const box = ov.querySelector('#planChosen');
  if (!state.chosen.length) {
    box.innerHTML = '<li class="hint">还没有选任务。计划里一个任务都没有的话，到点不会执行任何东西。</li>';
    return;
  }
  box.innerHTML = state.chosen.map((id, i) => {
    const t = state.byId[id];
    return `<li>
      <span class="planpick-name">${esc(t ? t.name : id)}<small>${esc(t ? t.clientName : '')}</small></span>
      <span class="planpick-ops">
        <button class="small" data-move="${i}" data-dir="-1" ${i === 0 ? 'disabled' : ''}>↑</button>
        <button class="small" data-move="${i}" data-dir="1" ${i === state.chosen.length - 1 ? 'disabled' : ''}>↓</button>
        <button class="small danger" data-drop="${esc(id)}">移除</button>
      </span></li>`;
  }).join('');
}

function renderPool(ov, state) {
  const box = ov.querySelector('#planPool');
  const rows = state.pool.map(t => {
    const taken = t.planName && t.planName !== state.selfPlanName;
    const checked = state.chosen.includes(t.id);
    return `<label class="planpick-row${taken ? ' is-disabled' : ''}">
      <input type="checkbox" data-pick="${esc(t.id)}" ${checked ? 'checked' : ''} ${taken ? 'disabled' : ''}>
      <span class="planpick-name">${esc(t.name)}<small>${esc(t.clientName)} · ${esc(t.applicationName)}</small></span>
      ${taken ? `<span class="status status--mut pill">已在「${esc(t.planName)}」里</span>` : ''}
      ${t.enabled ? '' : '<span class="status status--mut pill">任务已停用</span>'}
    </label>`;
  }).join('');
  box.innerHTML = rows || '<div class="hint">没有可选的备份任务。</div>';
}

async function loadPickerData(selfPlanId) {
  const [tasks, plans] = await Promise.all([
    api('/api/v1/admin/backup-tasks?page=1&pageSize=500'),
    api('/api/v1/admin/backup-plans')
  ]);

  const planByTask = {};
  for (const p of plans || []) {
    for (const it of p.items || []) planByTask[it.taskId] = p.name;
  }

  const items = (tasks.items || tasks || []).map(t => ({
    id: t.id,
    name: t.name,
    applicationName: t.applicationName || '',
    clientName: t.clientDisplayName || t.clientHostname || '',
    enabled: t.enabled !== false,
    planName: planByTask[t.id] || null
  }));

  return { items, selfPlanName: (plans || []).find(p => p.id === selfPlanId)?.name || null };
}

function planFields(v) {
  return [
    { name: 'name', label: '计划名称', type: 'text', value: v.name, required: true, placeholder: '如 夜间全量备份' },
    { name: 'scheduleKind', label: '执行频率', type: 'select', value: v.scheduleKind || 'daily',
      options: [{ v: 'daily', t: '每天' }, { v: 'weekly', t: '每周指定几天' }] },
    { name: 'runAt', label: '开始时间', type: 'time', value: v.runAt || '02:00', required: true,
      hint: '到点后按下面的顺序依次驱动；这是计划自己的时区里的时刻' },
    { name: 'daysPick', type: 'static', label: '每周哪几天',
      html: `<div class="weekpick">${WEEK.map((w, i) =>
        `<label><input type="checkbox" data-day="${i + 1}" ${(v.daysOfWeek || []).includes(i + 1) ? 'checked' : ''}>周${w}</label>`).join('')}</div>`,
      hint: '只有「每周指定几天」时才生效' },
    { name: 'maxConcurrent', label: '同时执行几个', type: 'number', value: v.maxConcurrent ?? 1,
      hint: '1 = 严格一个接一个（默认）。填 N 表示最多 N 个同时跑，用来在带宽够的时候压缩总时长' },
    { name: 'tasksPick', type: 'static', label: '编组任务', html: taskPickerHtml(),
      hint: '进了计划的任务，它自己的扫描计划就不再下发给客户端了——否则一天会跑两次' },
    { name: 'itemTimeoutMinutes', label: '单项超时（分钟）', type: 'number', value: v.itemTimeoutMinutes ?? 240, advanced: true,
      hint: '一项等这么久还没结果就判它失败、继续下一项。否则一台关机的客户端能让整晚的计划全部不执行' },
    { name: 'timezone', label: '时区', type: 'text', value: v.timezone || 'Asia/Shanghai', advanced: true },
    { name: 'enabled', label: '状态', labelText: '启用该计划', type: 'checkbox', value: v.enabled !== false, advanced: true },
    { name: 'notifyOnFinish', label: '完成通知', labelText: '每次跑完发一条汇总回执', type: 'checkbox', value: v.notifyOnFinish === true, advanced: true,
      hint: '一次计划一条，写明共几项、成功几项、失败几项，并列出失败的项。全部成功也会发——'
        + '收到它才知道计划确实跑了。只对到点触发的执行发，手动点「立即执行」不发。'
        + '打开后，这个计划里的任务自己那条「备份成功通知」会被压掉，以计划为准。' }
  ];
}

function openPlanForm(title, initial, okText, submit) {
  const v = initial || {};
  const state = {
    chosen: (v.items || []).map(i => i.taskId),
    byId: {},
    pool: [],
    selfPlanName: v.name || null
  };

  const collect = vals => ({
    name: vals.name,
    enabled: vals.enabled,
    notifyOnFinish: vals.notifyOnFinish === true,
    scheduleKind: vals.scheduleKind,
    runAt: vals.runAt,
    daysOfWeek: state.days,
    timezone: vals.timezone,
    maxConcurrent: Number(vals.maxConcurrent) || 1,
    itemTimeoutMinutes: Number(vals.itemTimeoutMinutes) || 240,
    taskIds: state.chosen
  });

  formModal(title, planFields(v), async vals => {
    await submit(collect(vals));
  }, okText, {
    wide: true,
    validate: vals => {
      state.days = [...document.querySelectorAll('[data-day]:checked')].map(el => Number(el.dataset.day));
      if (vals.scheduleKind === 'weekly' && state.days.length === 0) return '「每周指定几天」至少要选一天';
      if (!state.chosen.length) return '计划里至少要有一个任务';
      return '';
    },
    onMount: async ov => {
      // 频率不是每周时，把星期选择收起来——留着只会让人以为它生效
      const syncWeek = () => {
        const weekly = ov.querySelector('[name="scheduleKind"]').value === 'weekly';
        ov.querySelector('[data-static="daysPick"]').style.display = weekly ? '' : 'none';
      };
      ov.querySelector('[name="scheduleKind"]').addEventListener('change', syncWeek);
      syncWeek();

      try {
        const { items, selfPlanName } = await loadPickerData(v.id);
        state.pool = items;
        state.selfPlanName = selfPlanName || state.selfPlanName;
        for (const t of items) state.byId[t.id] = t;
        renderPool(ov, state);
        renderChosen(ov, state);
      } catch (e) {
        ov.querySelector('#planPool').innerHTML = `<div class="hint">任务列表加载失败：${esc(e.message)}</div>`;
      }

      ov.addEventListener('change', ev => {
        const pick = ev.target.closest('[data-pick]');
        if (!pick) return;
        const id = pick.dataset.pick;
        if (pick.checked) { if (!state.chosen.includes(id)) state.chosen.push(id); }
        else state.chosen = state.chosen.filter(x => x !== id);
        renderChosen(ov, state);
      });

      ov.addEventListener('click', ev => {
        const move = ev.target.closest('[data-move]');
        if (move) {
          const i = Number(move.dataset.move), d = Number(move.dataset.dir);
          const j = i + d;
          if (j < 0 || j >= state.chosen.length) return;
          [state.chosen[i], state.chosen[j]] = [state.chosen[j], state.chosen[i]];
          renderChosen(ov, state);
          return;
        }
        const drop = ev.target.closest('[data-drop]');
        if (drop) {
          state.chosen = state.chosen.filter(x => x !== drop.dataset.drop);
          const box = ov.querySelector(`[data-pick="${drop.dataset.drop}"]`);
          if (box) box.checked = false;
          renderChosen(ov, state);
        }
      });
    }
  });
}

ACTIONS['plans:create'] = async () => {
  openPlanForm('新建备份计划', null, '创建', async body => {
    await api('/api/v1/admin/backup-plans', { method: 'POST', body });
    toast('计划已创建', 'ok'); LOADERS.plans();
  });
};

ACTIONS['plans:edit'] = async id => {
  const plan = await api(`/api/v1/admin/backup-plans/${id}`);
  openPlanForm(`编辑计划：${plan.name}`, plan, '保存', async body => {
    await api(`/api/v1/admin/backup-plans/${id}`, { method: 'PUT', body });
    toast('计划已保存', 'ok'); LOADERS.plans();
  });
};

ACTIONS['plans:del'] = async id => {
  if (!await confirmModal('删除这个计划？里面的任务不会被删除，它们会回到各自的扫描计划。')) return;
  try {
    await api(`/api/v1/admin/backup-plans/${id}`, { method: 'DELETE' });
    toast('计划已删除', 'ok'); LOADERS.plans();
  } catch (e) { errToast(e); }
};

ACTIONS['plans:trigger'] = async id => {
  if (!await confirmModal('现在就执行一次这个计划？不影响下一次到点执行。')) return;
  try {
    const run = await api(`/api/v1/admin/backup-plans/${id}/trigger`, { method: 'POST' });
    toast('已开始执行', 'ok');
    LOADERS.plans();
    ACTIONS['plans:run'](run.id);
  } catch (e) { errToast(e); }
};

/* 执行详情走共用实现；取消成功后刷新计划列表，让「当前状态」那一列跟上。 */
ACTIONS['plans:run'] = runId => openRunModal(runId, { onChange: () => LOADERS.plans() });

ACTIONS['plans:history'] = async planId => {
  try {
    const page = await api(`/api/v1/admin/backup-plans/${planId}/runs?page=1&pageSize=30`);
    openModal('历次执行', tableHtml([
      { l: '开始时间', render: r => fmtDT(r.startedAt || r.createdAt) },
      { l: '触发', render: r => r.triggerSource === 'manual' ? '人工' : '到点' },
      { l: '状态', render: r => statusPill(RUN_STATUS, r.status) },
      { l: '成功 / 失败 / 共', render: r => `${esc(r.succeededItems)} / ${esc(r.failedItems)} / ${esc(r.totalItems)}` },
      { l: '', render: r => `<button class="small" data-ui-action="act" data-view="plans" data-action="run" data-id="${esc(r.id)}">详情</button>` }
    ], page.items || [], { empty: emptyState('first', { glyph: '◷', title: '还没有执行过' }) }), { wide: true });
  } catch (e) { errToast(e); }
};
