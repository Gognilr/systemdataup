/* js/views/job-history.js —— 已提交批次结果（原批量操作页的只读部分） */
import { api } from '../api.js';
import { App, ACTIONS, LOADERS } from '../state.js';
import {
  $, esc, status, shortId, fmtDT, tableHtml, pagerHtml,
  emptyState, openModal, errToast
} from '../ui.js';
import { shell, loading } from '../app.js';

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
      { l: '并发', num: true, render: r => `${esc(r.maxConcurrentClients)} 客户端 × ${esc(r.maxConcurrentPerClient)}` },
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
      { l: '候选备份集', render: r => `<span class="mono">${shortId(r.candidateBackupSetId)}</span>` },
      { l: '候选键', render: r => `<span class="mono">${esc(r.candidateKey)}</span>` },
      { l: '客户端', k: 'clientHostname' }, { l: '指令状态', render: r => esc(r.commandStatus || '—') },
      { l: '上传会话', render: r => esc(r.uploadSessionStatus || '—') }
    ], d.items, { empty: '<div class="empty">批次为空</div>' })}`, { wide: true });
  } catch (e) { errToast(e); }
};
