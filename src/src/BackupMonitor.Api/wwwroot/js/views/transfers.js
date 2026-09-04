/* js/views/transfers.js —— 正在传输的备份。
   备份工具最基本的一块表盘：一份几十 GB 的备份传了两小时，
   人要能看出来它还在动、大概什么时候完、还是已经卡死了。 */
import { api } from '../api.js';
import { App, ACTIONS, LOADERS } from '../state.js';
import {
  $, esc, status, fmtBytes, fmtRate, fmtDuration, relTime,
  tableHtml, emptyState, xferBar, toast, errToast, confirmModal
} from '../ui.js';
import { shell, loading } from '../app.js';

/* 有东西在传时 5 秒一刷：慢传输里数字动不动，本身就是最重要的那条信息。
   一条都没有时放慢到 15 秒——没人守着一个空页面，但新的传输开始时要能自己冒出来。 */
const POLL_ACTIVE_MS = 5000;
const POLL_IDLE_MS = 15000;

export async function vTransfers() {
  $('#app').innerHTML = shell('transfers', '传输中',
    `<div class="toolbar"><span class="tip">只显示正在传的备份；传完存好之后到「备份集」页面看。</span></div>`
    + `<div id="vwrap">${loading()}</div>`
    + finishedShellHtml());
  // 「最近结束」刻意放在 #vwrap 外面：在传的那张表 5 秒重画一次 innerHTML，
  // 放进去的话人刚展开就会被下一轮刷新收起来。
  wireFinished();
  await LOADERS.transfers();
}

/* ---------- 最近结束的传输 ----------

   默认折叠。常态下人要看的是在传的那几条，这一块占地方没有意义。
   但有两类信息只有这里答得上来：

     一、没传成的那些（failed / cancelled / expired）。它们不入备份集（没归档），
         也不在上面那张表（已终结），此前只能翻日志或查库；
     二、耗时、平均速度、重试次数。判断「链路够不够快」「限速要不要调」靠的是它。

   已入库的也列进来，但只作为时间线上的一行，正式档案在「备份集」页面——
   同一批数据不做第二个入口，这里给个编号让人点过去就够了。 */

const FINISHED_PAGE_SIZE = 50;
const finishedState = { page: 0, rows: [], total: 0, loading: false };

function finishedShellHtml() {
  return `<details id="vfinished" class="fold-section">
    <summary>最近结束的传输<span class="sub">传成功的去了「备份集」，失败、取消、过期的只有这里看得到</span></summary>
    <div id="vfinished-body"></div>
  </details>`;
}

function wireFinished() {
  const box = $('#vfinished');
  if (!box) return;
  box.addEventListener('toggle', () => {
    // 只在第一次展开时拉，之后靠「加载更多」翻页；收起再展开不重拉，
    // 免得人只是想收起来腾地方却触发一次请求。
    if (box.open && finishedState.page === 0 && !finishedState.loading) loadFinished();
  });
}

async function loadFinished() {
  const body = $('#vfinished-body');
  if (!body || finishedState.loading) return;
  finishedState.loading = true;
  if (finishedState.page === 0) body.innerHTML = loading();

  try {
    const next = finishedState.page + 1;
    const data = await api(`/api/v1/admin/upload-sessions/finished?page=${next}&pageSize=${FINISHED_PAGE_SIZE}`);
    finishedState.page = next;
    finishedState.total = data.totalCount || 0;
    finishedState.rows = finishedState.rows.concat(data.items || []);
    renderFinished();
  } catch (e) {
    // 这一块是补充信息，失败不该影响上面那张表；就地把原因说出来即可。
    body.innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`;
  } finally {
    finishedState.loading = false;
  }
}

function renderFinished() {
  const body = $('#vfinished-body');
  if (!body) return;

  const more = finishedState.rows.length < finishedState.total
    ? `<div class="toolbar"><button class="small" data-ui-action="act" data-view="transfers" data-action="more">加载更多</button>`
      + `<span class="tip">已显示 ${finishedState.rows.length} / ${finishedState.total} 条</span></div>`
    : `<div class="toolbar"><span class="tip">共 ${finishedState.total} 条，已全部显示</span></div>`;

  // 折叠标题上只放得下一句话，而这一块「为什么存在、该怎么用」需要三句才说得清；
  // 展开之后再把话补全，不占常态下的版面。
  const intro = `<div class="toolbar"><span class="tip">`
    + `按结束时间倒序，成功和没成功的都在里面。`
    + `传成功的正式档案在「备份集」页面，这里只留一行时间线，点「归档」列的编号就能过去；`
    + `失败、取消、过期的不入备份集，也不会留在上面那张表里，只有这里看得到，`
    + `断在哪儿看「传输量」，为什么断看「原因」。`
    + `另外耗时、平均速度、重试次数只有传输会话上才有——判断链路够不够快、限速要不要调，看的就是这几列。`
    + `</span></div>`;

  body.innerHTML = intro + tableHtml(FINISHED_COLUMNS, finishedState.rows, {
    empty: emptyState('ok', { glyph: '✓', title: '还没有结束过的传输' })
  }) + (finishedState.total ? more : '');
}

const FINISHED_COLUMNS = [
  {
    l: '客户端',
    render: r => `<a href="#/clients/${esc(r.clientId)}">${esc(r.clientDisplayName)}</a>`
  },
  {
    l: '任务 / 业务单元',
    render: r => `${esc(r.taskName)}${r.businessUnitName ? ` · <b>${esc(r.businessUnitName)}</b>` : ''}`
  },
  { l: '状态', render: r => status('upload_status', r.status) },
  {
    // 失败的那些靠「传了多少 / 一共多少」看出是传到哪儿断的；
    // 只写总量的话，断在开头和断在 99% 长得一模一样。
    l: '传输量',
    num: true,
    render: r => (r.uploadedBytes === r.totalBytes
      ? esc(fmtBytes(r.totalBytes))
      : `${esc(fmtBytes(r.uploadedBytes))} / ${esc(fmtBytes(r.totalBytes))}`)
  },
  { l: '耗时', num: true, render: r => (r.durationSeconds == null ? '<span class="sub">—</span>' : esc(fmtDuration(r.durationSeconds))) },
  { l: '平均速度', num: true, render: r => fmtRate(r.averageBytesPerSecond) },
  {
    l: '重试',
    num: true,
    render: r => (r.retryCount ? `<span class="cell-warn">${esc(r.retryCount)}</span>` : '<span class="sub">0</span>')
  },
  { l: '结束于', render: r => relTime(r.completedAt) },
  {
    // committed 却没有编号，说明那份备份已经被删了。这一格空着本身就是信息，
    // 而不是「数据没取到」——所以要把话写出来，不能留一个横杠。
    l: '归档',
    render: r => {
      if (r.backupSetCode) return `<a href="#/backups">${esc(r.backupSetCode)}</a>`;
      if (r.status === 'committed') return '<span class="cell-warn">备份已删除</span>';
      return `<span class="sub">${esc(r.errorCode || '未入库')}</span>`;
    }
  },
  {
    l: '原因',
    render: r => (r.errorMessage ? `<span class="sub">${esc(r.errorMessage)}</span>` : '')
  }
];

ACTIONS['transfers:more'] = () => loadFinished();

LOADERS.transfers = async function () {
  clearTimeout(App.timer);
  const wrap = $('#vwrap');
  if (!wrap) return;
  try {
    // api() 在少数分支会返回 null，兜一层空数组，免得整页栽在 rows.length 上。
    const [rows, queue] = await Promise.all([
      api('/api/v1/admin/upload-sessions/active').then(r => r || []),
      // 排队状态取不到不该让整页失败：它是补充信息，正在传的那几条才是主角。
      api('/api/v1/admin/upload-sessions/queue-status').catch(() => null)
    ]);
    wrap.innerHTML = queueHtml(queue) + summaryHtml(rows) + tableHtml(COLUMNS, rows, {
      empty: emptyState('ok', {
        glyph: '⇅', title: '当前没有正在传输的备份',
        // 刚点过「立即备份」的人多半是被那句「进度在传输中页面」指过来的，
        // 而扫描阶段这里本来就是空的——不说清楚，空页面唯一能得出的结论是「没发起成功」。
        // 备份计划到点跑起来时人第一个来的就是这里，而计划的第一步是预检
        // （客户端把备份文件整读一遍算校验和），那个阶段一个上传会话都还没有。
        // 只说「立即备份」的话，从计划过来的人得到的结论只能是「计划没跑起来」。
        sub: '客户端开始上传后，这里会自动出现并每 5 秒刷新一次。'
          + '备份计划刚到点、或刚点过「立即备份」的话，客户端要先把备份文件整读一遍算校验和，'
          + '几十 GB 会花上一段时间，这个阶段这里还是空的——'
          + '想知道它扫到哪一步，到「运行记录」里打开那次执行看每一项的「说明」'
      })
    });
    // 有东西排队时也按快节奏刷：人盯着的正是「什么时候轮到我」。
    const busy = rows.length || (queue && queue.queuedItems);
    App.timer = setTimeout(() => {
      if (location.hash === '#/transfers') LOADERS.transfers();
    }, busy ? POLL_ACTIVE_MS : POLL_IDLE_MS);
  } catch (e) {
    // 刷新失败不清空已有内容也不再排下一次：一直重试会把错误刷成滚动条，
    // 而上一次的数字仍然比空白有用。
    wrap.innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`;
  }
};

/* 全局上传闸的状态。限流如果看不见，它的表现就是「点了备份没反应」——
   人会以为系统坏了，然后去点更多次。排队数为 0 且没到上限时不显示，
   常态下这一行不该占地方。 */
function queueHtml(q) {
  if (!q || (!q.queuedItems && q.activeUploads < q.globalLimit)) return '';
  const full = q.activeUploads >= q.globalLimit;
  // 两种「排队中」分开说。合成一个数的那一版会写出
  // 「0 个正在传 / 2 个排队中 · 有空余名额」——这句话自相矛盾，
  // 读起来就是系统卡住了，而实际上那两项只是在等本次执行的并发度。
  const parts = [];
  if (q.waitingForUploadSlot) parts.push(`${esc(q.waitingForUploadSlot)} 个等名额`);
  if (q.waitingInRun) parts.push(`${esc(q.waitingInRun)} 个等前一项跑完`);
  const queued = parts.length ? parts.join(' / ') : `${esc(q.queuedItems)} 个排队中`;

  // 「有空余名额」只在确实有人在等名额时才值得说；
  // 剩下的都在等前一项时，该说的是「这是顺序执行的正常表现」。
  let hint;
  if (full) hint = '已达上限，等名额的会在有空位时自动开始';
  else if (q.waitingInRun) hint = '按执行的并发度依次放行，不是卡住';
  else hint = '有空余名额';

  return `<div class="dashboard-statusbar${full ? ' has-work' : ''}" role="status">
    <span class="status-mark" aria-hidden="true">⧖</span>
    <strong class="status-title">${esc(q.activeUploads)} 个正在传 / ${queued}</strong>
    <span class="status-meta">
      <span>全局上限 ${esc(q.globalLimit)}</span>
      <span>${hint}</span>
    </span>
  </div>`;
}

function summaryHtml(rows) {
  if (!rows.length) return '';
  const total = rows.reduce((n, r) => n + Number(r.totalBytes || 0), 0);
  const done = rows.reduce((n, r) => n + Number(r.uploadedBytes || 0), 0);
  // 速度合计只加算得出来的那几条。把 null 当 0 加进去会让总速度偏低，
  // 而这个数是用来判断"整条链路够不够快"的。
  const rated = rows.filter(r => r.bytesPerSecond != null);
  const rate = rated.reduce((n, r) => n + Number(r.bytesPerSecond), 0);
  const stalled = rows.filter(r => r.stalled).length;
  const clients = new Set(rows.map(r => r.clientId)).size;
  return `<div class="dashboard-statusbar${stalled ? ' has-work' : ''}" role="status">
    <span class="status-mark" aria-hidden="true">${stalled ? '!' : '⇅'}</span>
    <strong class="status-title">${stalled ? `${stalled} 条疑似卡住` : `${rows.length} 条正在传输`}</strong>
    <span class="status-meta">
      <span>${esc(clients)} 台客户端</span>
      <span>${fmtBytes(done)} / ${fmtBytes(total)}</span>
      <span>合计 ${rated.length ? fmtRate(rate) : '速度计算中'}</span>
    </span>
  </div>`;
}

const COLUMNS = [
  {
    l: '客户端',
    render: r => `<a href="#/clients/${esc(r.clientId)}"><b>${esc(r.clientDisplayName || r.hostname)}</b></a><span class="sub mono">${esc(r.hostname)}</span>`
  },
  {
    // 账套要跟任务名并排显示，而不是塞进副行：U8 一台机器 18 个账套时，
    // 这一列是这 18 行之间**唯一**的区别——只写任务名的话，
    // 「现在备到哪个账套了」在这张表上根本读不出来。
    l: '任务 / 业务单元',
    render: r => `${esc(r.taskName)}${r.businessUnitName ? ` · <b>${esc(r.businessUnitName)}</b>` : ''}`
      + `<span class="sub">${esc(r.totalFiles)} 个文件</span>`
  },
  {
    l: '进度',
    render: r => `${xferBar(r.percent, r.stalled)}<span class="sub">${Number(r.percent).toFixed(1)}% · ${fmtBytes(r.uploadedBytes)} / ${fmtBytes(r.totalBytes)}</span>`
  },
  { l: '速度', num: true, render: r => fmtRate(r.bytesPerSecond) },
  {
    l: '预计剩余',
    num: true,
    render: r => (r.etaSeconds == null
      ? '<span class="sub">—</span>'
      : esc(fmtDuration(r.etaSeconds)))
  },
  {
    // 「慢」和「死」是两回事，而这一列是唯一能把两者分开的依据：
    // 速度低但静默时间在正常范围 = 还在传；静默时间一路涨 = 已经不动了。
    l: '静默',
    num: true,
    render: r => (r.stalled
      ? `<span class="cell-warn">${esc(fmtDuration(r.idleSeconds))}</span>`
      : esc(fmtDuration(r.idleSeconds)))
  },
  { l: '状态', render: r => status('upload_status', r.status) },
  { l: '开始于', render: r => relTime(r.startedAt) },
  {
    // 这张表此前是纯只读的：白天发现某台机器正在把带宽占满时，唯一能做的是等它传完。
    // 暂停会真的停住写入（服务端不再接受分块），暂存与已传的块都留着，恢复时从断点继续。
    l: '操作',
    render: r => {
      const b = [];
      if (r.status === 'paused')
        b.push(`<button class="small primary" data-ui-action="act" data-view="transfers" data-action="resume" data-id="${esc(r.sessionId)}">恢复</button>`);
      else
        b.push(`<button class="small" data-ui-action="act" data-view="transfers" data-action="pause" data-id="${esc(r.sessionId)}">暂停</button>`);
      b.push(`<button class="small danger" data-ui-action="act" data-view="transfers" data-action="cancel" data-id="${esc(r.sessionId)}">取消</button>`);
      return b.join(' ');
    }
  }
];

ACTIONS['transfers:pause'] = async id => {
  try {
    await api(`/api/v1/admin/upload-sessions/${id}/pause`, { method: 'POST' });
    toast('已暂停，恢复时从断点继续', 'ok');
    LOADERS.transfers();
  } catch (e) { errToast(e); }
};

ACTIONS['transfers:resume'] = async id => {
  try {
    await api(`/api/v1/admin/upload-sessions/${id}/resume`, { method: 'POST' });
    toast('已恢复，客户端会接着传', 'ok');
    LOADERS.transfers();
  } catch (e) { errToast(e); }
};

ACTIONS['transfers:cancel'] = async id => {
  // 取消之后这次传输就作废了，已传的部分不会被用上——问一句再动手
  if (!await confirmModal('取消这次传输？已经传上来的部分会作废，下次要重新传。')) return;
  try {
    await api(`/api/v1/admin/upload-sessions/${id}/cancel`, { method: 'POST' });
    toast('这次传输已取消', 'ok');
    LOADERS.transfers();
  } catch (e) { errToast(e); }
};
