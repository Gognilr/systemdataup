/* js/views/settings.js —— 存储设置：备份存到哪、上传暂存放哪
 *
 * 这两个目录以前只在服务端安装向导里问一次，装完就没有任何界面能看、能改。
 * 于是"我的备份到底存到哪去了"这个最基本的问题，得去翻 system_settings 表才有答案。 */
import { api } from '../api.js';
import { $, esc, fmtBytes, toast, errToast, openModal, closeModal, skeleton } from '../ui.js';
import { shell, loading } from '../app.js';

/* 取值来源：让人一眼看出当前生效的路径是谁给的——自己配的、配置文件里的、还是兜底默认值。 */
const SOURCE_LABEL = {
  database: '管理端配置',
  configuration: 'appsettings 配置文件',
  default: '未配置，使用程序目录下的默认位置'
};

/* 服务端主机名。管理页面多半不是在服务器本机上打开的，而这一页所有路径、
   目录选择器列出的所有磁盘，指的都是服务端那台机器——必须写出来是哪台。 */
let serverHostname = '';

export async function vSettings() {
  $('#app').innerHTML = shell('settings', '存储设置', `<div id="storageBody">${loading()}</div>`);
  await render();
}

async function render() {
  const body = $('#storageBody');
  try {
    const s = await api('/api/v1/admin/storage-settings');
    serverHostname = s.serverHostname || '';
    body.innerHTML = `<div class="card">
      <p class="text-muted">备份文件最终存放在「备份存放目录」下，按 <code>主机名\\任务名\\备份时间</code> 分层，每份备份带一个 <code>manifest.json</code> 文件清单。上传过程中的分块先落在「上传暂存目录」，校验通过后才搬进正式目录，因此两个目录建议放在不同磁盘。</p>
      <div class="notice">这里的路径都是<strong>服务端 ${esc(serverHostname || '本机')}</strong> 上的目录，不是你当前这台电脑的。从别的电脑打开管理页面时，「浏览…」列出的同样是服务器上的磁盘。</div>

      ${pathBlock('repo', '备份存放目录', s.repository, 'D:\\BackupRepository', '备份最终存这里。改完之后，新备份写入新目录，已经入库的备份仍留在原来的位置，不会被移动，也不会丢。')}
      ${pathBlock('stage', '上传暂存目录', s.staging, 'D:\\BackupStaging', '只放上传过程中的临时分块，提交后自动清理。要留出足够空间容纳单份最大的备份。')}

      ${uploadLimitBlock(s)}

      ${s.backupSetsOutsideRoot
        ? `<div class="notice warning">有 ${esc(s.backupSetsOutsideRoot)} 份已入库备份不在当前备份存放目录之内（通常是因为改过路径）。这些备份仍然可以查看和恢复，但保留策略到期时无法自动删除它们的文件——需要人工把老目录搬到新目录下，或者手动清理。</div>`
        : ''}

      <button class="primary" id="st_save">保存设置</button>
      <span class="tip">留空表示不指定，由配置文件或程序目录下的 data 子目录兜底。保存前会实地建目录并试写一个文件，写不进去会直接报错，不会等到下次备份才发现。</span>
    </div>`;

    $('#st_save').addEventListener('click', save);
    body.querySelectorAll('[data-pick]').forEach(btn => {
      btn.addEventListener('click', () => pickInto(btn.dataset.pick, btn.dataset.title));
    });
  } catch (e) {
    body.innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`;
  }
}

/* 同时上传的备份数上限。
   它放在存储设置里而不是别处，是因为这道闸管的就是「服务端暂存盘同时被几路读写」——
   跟上传暂存目录是同一件事的两面。

   这个值此前只存在于 system_settings 表里，界面上没有任何入口：
   备份计划一起跑几台服务器时，「为什么这台一直在等」这个问题在界面上无从回答，
   而答案往往就是这个数字。 */
function uploadLimitBlock(s) {
  const limit = s.maxConcurrentUploadsTotal;
  const active = s.activeUploads || 0;
  return `<h3>同时上传的备份数上限</h3>
    <dl class="storage-facts">
      <dt>当前上限</dt><dd>${esc(limit)} 份</dd>
      <dt>此刻在传</dt><dd>${esc(active)} 份${active >= limit ? ' <span class="status status--wait">已顶到上限</span>' : ''}</dd>
    </dl>
    <div class="frow"><label>上限（1–64）</label>
      <input id="st_limit" type="number" min="1" max="64" step="1" value="${esc(limit)}">
      <div class="hint">数的是<strong>上传会话</strong>，一个业务单元算一份——U8 一台机器 18 个账套时，这 18 份各算各的。
        客户端是一条一条传的，所以一台服务器同时只占一份名额，这个数实际上约等于「最多允许几台客户端同时往上传」。
        扫描（预检）不占名额。调大会让暂存盘同时被更多路读写，受益的是带宽、代价是磁盘 IO 和暂存空间。</div>
    </div>`;
}

/* 一个存储根的展示块：当前生效值 + 磁盘余量 + 可写性 + 输入框 + 浏览按钮。
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
    <dl class="storage-facts">
      <dt>当前生效</dt><dd><code class="dirpath">${esc(p.effectivePath)}</code></dd>
      <dt>取值来源</dt><dd>${esc(SOURCE_LABEL[p.source] || p.source)}</dd>
      <dt>磁盘</dt><dd>${esc(disk)} ${health}</dd>
    </dl>
    <div class="frow"><label>${esc(title)}</label>
      <div class="storage-path-input">
        <input id="st_${id}" class="mono" value="${esc(p.configuredPath || '')}" placeholder="${esc(placeholder)}">
        <button class="small" data-pick="st_${id}" data-title="${esc(title)}">浏览…</button>
      </div>
      <div class="hint">${esc(note)}</div>
    </div>`;
}

/* ── 目录选择器 ──
   服务端本机的目录树。手敲路径的问题不在于麻烦，而在于敲错一个字符不会有任何报错——
   备份会安安静静地写进另一个目录，等到要恢复时才发现。 */
async function pickInto(inputId, title) {
  const input = $('#' + inputId);
  const picked = await pickDirectory(title, input.value.trim());
  if (picked) input.value = picked;
}

function pickDirectory(title, startPath) {
  return new Promise(resolve => {
    let settled = false;
    let current = null;         // 当前所在目录，null 表示磁盘列表
    let selected = null;        // 选中的目录（点击列表项即选中）
    let newName = '';           // 新建子目录名。每次重绘都会重建输入框，值必须自己留着

    const finish = value => { if (!settled) { settled = true; resolve(value); } };
    const heading = serverHostname ? `在服务端 ${serverHostname} 上选择${title}` : `选择${title}`;
    const ov = openModal(heading, `<div id="dpick">${skeleton(4)}</div>`, { okText: '选择此目录' });
    ov.addEventListener('bm:dismiss', () => finish(null));

    const bodyEl = () => ov.querySelector('#dpick');
    const okBtn = ov.querySelector('[data-ok]');

    okBtn.addEventListener('click', () => {
      const target = selected || current;
      if (!target) { toast('请先选择一个目录', 'err'); return; }
      // 新建子目录名留空就是选中当前目录本身。目录不必事先存在——
      // 保存设置时会建出来，所以这里不需要一个"新建文件夹"的写接口。
      const extra = newName.trim();
      finish(extra ? joinPath(target, extra) : target);
      closeModal();
    });

    ov.addEventListener('click', async ev => {
      const el = ev.target.closest('[data-dact]');
      if (!el) return;
      const act = el.dataset.dact;
      if (act === 'open') await load(el.dataset.path);
      else if (act === 'up') await load(el.dataset.path || null);
      else if (act === 'drives') await load(null);
      else if (act === 'select') { selected = el.dataset.path; paint(); }
      else if (act === 'retry') await load(current);
    });

    ov.addEventListener('input', ev => {
      if (ev.target.id !== 'dpick_new') return;
      newName = ev.target.value;
      const preview = ov.querySelector('[data-dpreview]');
      if (preview) preview.textContent = previewPath();
    });

    let snapshot = null;

    async function load(path) {
      bodyEl().innerHTML = `<div class="hint">正在读取 ${path ? esc(path) : '磁盘列表'}…</div>${skeleton(4)}`;
      try {
        const query = path ? `?path=${encodeURIComponent(path)}` : '';
        snapshot = await api(`/api/v1/admin/storage-settings/browse${query}`);
        current = snapshot.path || null;
        selected = current;
        paint();
      } catch (e) {
        snapshot = null;
        bodyEl().innerHTML = `<div class="empty">${esc(e.message)}</div>
          <div class="dirbar"><button class="small" data-dact="drives">回到磁盘列表</button></div>`;
      }
    }

    function paint() {
      if (!snapshot) return;
      const notes = [];
      if (snapshot.truncated) notes.push('这个目录下的子目录太多，只列出了一部分。');
      if (snapshot.deniedCount) notes.push(`有 ${snapshot.deniedCount} 个子目录没有访问权限，未能列出。`);
      // 网络盘这条只在磁盘列表这一层说：那正是有人找不到自己映射的 Z: 盘、
      // 以为功能坏了的时刻。服务以 LocalSystem 运行，盘符映射是按登录会话的，服务看不到。
      if (snapshot.isDriveList)
        notes.push('这些是服务端本机的磁盘。映射的网络盘符（如 Z:）不会出现在这里——要存到 NAS，请关掉本窗口、在输入框里手工填写 UNC 路径（如 \\\\nas\\backup），并确保服务器的机器账户对该共享有写权限。');

      const rows = snapshot.entries.map(e => `
        <div class="dnode${selected === e.path ? ' is-picked' : ''}">
          <button class="dname" data-dact="select" data-path="${esc(e.path)}">${esc(e.name)}</button>
          ${e.isDrive && e.freeBytes != null
            ? `<span class="dmeta">剩余 ${fmtBytes(e.freeBytes)} / 共 ${fmtBytes(e.totalBytes)}</span>`
            : ''}
          <button class="small" data-dact="open" data-path="${esc(e.path)}">打开</button>
        </div>`).join('');

      bodyEl().innerHTML = `
        <div class="dirbar">
          <button class="small" data-dact="drives">‹ 磁盘</button>
          ${current && snapshot.parentPath
            ? `<button class="small" data-dact="up" data-path="${esc(snapshot.parentPath)}">‹ 上一级</button>`
            : ''}
          <code class="dirpath">${esc(current || '磁盘列表')}</code>
        </div>
        ${notes.map(n => `<div class="hint">${esc(n)}</div>`).join('')}
        <div class="dirtree">${rows || '<div class="empty">这个目录下没有子目录</div>'}</div>
        <div class="frow" style="margin-top:12px">
          <label>在选中目录下新建子目录（可选）</label>
          <input id="dpick_new" class="mono" value="${esc(newName)}" placeholder="如 BackupRepository">
          <div class="hint">目录不必事先存在，保存设置时会自动创建。</div>
        </div>
        <div class="hint">当前选中：<code class="dirpath" data-dpreview>${esc(previewPath())}</code></div>`;
    }

    /* 点"选择此目录"最终会得到哪个路径——把它一直摆在眼前，
       不要让人点完确定才发现少了一层或多了一层。 */
    function previewPath() {
      const base = selected || current;
      if (!base) return '（未选择）';
      const extra = newName.trim();
      return extra ? joinPath(base, extra) : base;
    }

    load(startPath || null);
  });
}

/* 拼接 Windows 路径：只负责补一个反斜杠，真正的合法性由服务端在保存时判定。 */
function joinPath(base, name) {
  return base.replace(/[\\/]+$/, '') + '\\' + name.replace(/^[\\/]+/, '');
}

async function save() {
  const btn = $('#st_save');
  btn.disabled = true;
  try {
    await api('/api/v1/admin/storage-settings', { method: 'PUT', body: {
      repositoryPath: $('#st_repo').value.trim() || null,
      stagingPath: $('#st_stage').value.trim() || null,
      // 留空按「不改」提交 null，而不是当成 0：路径和并发共用一个表单，
      // 把空输入框翻译成一个具体数字会在人没打算动它的时候把它改掉。
      maxConcurrentUploadsTotal: $('#st_limit').value.trim() === '' ? null : Number($('#st_limit').value)
    } });
    toast('存储设置已保存', 'ok');
    await render();
  } catch (e) {
    errToast(e);
    btn.disabled = false;
  }
}
