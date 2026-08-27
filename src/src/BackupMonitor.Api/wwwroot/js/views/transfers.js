/* js/views/transfers.js —— 正在传输的备份。
   备份工具最基本的一块表盘：一份几十 GB 的备份传了两小时，
   人要能看出来它还在动、大概什么时候完、还是已经卡死了。 */
import { api } from '../api.js';
import { App, LOADERS } from '../state.js';
import {
  $, esc, status, fmtBytes, fmtRate, fmtDuration, relTime,
  tableHtml, emptyState, xferBar
} from '../ui.js';
import { shell, loading } from '../app.js';

/* 有东西在传时 5 秒一刷：慢传输里数字动不动，本身就是最重要的那条信息。
   一条都没有时放慢到 15 秒——没人守着一个空页面，但新的传输开始时要能自己冒出来。 */
const POLL_ACTIVE_MS = 5000;
const POLL_IDLE_MS = 15000;

export async function vTransfers() {
  $('#app').innerHTML = shell('transfers', '传输中',
    `<div class="toolbar"><span class="tip">只显示正在进行的上传会话；传完入库后请到「备份集」查看。</span></div><div id="vwrap">${loading()}</div>`);
  await LOADERS.transfers();
}

LOADERS.transfers = async function () {
  clearTimeout(App.timer);
  const wrap = $('#vwrap');
  if (!wrap) return;
  try {
    // api() 在少数分支会返回 null，兜一层空数组，免得整页栽在 rows.length 上。
    const rows = (await api('/api/v1/admin/upload-sessions/active')) || [];
    wrap.innerHTML = summaryHtml(rows) + tableHtml(COLUMNS, rows, {
      empty: emptyState('ok', {
        glyph: '⇅', title: '当前没有正在传输的备份',
        sub: '客户端开始上传后，这里会自动出现并每 5 秒刷新一次'
      })
    });
    App.timer = setTimeout(() => {
      if (location.hash === '#/transfers') LOADERS.transfers();
    }, rows.length ? POLL_ACTIVE_MS : POLL_IDLE_MS);
  } catch (e) {
    // 刷新失败不清空已有内容也不再排下一次：一直重试会把错误刷成滚动条，
    // 而上一次的数字仍然比空白有用。
    wrap.innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`;
  }
};

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
  { l: '任务', render: r => `${esc(r.taskName)}<span class="sub">${esc(r.totalFiles)} 个文件</span>` },
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
  { l: '开始于', render: r => relTime(r.startedAt) }
];
