/* js/views/job-history.js —— 已提交批次结果（原批量操作页的只读部分） */
import { api } from '../api.js';
import { App, ACTIONS, LOADERS } from '../state.js';
import {
  $, esc, status, shortId, fmtDT, tableHtml, pagerHtml,
  emptyState, openModal, errToast
} from '../ui.js';
import { shell, loading } from '../app.js';

/* 队列里每一项的状态（V028 起批量上传走顺序执行队列，排队中的项也要看得见） */
const QUEUE_TEXT = {
  pending: '排队中', running: '执行中', succeeded: '完成', failed: '失败',
  timeout: '等超时', skipped: '跳过', cancelled: '已取消'
};

export async function vJobHistory() {
  App.state.jobHistory = App.state.jobHistory || { page: 1, pageSize: 10, totalCount: 0 };
  const st = App.state.jobHistory;
  $('#app').innerHTML = shell('job-history', '作业历史', `<div class="toolbar"><span class="tip">查看已提交批量上传作业的执行结果；批量动作请从客户端列表多选后发起。</span></div><div id="vwrap">${loading()}</div>`);
  await LOADERS.jobHistory();
}
LOADERS.jobHistory = async function () {
  const st = App.state.jobHistory;
  const wrap = $('#vwrap'); if (!wrap) return;
  try {
    const data = await api(`/api/v1/admin/operations/upload-batches?page=${st.page}&pageSize=${st.pageSize}`);
    st.totalCount = data.totalCount;
    wrap.innerHTML = tableHtml([
      { l: '批次', render: r => `<b>${esc(r.name || shortId(r.id))}</b>` },
      { l: '状态', render: r => status('batch_status', r.status) },
      { l: '进度', num: true, render: r => `${esc(r.succeededItems)} 成功 / ${esc(r.failedItems)} 失败 / 共 ${esc(r.totalItems)}` },
      // V028 起这个数字真的会限流：批次投进顺序执行队列，它就是队列并发度。
      // 「× 每客户端 1」那半截删掉了——Agent 天生串行执行指令，那个值填几都一样。
      { l: '同时几台', num: true, render: r => `${esc(r.maxConcurrentClients)} 台` },
      { l: '创建人', k: 'createdByName' }, { l: '创建时间', render: r => fmtDT(r.createdAt) },
      { l: '完成时间', render: r => fmtDT(r.completedAt) },
      { l: '操作', render: r => `<button class="small" data-ui-action="act" data-view="job-history" data-action="detail" data-id="${esc(r.id)}">详情</button>` }
    ], data.items, { empty: emptyState('first', { glyph: '▤', title: '暂无作业历史', sub: '从客户端列表发起批量上传后，结果会出现在这里' }) }) + pagerHtml('jobHistory', st);
  } catch (e) { wrap.innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`; }
};
ACTIONS['job-history:detail'] = async id => {
  try {
    const d = await api(`/api/v1/admin/operations/upload-batches/${id}`);
    openModal(`作业详情：${d.name || shortId(d.id)}`, `<div class="kv">
      <div class="row"><div class="k">状态</div><div class="v">${status('batch_status', d.status)}</div></div>
      <div class="row"><div class="k">进度</div><div class="v">${esc(d.succeededItems)} 成功 / ${esc(d.failedItems)} 失败 / 共 ${esc(d.totalItems)}</div></div>
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
