/* js/views/notifications.js —— 通知设置 + 通知投递，两 Tab 同页呈现 */
import { api } from '../api.js';
import { App, LOADERS } from '../state.js';
import {
  $, esc, L, optsOf, status, shortId, fmtDT, tableHtml, pagerHtml,
  emptyState, toast, errToast
} from '../ui.js';
import { shell, loading } from '../app.js';

export async function vNotifications(tab = 'settings') {
  App.state.notifications = App.state.notifications || { tab: 'settings', page: 1, pageSize: 15, status: '', channel: '', totalCount: 0 };
  const st = App.state.notifications;
  st.tab = tab === 'deliveries' ? 'deliveries' : 'settings';
  $('#app').innerHTML = shell('notifications', '通知', `<div class="tabbar" id="notificationTabs" role="tablist">
    <a role="tab" aria-selected="${st.tab === 'settings'}" class="tab ${st.tab === 'settings' ? 'active' : ''}" href="#/notifications/settings">通知设置</a>
    <a role="tab" aria-selected="${st.tab === 'deliveries'}" class="tab ${st.tab === 'deliveries' ? 'active' : ''}" href="#/notifications/deliveries">通知投递</a>
  </div><div id="notificationBody">${loading()}</div>`);
  if (st.tab === 'settings') await renderSettings();
  else await renderDeliveries();
}

/* 整改批次 C · C1：凭据类字段（SMTP 密码 / webhook 地址）后端已改为掩码回显（"__UNCHANGED__"
 * 表示已配置但不回显明文，null 表示未配置）。密码/webhook 输入框不再回填 value，
 * 只用 placeholder 提示当前是否已配置；用户不重新输入则保存时提交掩码本身，
 * 后端据此保留库里原值不变。*/
const UNCHANGED = '__UNCHANGED__';

/* 每个渠道一套筛选（R24）。
   在此之前只有渠道总开关：启用邮件 = 每一条告警都发。20 多类告警里
   「客户端 CPU 吃紧」「客户端版本落后」最容易变成没人看的背景噪音，
   而它们一旦被习惯性忽略，「备份复查未通过」也会跟着被忽略——
   报警的可信度是整体的，摊薄了就都没了。

   措辞上要一直说清楚一件事：**这里筛的是「发不发」，不是「报不报」**。
   被筛掉的告警照样在管理网页和客户端托盘上看得见。 */
const LEVEL_OPTS = [
  ['critical', '只发严重'],
  ['warning', '严重 + 警告（默认）'],
  ['notice', '全部都发（含提示级）']
];

function filterBlock(id, filter, categories) {
  const min = filter?.minLevel || 'warning';
  const excluded = new Set(filter?.excludedCategories || []);
  const levelOpts = LEVEL_OPTS
    .map(([v, t]) => `<option value="${v}" ${min === v ? 'selected' : ''}>${t}</option>`).join('');
  const boxes = categories.map(c => `<label class="cat-pick"><input type="checkbox" data-cat="${id}" value="${esc(c.key)}"${excluded.has(c.key) ? ' checked' : ''}> ${esc(c.label)}</label>`).join('');
  return `<div class="frow"><label>发送哪些告警</label><select id="${id}_min">${levelOpts}</select>
      <div class="hint">筛的是<strong>发不发这一封</strong>，不是报不报：被挡下的告警照样出现在告警页和客户端托盘上，也照样计数。</div></div>
    <details class="cat-filter"><summary>另外排除这些类别（已排除 <span id="${id}_cnt">${excluded.size}</span> 类）</summary>
      <div class="cat-grid">${boxes}</div></details>`;
}

function readFilter(id) {
  const excludedCategories = [...document.querySelectorAll(`[data-cat="${id}"]:checked`)].map(b => b.value);
  return { minLevel: $(`#${id}_min`).value, excludedCategories };
}

async function renderSettings() {
  const body = $('#notificationBody');
  try {
    const s = await api('/api/v1/admin/notification-settings');
    const cats = s.availableCategories || [];
    const secModeOpts = [['auto', '自动协商（按端口）'], ['none', '不加密'], ['starttls', 'STARTTLS（如 587）'], ['ssl', '隐式 TLS/SSL（如 465）']]
      .map(([v, t]) => `<option value="${v}" ${(s.email.securityMode || 'auto') === v ? 'selected' : ''}>${t}</option>`).join('');
    body.innerHTML = `<div class="card notification-settings">
      <p class="text-muted">设置是配置，投递 Tab 是该配置产生的运行结果。修改后约 1 分钟内对新告警生效。</p>
      <h3>邮件</h3><div class="frow inline"><label><input type="checkbox" id="n_email_en" ${s.email.enabled ? 'checked' : ''}> 启用邮件渠道</label></div>
      <div class="frow"><label>收件人（每行一个）</label><textarea id="n_email_to">${esc((s.email.recipients || []).join('\n'))}</textarea></div>
      <div class="form-grid"><div class="frow"><label>SMTP 服务器</label><input id="n_email_host" value="${esc(s.email.smtpHost || '')}"></div><div class="frow"><label>SMTP 端口</label><input id="n_email_port" type="number" value="${esc(s.email.smtpPort || 587)}"></div>
      <div class="frow"><label>安全模式</label><select id="n_email_sec">${secModeOpts}</select></div>
      <div class="frow"><label>SMTP 用户名</label><input id="n_email_user" value="${esc(s.email.smtpUsername || '')}"></div><div class="frow"><label>SMTP 密码</label><input id="n_email_pass" type="password" placeholder="${s.email.smtpPassword === UNCHANGED ? '已配置，留空则不修改' : '留空则匿名发送'}"></div>
      <div class="frow"><label>发件人地址</label><input id="n_email_from" value="${esc(s.email.fromAddress || '')}"></div></div>
      <div class="frow inline"><button class="small" id="n_email_test">发送测试邮件</button><span class="tip">用当前表单里的配置试发一封，SMTP 服务器返回的原始错误会直接显示</span></div>
      ${filterBlock('n_email_f', s.email.filter, cats)}
      <h3>企业微信机器人</h3><div class="frow inline"><label><input type="checkbox" id="n_wecom_en" ${s.wecom.enabled ? 'checked' : ''}> 启用企业微信</label></div><div class="frow"><label>Webhook 地址</label><input id="n_wecom_url" class="mono" type="password" placeholder="${s.wecom.webhookUrl === UNCHANGED ? '已配置，留空则不修改' : '含 access_token，请从企业微信机器人管理页复制'}"></div>
      ${filterBlock('n_wecom_f', s.wecom.filter, cats)}
      <h3>钉钉机器人</h3><div class="frow inline"><label><input type="checkbox" id="n_ding_en" ${s.dingtalk.enabled ? 'checked' : ''}> 启用钉钉</label></div><div class="frow"><label>Webhook 地址</label><input id="n_ding_url" class="mono" type="password" placeholder="${s.dingtalk.webhookUrl === UNCHANGED ? '已配置，留空则不修改' : '含 access_token，请从钉钉机器人管理页复制'}"></div>
      ${filterBlock('n_ding_f', s.dingtalk.filter, cats)}
      <button class="primary" id="n_save">保存设置</button>
    </div>
    <div class="card" id="thresholdCard">${loading()}</div>`;

    const buildEmail = () => ({
      enabled: $('#n_email_en').checked,
      recipients: $('#n_email_to').value.split('\n').map(x => x.trim()).filter(Boolean),
      smtpHost: $('#n_email_host').value.trim() || null,
      smtpPort: Number($('#n_email_port').value) || 587,
      securityMode: $('#n_email_sec').value,
      smtpUsername: $('#n_email_user').value.trim() || null,
      // 留空 → 提交掩码常量，后端保留原密码；重新输入 → 提交新明文
      smtpPassword: $('#n_email_pass').value || (s.email.smtpPassword === UNCHANGED ? UNCHANGED : null),
      fromAddress: $('#n_email_from').value.trim() || null,
      filter: readFilter('n_email_f')
    });

    $('#n_save').addEventListener('click', async () => {
      try {
        await api('/api/v1/admin/notification-settings', { method: 'PUT', body: {
          email: buildEmail(),
          wecom: { enabled: $('#n_wecom_en').checked, webhookUrl: $('#n_wecom_url').value.trim() || (s.wecom.webhookUrl === UNCHANGED ? UNCHANGED : null), filter: readFilter('n_wecom_f') },
          dingtalk: { enabled: $('#n_ding_en').checked, webhookUrl: $('#n_ding_url').value.trim() || (s.dingtalk.webhookUrl === UNCHANGED ? UNCHANGED : null), filter: readFilter('n_ding_f') }
        } });
        toast('通知设置已保存', 'ok');
      } catch (e) { errToast(e); }
    });

    // 折叠起来的勾选数要在标题上看得见，否则「排除了几类」这件事被折叠藏掉了。
    body.querySelectorAll('[data-cat]').forEach(box => box.addEventListener('change', () => {
      const id = box.getAttribute('data-cat');
      const count = body.querySelectorAll(`[data-cat="${id}"]:checked`).length;
      const label = $(`#${id}_cnt`);
      if (label) label.textContent = count;
    }));

    renderThresholds();

    $('#n_email_test').addEventListener('click', async () => {
      try {
        const email = buildEmail();
        const recipient = (email.recipients || [])[0];
        if (!recipient) { errToast(new Error('请先在收件人列表里填一个邮箱地址')); return; }
        await api('/api/v1/admin/notification-settings/test', { method: 'POST', body: { email, recipient } });
        toast('测试邮件已发送，请检查收件箱', 'ok');
      } catch (e) { errToast(e); }
    });
  } catch (e) { body.innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`; }
}

/* 告警触发阈值（R25）。
   与上面的筛选分工：**这里决定告警什么时候产生，上面决定产生之后发不发。**
   两者不能互相替代——阈值调高会让告警在网页上也一并消失，筛选只是不打扰人。

   这四个值一直存在于 system_settings 里、代码也一直在读，但界面上没有入口：
   一台常年 90% CPU 的 ERP 服务器要把阈值调到 95%，此前唯一的办法是连进数据库改 JSON。
   于是实际发生的是没人调，告警每天照报，报到没人看。 */
async function renderThresholds() {
  const card = $('#thresholdCard');
  if (!card) return;
  try {
    const t = await api('/api/v1/admin/alert-thresholds');
    card.innerHTML = `<h3>告警触发阈值</h3>
      <p class="text-muted">这里决定告警<strong>什么时候产生</strong>；上面的「发送哪些告警」决定产生之后<strong>发不发通知</strong>。
        调高阈值会让它在告警页上也一并消失，只是不想被打扰的话改上面那个。</p>
      <div class="form-grid">
        <div class="frow"><label for="th_cpu">客户端 CPU 使用率达到（%）</label>
          <input id="th_cpu" type="number" min="1" max="100" value="${esc(t.clientCpuPercent)}">
          <div class="hint">默认 85。常年满负荷的数据库 / ERP 服务器建议调到 95 或更高。</div></div>
        <div class="frow"><label for="th_mem">客户端内存使用率达到（%）</label>
          <input id="th_mem" type="number" min="1" max="100" value="${esc(t.clientMemoryPercent)}">
          <div class="hint">默认 90。SQL Server 会主动吃满内存，这类机器上 90% 属于正常。</div></div>
        <div class="frow"><label for="th_disk">源盘可用空间低于（%）</label>
          <input id="th_disk" type="number" min="1" max="99" value="${esc(t.clientDiskFreePercent)}">
          <div class="hint">默认 10。只看备份源文件所在的盘。</div></div>
        <div class="frow"><label for="th_renotify">未恢复时隔多久补发一次（小时）</label>
          <input id="th_renotify" type="number" min="1" max="720" value="${esc(t.renotifyHours)}">
          <div class="hint">默认 24。同一条告警一直不恢复时隔这么久重发一封「【仍未恢复】」。</div></div>
      </div>
      <button class="primary" id="th_save">保存阈值</button>`;
    $('#th_save').addEventListener('click', saveThresholds);
  } catch (e) {
    card.innerHTML = `<h3>告警触发阈值</h3><div class="empty">加载失败：${esc(e.message)}</div>`;
  }
}

async function saveThresholds() {
  const btn = $('#th_save');
  btn.disabled = true;
  try {
    await api('/api/v1/admin/alert-thresholds', { method: 'PUT', body: {
      clientCpuPercent: Number($('#th_cpu').value) || 85,
      clientMemoryPercent: Number($('#th_mem').value) || 90,
      clientDiskFreePercent: Number($('#th_disk').value) || 10,
      renotifyHours: Number($('#th_renotify').value) || 24
    } });
    toast('告警阈值已保存', 'ok');
    await renderThresholds();
  } catch (e) {
    errToast(e);
    btn.disabled = false;
  }
}

async function renderDeliveries() {
  const st = App.state.notifications;
  $('#notificationBody').innerHTML = `<div class="toolbar"><select id="n_status"><option value="">全部状态</option>${optsOf(L.notif_status).map(o => `<option value="${o.v}" ${st.status === o.v ? 'selected' : ''}>${o.t}</option>`).join('')}</select>
    <select id="n_channel"><option value="">全部渠道</option>${optsOf(L.channel).map(o => `<option value="${o.v}" ${st.channel === o.v ? 'selected' : ''}>${o.t}</option>`).join('')}</select><button class="primary" id="n_query">查询</button><span class="tip">通知派发工作器按配置间隔驱动，失败自动重试</span></div><div id="nwrap">${loading()}</div>`;
  $('#n_status').onchange = () => { st.status = $('#n_status').value; st.page = 1; LOADERS.notifications(); };
  $('#n_channel').onchange = () => { st.channel = $('#n_channel').value; st.page = 1; LOADERS.notifications(); };
  $('#n_query').addEventListener('click', () => { st.page = 1; LOADERS.notifications(); });
  await LOADERS.notifications();
}
LOADERS.notifications = async function () {
  const st = App.state.notifications;
  const wrap = $('#nwrap'); if (!wrap || st.tab !== 'deliveries') return;
  try {
    const q = new URLSearchParams({ page: st.page, pageSize: st.pageSize });
    if (st.status) q.set('status', st.status);
    if (st.channel) q.set('channel', st.channel);
    const data = await api('/api/v1/admin/notification-deliveries?' + q);
    st.totalCount = data.totalCount;
    wrap.innerHTML = tableHtml([
      { l: '告警', render: r => esc(r.alertTitle || shortId(r.alertId)) },
      { l: '渠道', render: r => esc(L.channel[r.channel] || r.channel) },
      { l: '收件人', render: r => `<span class="mono">${esc(r.recipient)}</span>` },
      { l: '状态', render: r => status('notif_status', r.status) },
      { l: '尝试次数', num: true, k: 'attemptCount' }, { l: '最近尝试', render: r => fmtDT(r.lastAttemptAt) },
      { l: '发送成功', render: r => fmtDT(r.sentAt) }, { l: '错误', render: r => `<span class="text-error">${esc((r.errorMessage || '').slice(0, 60))}</span>` }
    ], data.items, { empty: emptyState('ok', { title: '没有通知投递记录', sub: '当前没有需要关注的发送结果' }) }) + pagerHtml('notifications', st);
  } catch (e) { wrap.innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`; }
};
