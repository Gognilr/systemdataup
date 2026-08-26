/* js/views/settings.js —— 存储设置：备份存到哪、上传暂存放哪
 *
 * 这两个目录以前只在服务端安装向导里问一次，装完就没有任何界面能看、能改。
 * 于是"我的备份到底存到哪去了"这个最基本的问题，得去翻 system_settings 表才有答案。 */
import { api } from '../api.js';
import { $, esc, fmtBytes, toast, errToast } from '../ui.js';
import { shell, loading } from '../app.js';

/* 取值来源：让人一眼看出当前生效的路径是谁给的——自己配的、配置文件里的、还是兜底默认值。 */
const SOURCE_LABEL = {
  database: '管理端配置',
  configuration: 'appsettings 配置文件',
  default: '未配置，使用程序目录下的默认位置'
};

export async function vSettings() {
  $('#app').innerHTML = shell('settings', '存储设置', `<div id="storageBody">${loading()}</div>`);
  await render();
}

async function render() {
  const body = $('#storageBody');
  try {
    const s = await api('/api/v1/admin/storage-settings');
    body.innerHTML = `<div class="card">
      <p class="text-muted">备份文件最终存放在「备份存放目录」下，按 <code>主机名\\任务名\\备份时间</code> 分层，每份备份带一个 <code>manifest.json</code> 文件清单。上传过程中的分块先落在「上传暂存目录」，校验通过后才搬进正式目录，因此两个目录建议放在不同磁盘。</p>

      ${pathBlock('repo', '备份存放目录', s.repository, 'D:\\BackupRepository', '备份最终存这里。改完之后，新备份写入新目录，已经入库的备份仍留在原来的位置，不会被移动，也不会丢。')}
      ${pathBlock('stage', '上传暂存目录', s.staging, 'D:\\BackupStaging', '只放上传过程中的临时分块，提交后自动清理。要留出足够空间容纳单份最大的备份。')}

      ${s.backupSetsOutsideRoot
        ? `<div class="notice warning">有 ${esc(s.backupSetsOutsideRoot)} 份已入库备份不在当前备份存放目录之内（通常是因为改过路径）。这些备份仍然可以查看和恢复，但保留策略到期时无法自动删除它们的文件——需要人工把老目录搬到新目录下，或者手动清理。</div>`
        : ''}

      <button class="primary" id="st_save">保存设置</button>
      <span class="tip">留空表示不指定，由配置文件或程序目录下的 data 子目录兜底。保存前会实地建目录并试写一个文件，写不进去会直接报错，不会等到下次备份才发现。</span>
    </div>`;

    $('#st_save').addEventListener('click', save);
  } catch (e) {
    body.innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`;
  }
}

/* 一个存储根的展示块：当前生效值 + 磁盘余量 + 可写性 + 输入框。
   输入框故意不回填"生效路径"，只回填管理员显式配过的值——
   否则清空配置（回落到默认）这件事就没法在界面上表达了。 */
function pathBlock(id, title, p, placeholder, note) {
  const disk = p.freeBytes != null && p.totalBytes != null
    ? `剩余 ${fmtBytes(p.freeBytes)} / 共 ${fmtBytes(p.totalBytes)}`
    : '容量未知';
  const health = p.problem
    ? `<span class="status status--wait">${esc(p.problem)}</span>`
    : `<span class="status status--ok">目录可写</span>`;

  return `<h3>${esc(title)}</h3>
    <div class="kv">
      <div class="row"><div class="k">当前生效</div><div class="v"><span class="dirpath mono">${esc(p.effectivePath)}</span></div></div>
      <div class="row"><div class="k">取值来源</div><div class="v">${esc(SOURCE_LABEL[p.source] || p.source)}</div></div>
      <div class="row"><div class="k">磁盘</div><div class="v">${esc(disk)} ${health}</div></div>
    </div>
    <div class="frow"><label>${esc(title)}</label>
      <input id="st_${id}" class="mono" value="${esc(p.configuredPath || '')}" placeholder="${esc(placeholder)}">
      <div class="hint">${esc(note)}</div>
    </div>`;
}

async function save() {
  const btn = $('#st_save');
  btn.disabled = true;
  try {
    await api('/api/v1/admin/storage-settings', { method: 'PUT', body: {
      repositoryPath: $('#st_repo').value.trim() || null,
      stagingPath: $('#st_stage').value.trim() || null
    } });
    toast('存储设置已保存', 'ok');
    await render();
  } catch (e) {
    errToast(e);
    btn.disabled = false;
  }
}
