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
    // 有传输没结束时两个目录不能改（服务端同样会拒绝）。锁在输入框上而不是等点保存再报错：
    // 目录是要一层层浏览着挑出来的，挑完一整轮再被告知"现在不能改"，那一轮就白做了。
    const locked = (s.inFlightUploads || 0) > 0;
    body.innerHTML = `<div class="card">
      <p class="text-muted">备份文件最终存放在「备份存放目录」下，按 <code>主机名\\任务名\\备份时间</code> 分层，每份备份带一个 <code>manifest.json</code> 文件清单。上传过程中的分块先落在「上传暂存目录」，校验通过后才搬进正式目录，因此两个目录建议放在不同磁盘。</p>
      <div class="notice">这里的路径都是<strong>服务端 ${esc(serverHostname || '本机')}</strong> 上的目录，不是你当前这台电脑的。从别的电脑打开管理页面时，「浏览…」列出的同样是服务器上的磁盘。</div>

      ${locked
        ? `<div class="notice warning">当前有 ${esc(s.inFlightUploads)} 个传输还没结束，两个目录暂时不能改：正在传的备份是按现在的暂存目录找断点续传的，已经传完、正在入库的那几份也正往现在的备份目录里搬——这时候换目录，它们会找不到已经传上来的数据。到<a href="#/transfers">「传输中」</a>页面等它们跑完，或者在那里把它们取消掉，再回来改。下面的「同时上传的备份数上限」不受影响，随时可以改。</div>`
        : ''}

      ${pathBlock('repo', '备份存放目录', s.repository, 'D:\\BackupRepository', '备份最终存这里。改完之后，新备份写入新目录，已经入库的备份仍留在原来的位置，不会被移动，也不会丢。', locked)}
      ${pathBlock('stage', '上传暂存目录', s.staging, 'D:\\BackupStaging', '只放上传过程中的临时分块，提交后自动清理。要留出足够空间容纳单份最大的备份。', locked)}

      ${uploadLimitBlock(s)}

      ${s.backupSetsOutsideRoot
        ? `<div class="notice warning">有 ${esc(s.backupSetsOutsideRoot)} 份已入库备份不在当前备份存放目录之内（通常是因为改过路径）。这些备份仍然可以查看和恢复，但保留策略到期时无法自动删除它们的文件——需要人工把老目录搬到新目录下，或者手动清理。</div>`
        : ''}

      <button class="primary" id="st_save">保存设置</button>
      <span class="tip">留空表示不指定，由配置文件或程序目录下的 data 子目录兜底。保存前会实地建目录并试写一个文件，写不进去会直接报错，不会等到下次备份才发现。</span>
    </div>
    <div class="card" id="cfgBackupCard">${loading()}</div>
    <div class="card" id="reverifyCard">${loading()}</div>`;

    $('#st_save').addEventListener('click', save);
    body.querySelectorAll('[data-pick]').forEach(btn => {
      btn.addEventListener('click', () => pickInto(btn.dataset.pick, btn.dataset.title));
    });
    renderConfigBackup();
    renderReverify();
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
function pathBlock(id, title, p, placeholder, note, locked) {
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
        <input id="st_${id}" class="mono" value="${esc(p.configuredPath || '')}" placeholder="${esc(placeholder)}"${locked ? ' disabled' : ''}>
        <button class="small" data-pick="st_${id}" data-title="${esc(title)}"${locked ? ' disabled' : ''}>浏览…</button>
      </div>
      <div class="hint">${esc(note)}${locked ? '<strong>有传输没结束，暂时不能改。</strong>' : ''}</div>
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

/* ── 配置备份包 ────────────────────────────────────────────────────────────

   .bmbp = 服务端密钥 + 客户端 CA + HTTPS 证书 + 整库转储，等于整个系统的命：
   它丢了，仓库里的备份文件即使还在，「哪个文件属于哪台机器、哪个任务、哪一天」
   也全没了，同时所有客户端身份作废。

   在此之前唯一的入口是服务器本机上的服务管理台按钮——远程管理的人完全没有入口，
   于是这件事的实际执行频率取决于有没有人正好去过机房。

   导出是**手动**的（2026-09-10 定板：不做定时自动导出），因此这一页把
   「上次是什么时候」显式写出来，超期时用告警色——没有定时任务替人记着这件事。 */
async function renderConfigBackup() {
  const card = $('#cfgBackupCard');
  if (!card) return;
  try {
    const s = await api('/api/v1/admin/config-backup/status');
    const age = s.latestExportedAt
      ? `${s.ageDays === 0 ? '今天' : `${s.ageDays} 天前`} · ${esc(s.latestFileName)} · ${fmtBytes(s.latestSizeBytes || 0)}`
      : '从未导出过';
    card.innerHTML = `<h2>配置备份包</h2>
      <p class="text-muted">包内是服务端密钥、客户端 CA、HTTPS 证书与整库转储。<strong>不含备份文件本体</strong>——那些要靠备份存放目录本身的异地副本。</p>
      <div class="notice${s.overdue ? ' warning' : ''}">上次配置备份：<strong>${esc(age)}</strong>${s.overdue ? `　超过 ${esc(s.overdueDays)} 天没有导出过，请现在导出一份。` : ''}</div>
      <div class="notice">落点在服务端的 <code>${esc(s.directory)}</code>，与它保护的数据在同一台机器上。<strong>导出后请把包下载走并存到另一台机器</strong>——服务器系统盘挂掉时，这个包会和数据一起没。</div>
      ${s.lastRemoteExport && s.lastRemoteExport.status === 'failed'
        ? `<div class="notice warning">上一次通过本页面导出失败：${esc(s.lastRemoteExport.errorMessage || '未知原因')}</div>`
        : ''}
      <button class="primary" id="cb_export">立即导出</button>
      <button id="cb_download"${s.packageCount ? '' : ' disabled'}>下载最近一份</button>
      <span class="tip">下载链接一次性、有有效期：包里有 CA 私钥，是全系统最敏感的文件。</span>`;

    $('#cb_export').addEventListener('click', exportConfigBackup);
    $('#cb_download').addEventListener('click', downloadConfigBackup);
  } catch (e) {
    card.innerHTML = `<h2>配置备份包</h2><div class="empty">加载失败：${esc(e.message)}</div>`;
  }
}

async function exportConfigBackup() {
  const btn = $('#cb_export');
  btn.disabled = true;
  // 导出要跑 pg_dump，是秒到分钟级的事，按钮必须自己说明它在干什么，
  // 否则人会以为没反应而反复点。
  btn.textContent = '正在导出…';
  try {
    await api('/api/v1/admin/config-backup/export', { method: 'POST' });
    toast('配置备份包已导出，请下载并保存到另一台机器');
    await renderConfigBackup();
  } catch (e) {
    errToast(e);
    btn.disabled = false;
    btn.textContent = '立即导出';
  }
}

async function downloadConfigBackup() {
  try {
    const t = await api('/api/v1/admin/config-backup/download-token', { method: 'POST' });
    // 令牌本身就是凭据，直接跳转即可；不用 fetch 拿 blob，
    // 那会把整个包读进浏览器内存，而这个包可以有几百 MB。
    window.location.href = t.downloadUrl;
  } catch (e) {
    errToast(e);
  }
}

/* ── 定期复查（整改清单 R18）──────────────────────────────────────────────

   这一层抓的是**入库之后**才会发生的三件事：磁盘静默损坏、误删、勒索软件加密。
   这三样入库那一次核对无论多严格都看不到——它们全都发生在核对完成之后。

   界面上必须把这句话写出来。不写的话，人看到的只是「又一个后台任务在磨盘」，
   第一反应是关掉它；而关掉之后，上面那三件事就再也没有任何地方能发现了。
   所以文案的落点是「建议调稀，而不是关闭」。 */
async function renderReverify() {
  const card = $('#reverifyCard');
  if (!card) return;
  try {
    const s = await api('/api/v1/admin/reverify-settings');
    card.innerHTML = `<h2>备份定期复查</h2>
      <p class="text-muted">后台按最久没复查的优先，定期重算已入库备份的 SHA-256，与入库时记录的值比对。</p>
      <div class="notice">它抓的是<strong>入库之后</strong>才会发生的三件事：<strong>磁盘静默损坏、误删、勒索软件加密</strong>。
        这三样在入库那一次核对里一定看不到——它们全都发生在核对完成之后。
        复查是纯磁盘读，会和上传抢同一块盘；<strong>盘吃紧时建议调稀，而不是关闭</strong>。</div>
      <div class="frow"><label><input type="checkbox" id="rv_enabled"${s.enabled ? ' checked' : ''}> 启用定期复查</label>
        <div class="hint">停用后不再产生任何复查工作项。备份详情页上的「重新校验」按钮不受影响，随时可以手工触发。</div></div>
      <div class="frow"><label for="rv_interval">复查间隔（小时）</label>
        <input id="rv_interval" type="number" min="1" max="720" value="${esc(s.intervalHours)}">
        <div class="hint">1~720。默认 168（7 天）。</div></div>
      <div class="frow"><label for="rv_batch">每轮复查份数</label>
        <input id="rv_batch" type="number" min="1" max="200" value="${esc(s.batchSize)}">
        <div class="hint">1~200。默认 1。份数 × 单份大小 = 每一轮要读多少盘。</div></div>
      <button class="primary" id="rv_save">保存</button>`;
    $('#rv_save').addEventListener('click', saveReverify);
  } catch (e) {
    card.innerHTML = `<h2>备份定期复查</h2><div class="empty">加载失败：${esc(e.message)}</div>`;
  }
}

async function saveReverify() {
  const btn = $('#rv_save');
  btn.disabled = true;
  try {
    await api('/api/v1/admin/reverify-settings', {
      method: 'PUT',
      body: {
        enabled: $('#rv_enabled').checked,
        intervalHours: Number($('#rv_interval').value) || 168,
        batchSize: Number($('#rv_batch').value) || 1
      }
    });
    toast('定期复查设置已保存');
    await renderReverify();
  } catch (e) {
    errToast(e);
    btn.disabled = false;
  }
}
