/* js/views/runs.js —— 运行记录：服务端每一次「执行」，以及此刻的队列。

   这一页原先只查批量上传的批次（operations/upload-batches），
   而计划到点执行、人工立即执行、预检后自动排队的上传走的都是 execution_runs——
   于是最常见的那三类运行在「运行记录」里一条都不出现，页面长期是空的。

   队列名单放在最上面：并发度 1 的计划带着十台服务器时，
   「什么时候轮到我」是这一页要回答的第一个问题，而数字回答不了它。 */
import { api } from '../api.js';
import { App, ACTIONS, LOADERS } from '../state.js';
import {
  $, esc, status, shortId, fmtDT, relTime, tableHtml, pagerHtml,
  emptyState, openModal, errToast
} from '../ui.js';
import { shell, loading } from '../app.js';
import {
  openRunModal, RUN_STATUS, TRIGGER_TEXT, statusPill, runKindText, runTitle
} from './run-detail.js';

/* 有东西在跑或在排队时 5 秒一刷；全静的时候放慢到 30 秒——
   没人守着一个空队列，但新的执行开始时要能自己冒出来。 */
const POLL_BUSY_MS = 5000;
const POLL_IDLE_MS = 30000;

const WAIT_TEXT = {
  running: '—',
  waiting_in_run: '等前一项跑完',
  waiting_for_slot: '等上传名额'
};

/* 这一项要客户端做什么。预检只是扫目录算哈希，不占上传名额——
   队列里一排「排队中」而全局上限明明没满时，差别就在这一列。 */
const ITEM_KIND = {
  precheck_task: '扫描检查', upload_candidate: '上传备份', upload_latest: '上传备份'
};

/* 队列里每一项的状态（批量上传批次详情沿用同一套说法） */
const QUEUE_TEXT = {
  pending: '排队中', running: '执行中', succeeded: '完成', failed: '失败',
  timeout: '等超时', skipped: '跳过', cancelled: '已取消'
};

export async function vRuns() {
  App.state.runs = App.state.runs || { page: 1, pageSize: 20, totalCount: 0 };
  $('#app').innerHTML = shell('runs', '运行记录', `
    <div class="toolbar"><span class="tip">服务端每一次执行都在这里：计划到点、人工立即执行、批量上传，以及名额满时自动排队的上传。</span></div>
    <div id="queuewrap"></div>
    <h3>历次执行</h3>
    <div id="vwrap">${loading()}</div>`);
  await LOADERS.runs();
}

LOADERS.runs = async function () {
  clearTimeout(App.timer);
  const st = App.state.runs;
  const wrap = $('#vwrap');
  if (!wrap) return;

  let busy = false;
  try {
    // 队列取不到不该让整页失败：它是补充信息，历史记录才是这一页的主体。
    const [page, queue] = await Promise.all([
      api(`/api/v1/admin/execution-runs?page=${st.page}&pageSize=${st.pageSize}`),
      api('/api/v1/admin/execution-runs/queue').catch(() => null)
    ]);
    st.totalCount = page.totalCount;

    const qwrap = $('#queuewrap');
    if (qwrap) qwrap.innerHTML = queueHtml(queue);

    wrap.innerHTML = tableHtml(RUN_COLUMNS, page.items || [], {
      empty: emptyState('first', {
        glyph: '◷', title: '还没有任何执行记录',
        sub: '备份计划到点执行、任务列表里多选后「立即备份」、客户端列表里发起批量上传，都会在这里留下一条'
      })
    }) + pagerHtml('runs', st);

    busy = !!(queue && (queue.items || []).length)
      || (page.items || []).some(r => r.status === 'pending' || r.status === 'running');
  } catch (e) {
    // 刷新失败不清空已有内容：上一次的内容仍然比一片空白有用。
    wrap.innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`;
  }

  App.timer = setTimeout(() => {
    if (location.hash === '#/runs') LOADERS.runs();
  }, busy ? POLL_BUSY_MS : POLL_IDLE_MS);
};

/* ── 队列 ── */

function queueHtml(q) {
  if (!q) return '';
  const items = q.items || [];
  if (!items.length) {
    // 队列空是常态，不该占一整块地方；但「上限是多少」要一直看得见，
    // 否则「点了备份没反应」时人无从知道自己是不是撞在闸上。
    return `<h3>此刻的队列</h3><div class="hint">当前没有在跑或排队的项。全局上传上限 ${esc(q.globalLimit)} 份，此刻在传 ${esc(q.activeUploads)} 份。</div>`;
  }

  const full = q.activeUploads >= q.globalLimit;
  // 两种「排队中」分开说：等名额和等前一项跑完不是一回事，
  // 合成一个数会写出「0 个正在传 / 2 个排队中 · 有空余名额」这种自相矛盾的话。
  const parts = [];
  if (q.waitingForUploadSlot) parts.push(`${esc(q.waitingForUploadSlot)} 个等名额`);
  if (q.waitingInRun) parts.push(`${esc(q.waitingInRun)} 个等前一项`);

  return `<h3>此刻的队列</h3>
  <div class="dashboard-statusbar${full ? ' has-work' : ''}" role="status">
    <span class="status-mark" aria-hidden="true">⧖</span>
    <strong class="status-title">${esc(q.runningItems)} 项在跑${parts.length ? ` / ${parts.join(' / ')}` : ''}</strong>
    <span class="status-meta">
      <span>上传 ${esc(q.activeUploads)} / ${esc(q.globalLimit)}</span>
      <span>${full ? '已达上限，等名额的会在有空位时自动开始' : '按各自执行的并发度依次放行'}</span>
    </span>
  </div>
  ${tableHtml(QUEUE_COLUMNS, items)}`;
}

const QUEUE_COLUMNS = [
  {
    // 顺序号是这张表存在的理由：它把「还要等几个」变成一个能读出来的数字。
    l: '次序', num: true,
    render: e => `${esc(e.sortOrder + 1)}<span class="sub">${e.runMaxConcurrent === 1 ? '顺序' : `${esc(e.runMaxConcurrent)} 并行`}</span>`
  },
  {
    l: '任务 / 客户端',
    render: e => `<b>${esc(e.taskName)}</b><span class="sub">${esc(e.clientName)}</span>`
  },
  { l: '这一项', render: e => esc(ITEM_KIND[e.commandType] || e.commandType) },
  {
    l: '状态',
    render: e => `<span class="status status--${e.status === 'running' ? 'busy' : 'wait'}">${esc(QUEUE_TEXT[e.status] || e.status)}</span>`
  },
  {
    l: '在等',
    render: e => (e.wait === 'running'
      ? '<span class="sub">—</span>'
      : esc(WAIT_TEXT[e.wait] || e.wait))
  },
  {
    l: '属于哪次执行',
    render: e => `<button class="small" data-ui-action="act" data-view="runs" data-action="detail" data-id="${esc(e.runId)}">${esc(e.runName || runKindText({ kind: e.runKind }))}</button>`
  },
  {
    l: '开始于',
    render: e => (e.startedAt ? relTime(e.startedAt) : `<span class="sub">排入于 ${relTime(e.runCreatedAt)}</span>`)
  }
];

/* ── 历次执行 ── */

const RUN_COLUMNS = [
  {
    l: '时间',
    render: r => `${esc(fmtDT(r.startedAt || r.createdAt))}<span class="sub">${relTime(r.startedAt || r.createdAt)}</span>`
  },
  {
    l: '这次执行',
    render: r => `<b>${esc(runTitle(r))}</b><span class="sub">${esc(runKindText(r))}${r.planName && r.planName !== r.name ? ' · ' + esc(r.planName) : ''}</span>`
  },
  { l: '触发', render: r => esc(TRIGGER_TEXT[r.triggerSource] || r.triggerSource || '—') },
  { l: '状态', render: r => statusPill(RUN_STATUS, r.status) },
  {
    l: '进度', num: true,
    render: r => `${esc(r.succeededItems)} 成功 / ${esc(r.failedItems)} 失败 / 共 ${esc(r.totalItems)}`
  },
  { l: '并发', num: true, render: r => r.maxConcurrent === 1 ? '顺序' : `${esc(r.maxConcurrent)} 并行` },
  {
    l: '操作',
    render: r => {
      const b = [`<button class="small" data-ui-action="act" data-view="runs" data-action="detail" data-id="${esc(r.id)}">详情</button>`];
      // 批量上传另有一份按候选备份集展开的详情（指令 / 上传会话各自的状态），
      // 那是执行详情给不出的粒度，保留入口。
      if (r.uploadBatchId)
        b.push(`<button class="small" data-ui-action="act" data-view="runs" data-action="batch" data-id="${esc(r.uploadBatchId)}">批次详情</button>`);
      return b.join(' ');
    }
  }
];

ACTIONS['runs:detail'] = runId => openRunModal(runId, { onChange: () => LOADERS.runs() });

ACTIONS['runs:batch'] = async id => {
  try {
    const d = await api(`/api/v1/admin/operations/upload-batches/${id}`);
    openModal(`批次详情：${d.name || shortId(d.id)}`, `<div class="kv">
      <div class="row"><div class="k">状态</div><div class="v">${status('batch_status', d.status)}</div></div>
      <div class="row"><div class="k">进度</div><div class="v">${esc(d.succeededItems)} 成功 / ${esc(d.failedItems)} 失败 / 共 ${esc(d.totalItems)}</div></div>
      <div class="row"><div class="k">同时几台</div><div class="v">${esc(d.maxConcurrentClients)} 台</div></div>
    </div>${tableHtml([
      { l: '这次备份', render: r => `<span class="mono">${shortId(r.candidateBackupSetId)}</span>` },
      { l: '识别标识', render: r => `<span class="mono">${esc(r.candidateKey)}</span>` },
      { l: '客户端', k: 'clientHostname' },
      // 排队中的项此前在这张表里根本不存在（没有指令就查不到），并发闸的效果也就无从观察
      { l: '排队', render: r => esc(QUEUE_TEXT[r.queueStatus] || r.queueStatus || '—') },
      { l: '检查状态', render: r => (r.commandStatus ? status('command_status', r.commandStatus) : '—') },
      { l: '上传状态', render: r => (r.uploadSessionStatus ? status('upload_status', r.uploadSessionStatus) : '—') },
      { l: '说明', render: r => esc(r.message || '') }
    ], d.items, { empty: '<div class="empty">批次为空</div>' })}`, { wide: true });
  } catch (e) { errToast(e); }
};
