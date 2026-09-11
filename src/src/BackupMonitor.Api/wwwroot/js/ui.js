/* js/ui.js —— 组件工厂（UI-REDESIGN §6 / §9）：
   状态标记、表格、分页、模态、抽屉、Toast、骨架、空态、批量动作条。 */
import { App } from './state.js';

export const $ = s => document.querySelector(s);
export const esc = v => String(v ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

/* ── 枚举文案 ── */
export const L = {
  client_status: { pending_approval: '待审批', online: '在线', suspected_offline: '疑似离线', offline: '离线', disabled: '已禁用', revoked: '已注销', certificate_expired: '证书过期' },
  backup_set_status: { verifying: '校验中', available: '可用', verification_failed: '校验失败', quarantined: '已隔离', recycle_bin: '回收站', deleted: '已删除' },
  restore_status: { requested: '已请求', verifying: '校验中', ready: '就绪', downloading: '下载中', completed: '已完成', failed: '失败', expired: '已过期' },
  alert_level: { critical: '严重', warning: '警告', notice: '提示' },
  alert_status: { open: '未处理', acknowledged: '已确认', in_progress: '处理中', recovered: '已恢复', closed: '已关闭', ignored: '已忽略' },
  notif_status: { pending: '待发送', sent: '已发送', failed: '失败' },
  task_mode: { automatic: '自动', approval_required: '需审批', manual: '手动', monitor_only: '仅监控', paused: '已暂停' },
  importance: { low: '低', normal: '普通', high: '高', critical: '关键' },
  channel: { email: '邮件', wecom: '企业微信', dingtalk: '钉钉' },
  result: { success: '成功', failure: '失败' },
  batch_status: { pending: '等待', running: '执行中', completed: '已完成', partial: '部分完成', failed: '失败', cancelled: '已取消' },
  command_status: { pending: '等待客户端领取', claimed: '客户端已领取', running: '执行中', succeeded: '成功', failed: '失败', cancelled: '已取消', expired: '已过期', rejected: '被拒绝' },
  recognizer: { latest_single_file: '最新单文件', latest_directory: '最新目录', multi_file_set: '多文件集', subdirectory_units: '子目录单元' },
  service_expected: { running: '运行中', stopped: '已停止' },
  service_actual: { running: '运行中', stopped: '已停止', paused: '已暂停', not_found: '服务不存在', unknown: '未知' },
  service_start_type: { boot: '引导启动', system: '系统启动', auto: '自动', manual: '手动', disabled: '已禁用' },
  client_runtime: { idle: '空闲', uploading: '上传中', working: '执行中' },
  upload_status: { created: '已创建', waiting_permission: '等待放行', uploading: '传输中', paused: '已暂停', retry_wait: '等待重试', received: '已接收', verifying: '校验中', verified: '已校验', committed: '已入库', failed: '失败', cancelled: '已取消', expired: '已过期' },
  precheck: { not_scanned: '未检查', passed: '通过', still_changing: '文件还在写入', no_new_backup: '没有新备份', required_file_missing: '缺少必需文件', size_abnormal: '大小不对', path_not_found: '路径不存在', access_denied: '没有读取权限', failed: '失败' },
  // 告警类别此前在列表和详情里都是原样输出的英文标识（precheck_failed、upload_commit_failed…）。
  // 收到告警的人要据此决定做什么，这一列却是最需要翻译的一列。
  alert_category: {
    precheck_failed: '备份检查未通过', upload_failed: '备份上传失败', upload_commit_failed: '存入备份库失败',
    size_abnormal: '备份大小不对', backup_missed: '到点没有备份', verification_failed: '备份复查未通过',
    restore_verify_failed: '恢复前复查未通过', client_offline: '客户端离线', client_resource: '客户端资源吃紧',
    client_enrollment: '客户端登记', certificate_expiry: '证书即将到期', service_state: '被监控的服务状态异常',
    server_storage_low: '服务端磁盘不足', storage: '备份库异常',
    retention_delete_failed: '过期备份删除失败', retention_breaker_tripped: '保留清理已自动停手',
    // 以下这批此前漏在清单外，于是在告警列表里直接显示成英文标识。
    // 新增告警类别时，这里和服务端的 AlertCategoryCatalog 要一起加。
    upload_failed: '备份上传失败', server_storage_unavailable: '服务端存储不可用',
    agent_config_stale: '客户端一直没拿到新配置',
    retention_policy_invalid: '保留策略一条规则都没有', agent_version_drift: '客户端版本落后',
    config_backup_overdue: '配置备份超期未导出', notification_channel_failed: '通知渠道发不出去',
    server_certificate_expiring: '服务端 TLS 证书即将到期', client_ca_expiring: '客户端 CA 即将到期',
    client_certificate_truncated: '客户端证书被 CA 截短',
    execution_item_timeout: '备份计划里的项目执行超时',
    execution_item_queue_timeout: '备份计划里的项目一直没能开始',
    system: '系统（升级下发等）'
  }
};

/* ── §6.1 状态标记：形状+文字+颜色三重编码。
   ok=● 绿 / busy=◐ 蓝 / wait=○ 琥珀 / err=▲ 红 / off=⊘ 灰 / mut=○ 灰。
   pill（带背景胶囊）只留给真正异常的状态——颜色必须保留报警能力。 ── */
const STATUS_MAP = {
  online: ['ok'], suspected_offline: ['wait'], pending_approval: ['wait'],
  offline: ['err', 'pill'], disabled: ['off'], revoked: ['off'], certificate_expired: ['err', 'pill'],
  available: ['ok'], verifying: ['busy'], verification_failed: ['err', 'pill'],
  quarantined: ['err', 'pill'], recycle_bin: ['wait'], deleted: ['off'],
  requested: ['wait'], ready: ['wait'], downloading: ['busy'],
  completed: ['ok'], failed: ['err', 'pill'], expired: ['off'],
  critical: ['err', 'pill'], warning: ['wait'], notice: ['mut'],
  // 告警列表里「等级」和「状态」并排，原先 critical 和 open 都是红胶囊，
  // 一行两个一模一样的红块——重复三遍的警报等于没有警报。
  // 红色留给等级（它才回答"有多严重"），状态只回答"处理到哪一步了"，用琥珀点。
  open: ['wait'], acknowledged: ['busy'], in_progress: ['busy'],
  recovered: ['ok'], closed: ['off'], ignored: ['off'],
  pending: ['wait'], sent: ['ok'],
  // 指令状态：没有映射的值会退成灰点，「成功」显示成灰的就失去了一眼可读的意义
  claimed: ['wait'], succeeded: ['ok'], rejected: ['err', 'pill'],
  created: ['wait'], waiting_permission: ['wait'], retry_wait: ['wait'],
  received: ['busy'], verified: ['ok'], committed: ['ok'],
  automatic: ['ok'], approval_required: ['busy'], manual: ['mut'], monitor_only: ['mut'], paused: ['wait'],
  success: ['ok'], failure: ['err', 'pill'],
  running: ['busy'], partial: ['wait'], cancelled: ['off'],
  healthy: ['ok'], unhealthy: ['err', 'pill'], degraded: ['wait'],
  uploading: ['busy', 'pill'], working: ['busy'], idle: ['mut'],
  not_scanned: ['mut'], passed: ['ok'], still_changing: ['wait'],
  no_new_backup: ['wait'], required_file_missing: ['err', 'pill'], path_not_found: ['err', 'pill'],
  size_abnormal: ['err', 'pill'], access_denied: ['err', 'pill']
};
export function status(group, val) {
  const label = (L[group] && L[group][val]) || val || '—';
  const m = STATUS_MAP[val] || ['mut'];
  return `<span class="status status--${m[0]}${m[1] === 'pill' ? ' pill' : ''}">${esc(label)}</span>`;
}

/* ── 格式化 ── */
/* 有效位数随量级走（实施方案 U3）：998 MB / 12.4 GB / 1.24 TB。

   原先一律两位小数。「6.24 TB」里最后那一位既没有决策价值，又把数字拉长、
   把量级本身冲淡了——而在一屏全是数字的运维界面上，量级才是要一眼读到的东西。 */
function scaleBytes(n) {
  const u = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0, v = Number(n);
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
  const digits = i === 0 ? 0 : v >= 100 ? 0 : v >= 10 ? 1 : 2;
  return { value: v.toFixed(digits), unit: u[i] };
}

/* 纯文本版本，语义与从前一致（可以安全地进 esc()、textContent、title 属性）。
   全站 40 多处调用都走这里，不改契约。 */
export function fmtBytes(n) {
  if (n == null) return '—';
  const { value, unit } = scaleBytes(n);
  return `${value} ${unit}`;
}

/* HTML 版本：单位降一号字、用次要色。只用在确认是 innerHTML 的展示点——
   关键指标（--t-num 28px）用上之后，「**6.2** TB」和「6.24 TB」差的
   正好是那一点「贵」的感觉。刻意不去动 fmtBytes 本身：
   它有 40 多个调用点，其中有进 esc() 的、有进属性的，改契约必然漏掉一个，
   而漏掉的表现是界面上直接显示出 <span> 源码。 */
export function fmtBytesHtml(n) {
  if (n == null) return '—';
  const { value, unit } = scaleBytes(n);
  return `${value}<span class="unit">${unit}</span>`;
}
/* 速度和剩余时间：算不出来的时候一律给 —— 而不是 0。
   「0 B/s」和「剩余 0 秒」看起来都像是个结论，实际是「不知道」，
   而在一次慢传输里，人正是靠这两个数判断该不该去查网络。 */
export function fmtRate(bps) {
  if (bps == null) return '—';
  return fmtBytes(bps) + '/s';
}
export function fmtDuration(sec) {
  if (sec == null) return '—';
  sec = Math.max(0, Math.round(Number(sec)));
  if (sec < 60) return `${sec} 秒`;
  if (sec < 3600) return `${Math.round(sec / 60)} 分钟`;
  if (sec < 86400) {
    const h = Math.floor(sec / 3600);
    const m = Math.round((sec % 3600) / 60);
    return m ? `${h} 小时 ${m} 分` : `${h} 小时`;
  }
  const d = Math.floor(sec / 86400);
  // 超过一个月的预估没有参考价值，给个上界比给个精确的假数字诚实。
  if (d > 30) return '超过 30 天';
  const h = Math.round((sec % 86400) / 3600);
  return h ? `${d} 天 ${h} 小时` : `${d} 天`;
}

/* 传输进度条。数字要和条子放在一起：光有条子读不出还差多少，
   光有百分比看不出这一批里哪条最拖后腿。 */
export function xferBar(percent, stalled) {
  const p = Math.max(0, Math.min(100, Number(percent) || 0));
  return `<div class="xfer-bar${stalled ? ' is-stalled' : ''}" role="progressbar" aria-valuenow="${p.toFixed(1)}" aria-valuemin="0" aria-valuemax="100"><span style="width:${p.toFixed(2)}%"></span></div>`;
}

/* 速率走势线（实施方案 U1）。48×14 的内联 SVG，一条 polyline。

   刻意不引图表库：任何一个库为了这条线带进来的都是几十 KB 加一套自己的主题系统，
   而这里要画的只有「一行数据的一分钟趋势」。

   纵轴按本行自身的最大值归一化，不做跨行统一刻度：这条线要回答的是
   「这一条传输自己在变快还是变慢」，不是「哪一条最快」——后者是「速度」列的事。

   少于两点时给一个横杠。补零画出的曲线会让刚开始的传输看起来像刚刚提速，
   那是在界面上编造没有采样到的数据。 */
export function sparkline(values, { width = 48, height = 14 } = {}) {
  if (!Array.isArray(values) || values.length < 2)
    return '<span class="spark-empty" aria-hidden="true">—</span>';

  const max = Math.max(...values, 1);
  const step = width / (values.length - 1);
  const points = values
    .map((v, i) => `${(i * step).toFixed(1)},${(height - (Number(v) / max) * height).toFixed(1)}`)
    .join(' ');

  // 趋势用文字补给读屏与悬停：SVG 本身是纯视觉重复（速率数字就在旁边），标 aria-hidden。
  const head = values.slice(0, Math.max(1, Math.floor(values.length / 3)));
  const tail = values.slice(-Math.max(1, Math.floor(values.length / 3)));
  const avg = xs => xs.reduce((n, x) => n + Number(x), 0) / xs.length;
  const ratio = avg(head) > 0 ? avg(tail) / avg(head) : 1;
  const trend = ratio > 1.25 ? '正在变快' : ratio < 0.75 ? '正在变慢' : '基本平稳';

  return `<svg class="spark" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}"`
    + ` role="img" aria-hidden="true" focusable="false"><title>最近一分钟：${trend}</title>`
    + `<polyline points="${points}"/></svg>`;
}

export function fmtDT(v) {
  if (!v) return '—';
  const d = new Date(v);
  if (isNaN(d)) return esc(v);
  const p = x => String(x).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}
/* §6.2 相对时间：title 挂绝对时间；超过 7 天回退到日期 */
export function relTime(v) {
  if (!v) return '—';
  const d = new Date(v);
  if (isNaN(d)) return esc(v);
  const abs = fmtDT(v);
  const diff = Date.now() - d.getTime();
  if (diff < 0) return `<time title="${abs}">${abs}</time>`;
  const m = Math.floor(diff / 60000);
  if (m < 1) return `<time title="${abs}">刚刚</time>`;
  if (m < 60) return `<time title="${abs}">${m} 分钟前</time>`;
  const h = Math.floor(m / 60);
  if (h < 24) return `<time title="${abs}">${h} 小时前</time>`;
  const dd = Math.floor(h / 24);
  if (dd <= 7) return `<time title="${abs}">${dd} 天前</time>`;
  const p = x => String(x).padStart(2, '0');
  return `<time title="${abs}">${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}</time>`;
}
/* 相对时间的纯文字版本（不带标签），供「绝对时间 + 相对时间」并排的单元格复用。
   与 relTime 的差别只有一处：超过 7 天不回退成日期，仍然说「23 天前」——
   在 absTime 里日期已经摆在主位了，这里再重复一遍没有意义。 */
export function relText(v) {
  if (!v) return '';
  const d = new Date(v);
  if (isNaN(d)) return '';
  const diff = Date.now() - d.getTime();
  if (diff < 0) return '';
  const m = Math.floor(diff / 60000);
  if (m < 1) return '刚刚';
  if (m < 60) return `${m} 分钟前`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h} 小时前`;
  return `${Math.floor(h / 24)} 天前`;
}

/* 绝对时间为主、相对时间为辅。
   「3 分钟前」一眼能看出新鲜度，但要跟服务端日志、Windows 事件查看器、
   告警时间对照时它用不了——人得先在心里做一次减法，而那一步最容易错，
   排障时错一次就是找错方向。所以列表里把绝对时间摆在主位，
   相对时间降为灰字提示；title 仍然是带秒的完整时间。
   当年的时间省掉年份（列宽有限），跨年的补上。 */
/* 两个时间里更晚的那个，都为空时返回 null。

   「这台机器最后一次有动静是什么时候」要看的是最近一次**通信**，不只是心跳：
   领指令、报扫描进度、传分块——每一次都比心跳更能说明客户端活着，而心跳只是
   每分钟一次的例行汇报。只认心跳会把一台正在传 5 GB 备份、心跳被大文件哈希
   拖住的机器显示成很久没消息，而它正在好好干活。

   客户端列表和概览页用的必须是同一套口径，所以放在这里而不是各写各的。 */
export function laterOf(a, b) {
  if (!a) return b || null;
  if (!b) return a;
  return new Date(a) > new Date(b) ? a : b;
}

export function absTime(v) {
  if (!v) return '—';
  const d = new Date(v);
  if (isNaN(d)) return esc(v);
  const p = x => String(x).padStart(2, '0');
  const md = `${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
  const main = d.getFullYear() === new Date().getFullYear() ? md : `${d.getFullYear()}-${md}`;
  const rel = relText(v);
  return `<time title="${fmtDT(v)}">${main}</time>${rel ? ` <span class="hint">${rel}</span>` : ''}`;
}

export function shortId(g) { return g ? String(g).slice(0, 8) : '—'; }

/* 客户端在界面上的称呼。
   显示名是装机时人填的（「财务服务器」），主机名是机器生成的（WIN-CLS1Q76D08E）。
   要认出是哪台机器，人靠的是前者；后者只在重名时用来消歧。
   此前全站一律把主机名摆在主位、显示名当小字，等于把人填的名字架空了。 */
export function clientName(c) {
  const display = (c && (c.displayName || c.display_name) || '').trim();
  const host = (c && c.hostname || '').trim();
  return display || host || '—';
}

/* 带消歧后缀的完整称呼，用于下拉这类没有副标题位置的地方。 */
export function clientLabel(c) {
  const display = (c && (c.displayName || c.display_name) || '').trim();
  const host = (c && c.hostname || '').trim();
  return display && host && display !== host ? `${display}（${host}）` : (display || host || '—');
}
/* ── 客户端能不能动：一处判定，列表 / 详情 / 批量条共用 ──

   此前每个按钮都是无条件画出来的，于是有三类白点：
   离线的机器点「刷新指标」，指令只是进队列，24 小时后静默过期，界面还弹「已下发」；
   已禁用/已注销的机器点「刷新指标」「注销」，服务端直接 409；
   正在上传的机器点「上传」，服务端以 CLIENT_BUSY 回绝——服务端本来就在拦，
   界面却还让人点。判定依据必须和服务端一致，否则就是「按钮亮着，点了报错」。

   两条轴：连接状态（连不连得上）× 运行状态（现在忙不忙）。
   每个 can* 都配一句 why——变灰而不说为什么，比不变灰更让人困惑。 */
export function clientCaps(c) {
  const st = c && c.status;
  const runtime = (c && c.runtimeState) || 'idle';
  const busy = runtime !== 'idle';
  const busyWhy = runtime === 'uploading'
    ? '这台客户端正在上传备份，等它传完再操作'
    : '这台客户端正在执行指令，等它跑完再操作';
  const unreachableWhy = {
    pending_approval: '客户端还没审批通过，不能下发指令',
    offline: '客户端离线，指令下发了也没人执行',
    certificate_expired: '客户端证书已过期，连不上服务端',
    disabled: '客户端已禁用，不能下发指令',
    revoked: '客户端已注销，不能下发指令'
  }[st] || '';
  // 疑似离线只是漏了一次心跳，Agent 多半还活着，指令排队等它回来是合理的——
  // 这里不拦，只在提示里说清楚。
  const reachable = st === 'online' || st === 'suspected_offline';
  // 禁用/注销和「连不连得上」无关：离线的机器照样可以禁用。
  // 待审批的用「拒绝」，已禁用/已注销的服务端会 409。
  const manageable = st !== 'pending_approval' && st !== 'disabled' && st !== 'revoked';
  const dispatchWhy = reachable
    ? (st === 'suspected_offline' ? '客户端心跳已中断，指令会排队等它恢复' : '')
    : unreachableWhy;

  return {
    runtime, busy,
    canApprove: st === 'pending_approval',
    canDispatch: reachable,
    canUpload: reachable && !busy,
    canDisable: manageable && !busy,
    canEnable: st === 'disabled',
    canRevoke: st !== 'revoked' && st !== 'pending_approval' && !busy,
    whyDispatch: dispatchWhy,
    whyUpload: busy ? busyWhy : dispatchWhy,
    whyDisable: busy ? busyWhy : (st === 'disabled' ? '客户端已禁用' : st === 'revoked' ? '客户端已注销' : st === 'pending_approval' ? '待审批的客户端请用「拒绝」' : ''),
    whyEnable: st === 'revoked' ? '注销不可恢复，需要在客户端机器上重新登记' : '',
    whyRevoke: busy ? busyWhy : (st === 'revoked' ? '客户端已注销' : st === 'pending_approval' ? '待审批的客户端请用「拒绝」' : '')
  };
}

/* 「正在干活」的标记。空闲时什么都不画——给每一行都挂一个"空闲"标签，
   等于把真正需要注意的那两行淹掉。 */
export function runtimeBadge(c) {
  const rt = (c && c.runtimeState) || 'idle';
  if (rt === 'idle') return '';
  const detail = rt === 'uploading'
    ? `正在上传 ${c.activeUploadCount || 0} 个备份集`
    : `有 ${c.runningCommandCount || 0} 条指令正在执行`;
  // 「上传中」做成到「传输中」页的链接：看到这个徽标的人，下一个问题必然是
  // 「传到哪了、还要多久」，而那个答案在另一个页面上。
  return rt === 'uploading'
    ? ` <a href="#/transfers" title="${esc(detail)}：查看传输进度">${status('client_runtime', rt)}</a>`
    : ` <span title="${esc(detail)}">${status('client_runtime', rt)}</span>`;
}

/* 客户端 IP 有两种含义，界面上必须分开说，否则人分不清"机器上有这个地址"和"我们在用这个地址"：
   lastRemoteIp 是服务端实际收到心跳的那个对端地址，每次心跳刷新，必然连得通；
   ipAddresses 是 Agent 自报的本机网卡列表，能看出多网卡和网段，但只在列表变化时才重报。

   列表里显示的是服务端算好的 ipv4Address：看这一列的人下一步是照着它去 ping、去远程桌面，
   而对端地址很可能是 fe80:: 链路本地地址（Windows 名称解析常把 Agent 领到 IPv6 上去），
   那串东西拿到手上什么也做不了。真实对端地址不丢，收进 title 里并说明这个 IPv4 是哪来的。 */
/* 版本号的「核心」部分：去掉 +提交号 / -预发布 后缀。

   随附版本号是从发布出来的 exe 上取的 ProductVersion，长这样：
   "1.3.2+8d1d486fd1cbe72525c73b979034c0700b16e019"。而客户端自报的是 "1.3.2"。
   拿原串直接比，两个 1.3.2 会被判成不相等；直接显示，界面上会糊上 40 位提交号。
   服务端 AgentVersionService.TryParse 用的就是这同一条规则（按 '+' 和 '-' 截断）。 */
export function versionCore(v) {
  return (v || '').split('+')[0].split('-')[0].trim();
}

/* 「最近通信」单元格。客户端列表和概览页共用同一个实现。

   时间用 absTime 而不是 relTime：这一列是拿来跟日志、告警时间对照的，
   而「刚刚」「3 分钟前」在对照的时候要求人先做一次减法——
   一整列都是「刚刚」的时候，它等于什么都没说。

   心跳明显落后于最近一次通信时，把两个时间都说出来：
   「09-12 00:45（心跳 6 分钟前）」——那多半是客户端正忙着算校验和，
   是它在干活，不是它出了问题。 */
export function lastSeenCell(r) {
  const seen = laterOf(r.lastSeenAt, r.lastHeartbeatAt);
  if (!seen) return absTime(null);
  const lagged = r.lastHeartbeatAt && seen !== r.lastHeartbeatAt
    && new Date(seen) - new Date(r.lastHeartbeatAt) > 120000;
  return lagged
    ? `${absTime(seen)}<span class="hint">（心跳 ${esc(relText(r.lastHeartbeatAt))}）</span>`
    : absTime(seen);
}

/* 系统列。显示的是**版本**，不是 osName。

   osName 在 Windows 机器上永远是同一个词，一整列都写着「Windows」等于没有信息；
   而「哪几台还停在 2012 R2」是排查兼容问题时第一个要回答的问题，答案在 osVersion 里。

   去掉 "Microsoft " 前缀只为省列宽，完整原串留在 title 里，鼠标停上去能看全。
   客户端列表和概览页共用这一个实现——同一台机器在两个页面上写着不同的系统名，
   会让人以为是两台机器。 */
/* 内核版本号 → 产品名。

   老客户端（1.3.1 及以前）只会上报 OsName="Windows" 和
   OsVersion=Environment.OSVersion.VersionString，也就是 "Microsoft Windows NT 10.0.14393.0"——
   一整列全是内核版本号，而人要回答的是「哪几台还停在 2012 R2」。这张表把它翻译回来。

   已知的不精确：10.0.x 这些内核版本号在服务器版和桌面版之间是共用的
   （14393 既是 Server 2016 也是 Win10 1607），光看版本号分不出来。这里按服务器版翻译，
   因为这是个给服务器做备份的产品。新版客户端会直接上报注册表里的产品全名，
   到那时走的是 osName 分支，不再需要猜。原始串始终留在 title 里，没有信息被藏起来。 */
const WINDOWS_BUILD_NAMES = {
  '10.0.26100': 'Windows Server 2025',
  '10.0.25398': 'Windows Server 23H2',
  '10.0.20348': 'Windows Server 2022',
  '10.0.17763': 'Windows Server 2019',
  '10.0.14393': 'Windows Server 2016',
  '6.3': 'Windows Server 2012 R2',
  '6.2': 'Windows Server 2012',
  '6.1': 'Windows Server 2008 R2',
  '6.0': 'Windows Server 2008'
};

function windowsNameFromVersion(version) {
  const m = /(\d+)\.(\d+)(?:\.(\d+))?/.exec(version || '');
  if (!m) return null;
  const [, major, minor, build] = m;
  return WINDOWS_BUILD_NAMES[`${major}.${minor}.${build}`]
      || WINDOWS_BUILD_NAMES[`${major}.${minor}`]
      || null;
}

/* 系统列。

   优先用 osName——新版客户端上报的是注册表里的产品全名（"Windows Server 2016 Standard"）。
   老客户端那里 osName 只有一个「Windows」，等于没有信息，这时退回按内核版本号翻译。
   两条路都走不通才显示原始串。完整原串永远在 title 里。 */
export function osCell(c) {
  const name = (c.osName || '').trim();
  const version = (c.osVersion || '').trim();
  const full = [name, version].filter(Boolean).join(' ') || '—';
  if (full === '—') return '—';

  const informative = name && !/^(windows|linux|unknown)$/i.test(name);
  const text = informative
    ? name.replace(/^Microsoft\s+/i, '')
    : (windowsNameFromVersion(version) || version.replace(/^Microsoft\s+/i, '') || name);

  return `<span title="${esc(full)}">${esc(text || '—')}</span>`;
}

export function ipCell(c) {
  const peer = (c && c.lastRemoteIp) || '';
  const v4 = (c && c.ipv4Address) || '';
  if (!peer && !v4) return '<span class="sub">—</span>';

  // 老数据里对端地址可能还存着 IPv4 映射形式（::ffff:192.168.1.10）——服务端现在会还原，
  // 但已经写进库的那些不会自己变。判断来源前先按同一形状对齐，免得把它说成"来自网卡列表"。
  const peerAsV4 = peer.replace(/^::ffff:/i, '');
  if (!v4)
    return `<span class="mono" title="服务端最近一次收到心跳的对端地址。这台机器没有报告可用的 IPv4，只能按这个地址找它">${esc(peer)}</span>`;
  if (!peer)
    return `<span class="mono" title="来自 Agent 自报的网卡列表。服务端还没有收到过它的心跳">${esc(v4)}</span>`;

  const title = peerAsV4 === v4
    ? '服务端最近一次收到心跳的对端地址'
    : `服务端实际收到心跳的对端地址是 ${peer}，照着它找不到这台机器；这里显示的 IPv4 取自 Agent 自报的网卡列表`;
  return `<span class="mono" title="${esc(title)}">${esc(v4)}</span>`;
}

export function ipDetailRows(c) {
  const list = Array.isArray(c && c.ipAddresses) ? c.ipAddresses : [];
  const nics = list.length
    ? list.map(ip => `<span class="mono">${esc(ip)}</span>`).join('<br>')
    : '—';
  return `<div class="row"><div class="k">对端 IP</div><div class="v mono">${esc((c && c.lastRemoteIp) || '—')}<span class="sub">服务端最近一次收到心跳的地址</span></div></div>
        <div class="row"><div class="k">本机网卡地址</div><div class="v">${nics}</div></div>`;
}

/* 动作按钮。allowed=false 时不是把按钮藏起来，而是变灰 + title 说明原因——
   藏起来会让人以为功能不存在，转头去翻文档或者问人。 */
export function actBtn({ label, view, action, id, cls = '', allowed = true, why = '', hint = '', small = true }) {
  const klass = ((small ? 'small ' : '') + cls).trim();
  if (!allowed)
    return `<button${klass ? ` class="${esc(klass)}"` : ''} disabled title="${esc(why || '当前状态下不可用')}">${esc(label)}</button>`;
  return `<button${klass ? ` class="${esc(klass)}"` : ''} data-ui-action="act" data-view="${esc(view)}" data-action="${esc(action)}" data-id="${esc(id)}"${hint ? ` title="${esc(hint)}"` : ''}>${esc(label)}</button>`;
}

/* 溢出菜单。操作列此前把所有动作平铺出来，一行三四个按钮不分主次，
   最要命的是「注销」和「刷新指标」长得一样大、挨在一起——注销不可恢复。
   主操作留在外面，其余收进菜单：既压掉视觉噪声，也让危险动作多一次点击。

   用原生 <details> 而不是自己写开合状态：键盘可达、Esc 可关，不需要额外脚本。 */
export function actMenu(items, label = '更多操作') {
  const body = items.filter(Boolean).join('');
  if (!body) return '';
  return `<details class="act-menu"><summary class="small" title="${esc(label)}" aria-label="${esc(label)}">⋯</summary><div class="act-menu-pop">${body}</div></details>`;
}

/* 点别处就收起来。不做这件事的话，菜单会一直摊在那儿盖住下面几行。 */
document.addEventListener('click', e => {
  for (const menu of document.querySelectorAll('details.act-menu[open]'))
    if (!menu.contains(e.target)) menu.open = false;
});

export function prettyJson(s) {
  if (!s) return '—';
  try { return JSON.stringify(JSON.parse(s), null, 2); } catch (e) { return String(s); }
}
export function optsOf(map) { return Object.entries(map).map(([v, t]) => ({ v, t })); }

/* ── Toast ── */
export function toast(msg, type = 'info') {
  const el = document.createElement('div');
  el.className = 'toast ' + type;
  el.setAttribute('role', type === 'err' ? 'alert' : 'status');
  el.textContent = msg;
  $('#toasts').appendChild(el);
  setTimeout(() => el.remove(), 4200);
}
export function errToast(e) { toast(e && e.message ? e.message : String(e), 'err'); }

/* ── 扫描计划：从手写 cron 改成选择 ──

   `0 2 * * *` 这种东西没人愿意每次都想一遍，而写错了也没有即时反馈：
   服务端只能告诉你「不是合法 cron」，告诉不了你「你想要每天两点，但写成了每两小时」。
   段数记反（把秒当第一段）、星期写成 1-7、日和周同时限定，都是常见错法。

   这里把它拆成「多久一次 + 几点」两个选择器，cron 由界面合成，天生合法；
   真需要写复杂表达式的人，选「自己写 cron」照旧。 */

const CRON_DOW = [['1', '周一'], ['2', '周二'], ['3', '周三'], ['4', '周四'], ['5', '周五'], ['6', '周六'], ['0', '周日']];
const CRON_FREQ = [
  ['', '不自动扫描（只在需要时手动点「立即备份」）'],
  ['hourly', '每小时'],
  ['daily', '每天'],
  ['weekly', '每周'],
  ['monthly', '每月'],
  ['raw', '自己写 cron 表达式']
];
const cronPad = n => String(n).padStart(2, '0');

/* 已存的表达式 → 界面上的几个选择。认不出来的一律落到「自己写」，不猜。 */
function cronParse(expr) {
  const base = { freq: '', minute: '0', time: '02:00', dow: '1', dom: '1', raw: '' };
  const text = String(expr || '').trim();
  if (!text) return base;
  const raw = Object.assign({}, base, { freq: 'raw', raw: text });
  const f = text.split(/\s+/);
  if (f.length !== 5) return raw;
  const num = v => (/^\d+$/.test(v) ? Number(v) : null);
  const [m, h, dom, mon, dow] = f;
  const mn = num(m);
  if (mn === null || mn > 59 || mon !== '*') return raw;
  if (h === '*' && dom === '*' && dow === '*')
    return Object.assign({}, base, { freq: 'hourly', minute: String(mn) });
  const hn = num(h);
  if (hn === null || hn > 23) return raw;
  const time = `${cronPad(hn)}:${cronPad(mn)}`;
  if (dom === '*' && dow === '*') return Object.assign({}, base, { freq: 'daily', time });
  const dowN = num(dow);
  if (dom === '*' && dowN !== null && dowN <= 7)
    return Object.assign({}, base, { freq: 'weekly', time, dow: String(dowN % 7) });
  const domN = num(dom);
  if (dow === '*' && domN !== null && domN >= 1 && domN <= 31)
    return Object.assign({}, base, { freq: 'monthly', time, dom: String(domN) });
  return raw;
}

function cronCompose(s) {
  const parts = String(s.time || '02:00').split(':');
  const hh = String(Number(parts[0]) || 0);
  const mm = String(Number(parts[1]) || 0);
  switch (s.freq) {
    case 'hourly': return `${Number(s.minute) || 0} * * * *`;
    case 'daily': return `${mm} ${hh} * * *`;
    case 'weekly': return `${mm} ${hh} * * ${s.dow}`;
    case 'monthly': return `${mm} ${hh} ${s.dom} * *`;
    case 'raw': return String(s.raw || '').trim();
    default: return '';
  }
}

function cronEcho(s, expr) {
  if (!expr) return '不自动扫描。任务仍然可以在列表里手动点「立即备份」';
  const tz = '（按客户端所在时区）';
  const dowText = (CRON_DOW.find(d => d[0] === String(s.dow)) || ['', ''])[1];
  let text;
  switch (s.freq) {
    case 'hourly': text = `每小时的第 ${Number(s.minute) || 0} 分钟扫描一次`; break;
    case 'daily': text = `每天 ${s.time} 扫描`; break;
    case 'weekly': text = `每${dowText} ${s.time} 扫描`; break;
    case 'monthly': text = `每月 ${s.dom} 号 ${s.time} 扫描`; break;
    default: return `cron：${expr}${tz}`;
  }
  return `${text}${tz} · ${expr}`;
}

/* 详情页把扫描计划也说成人话——列表和详情读到的是同一段表达式，
   没理由一边给选择器、另一边还是甩一串 `0 2 * * *`。 */
export function describeCron(expr) {
  const s = cronParse(expr);
  return cronEcho(s, String(expr || '').trim());
}

function cronFieldHtml(name, value) {
  const s = cronParse(value);
  const opt = (list, cur) => list.map(([v, t]) =>
    `<option value="${esc(v)}" ${String(v) === String(cur) ? 'selected' : ''}>${esc(t)}</option>`).join('');
  const range = (from, to, suffix) => {
    const out = [];
    for (let i = from; i <= to; i++) out.push([String(i), i + suffix]);
    return out;
  };
  const minutes = [];
  for (let i = 0; i < 60; i += 5) minutes.push([String(i), `第 ${i} 分`]);
  return `<div class="cronfield" data-cron>
    <input type="hidden" name="${esc(name)}" value="${esc(cronCompose(s))}">
    <div class="cronrow">
      <select data-cron-freq aria-label="扫描频率">${opt(CRON_FREQ, s.freq)}</select>
      <select data-cron-dow aria-label="星期几">${opt(CRON_DOW, s.dow)}</select>
      <select data-cron-dom aria-label="每月几号">${opt(range(1, 31, ' 号'), s.dom)}</select>
      <input type="time" data-cron-time value="${esc(s.time)}" aria-label="扫描时刻">
      <select data-cron-minute aria-label="每小时第几分钟">${opt(minutes, s.minute)}</select>
      <input type="text" class="mono" data-cron-raw value="${esc(s.raw)}" placeholder="0 2 * * *" aria-label="cron 表达式">
    </div>
    <div class="hint" data-cron-echo></div>
  </div>`;
}

/* 把选择同步进隐藏的 input——readValues 按 name 取值，不需要为此改取值逻辑。 */
export function initCronFields(root) {
  root.querySelectorAll('[data-cron]').forEach(box => {
    const hidden = box.querySelector('input[type="hidden"]');
    const freq = box.querySelector('[data-cron-freq]');
    const parts = {
      dow: box.querySelector('[data-cron-dow]'),
      dom: box.querySelector('[data-cron-dom]'),
      time: box.querySelector('[data-cron-time]'),
      minute: box.querySelector('[data-cron-minute]'),
      raw: box.querySelector('[data-cron-raw]')
    };
    const shown = {
      '': [],
      hourly: ['minute'],
      daily: ['time'],
      weekly: ['dow', 'time'],
      monthly: ['dom', 'time'],
      raw: ['raw']
    };
    const sync = () => {
      const state = {
        freq: freq.value, dow: parts.dow.value, dom: parts.dom.value,
        time: parts.time.value, minute: parts.minute.value, raw: parts.raw.value
      };
      const visible = shown[state.freq] || [];
      Object.keys(parts).forEach(k => { parts[k].hidden = !visible.includes(k); });
      const expr = cronCompose(state);
      hidden.value = expr;
      box.querySelector('[data-cron-echo]').textContent = cronEcho(state, expr);
    };
    box.addEventListener('change', sync);
    box.addEventListener('input', sync);
    sync();
  });
}

/* 字节量输入：数值 + 单位下拉，隐藏 input 里存的仍然是字节。
   原来只给一个「字节」输入框，"上限 50 GB" 要人自己按 1024 连乘三次再填进去；
   算错一位就是十倍或十分之一的阈值，而这个数直接决定告不告警——
   偏大时永不触发（等于没配），偏小时天天误报（然后人把整个大小检查关掉）。 */
const BYTE_UNITS = [[1, 'B'], [1024, 'KB'], [1048576, 'MB'], [1073741824, 'GB'], [1099511627776, 'TB']];

/* 回显时选能整除的最大单位：存进去的 53687091200 要显示成 50 GB，
   否则人下次打开表单看到的还是那串数字，等于白改。 */
export function splitBytes(value) {
  const n = value === '' || value == null ? NaN : Number(value);
  if (!Number.isFinite(n) || n < 0) return { num: '', unit: 1073741824 };
  if (n === 0) return { num: '0', unit: 1 };
  let pick = BYTE_UNITS[0];
  for (const u of BYTE_UNITS) if (n % u[0] === 0) pick = u;
  return { num: String(n / pick[0]), unit: pick[0] };
}

export function composeBytes(num, unit) {
  const n = String(num).trim();
  if (n === '') return '';
  const v = Number(n);
  if (!Number.isFinite(v) || v < 0) return '';
  return String(Math.round(v * Number(unit)));
}

export function bytesFieldHtml(name, value, placeholder) {
  const s = splitBytes(value);
  const opts = BYTE_UNITS.map(([v, t]) =>
    `<option value="${v}" ${v === s.unit ? 'selected' : ''}>${t}</option>`).join('');
  return `<div class="bytesfield" data-bytes>
    <input type="hidden" name="${esc(name)}" value="${esc(value == null ? '' : value)}">
    <div class="bytesrow">
      <input type="number" min="0" step="any" data-bytes-num value="${esc(s.num)}"
             placeholder="${esc(placeholder || '留空表示不检查')}" aria-label="数值">
      <select data-bytes-unit aria-label="单位">${opts}</select>
    </div>
    <div class="hint" data-bytes-echo></div>
  </div>`;
}

/* 同步进隐藏 input——readValues 按 name 取值，取到的还是字节，服务端那头一点不用动。 */
export function initBytesFields(root) {
  root.querySelectorAll('[data-bytes]').forEach(box => {
    const hidden = box.querySelector('input[type="hidden"]');
    const num = box.querySelector('[data-bytes-num]');
    const unit = box.querySelector('[data-bytes-unit]');
    const echo = box.querySelector('[data-bytes-echo]');
    const sync = () => {
      const bytes = composeBytes(num.value, unit.value);
      hidden.value = bytes;
      // 把最终字节数摆出来：这是存进库、也是详情页会显示的那个数，
      // 人填完能自己确认一眼有没有点错单位。
      echo.textContent = bytes === '' ? '留空表示不检查' : `= ${Number(bytes).toLocaleString('en-US')} 字节`;
      hidden.dispatchEvent(new Event('input', { bubbles: true }));
    };
    box.addEventListener('input', ev => { if (ev.target !== hidden) sync(); });
    box.addEventListener('change', ev => { if (ev.target !== hidden) sync(); });
    sync();
  });
}

/* ── 表单类模态（详情类请用抽屉，§6.3） ── */
export function openModal(title, bodyHtml, opts = {}) {
  closeModal();
  const ov = document.createElement('div');
  ov.className = 'overlay';
  ov.id = 'overlay';
  ov.innerHTML = `<div class="modal ${opts.wide ? 'wide' : ''}" role="dialog" aria-modal="true">
    <h3>${esc(title)}</h3>
    <div class="mbody">${bodyHtml}</div>
    <div class="mfoot">
      <button data-close>关闭</button>
      ${opts.okText ? `<button class="primary" data-ok>${esc(opts.okText)}</button>` : ''}
    </div>
  </div>`;
  document.body.appendChild(ov);
  ov.addEventListener('click', ev => {
    // opts.persistent：多步流程（向导、要等客户端应答的选择器）里，点空白处误关一次，
    // 前面几步连同等了十几秒的结果一起没了，而人并没有表达过"我要放弃"。
    // 关闭仍然有两条明路——「关闭」按钮和 Esc，两者都是明确的动作。
    if (ev.target === ov && opts.persistent) return;
    if (ev.target === ov || ev.target.closest('[data-close]')) closeModal();
  });
  return ov;
}
/* target 可选：只关闭指定的那一层遮罩。
   向导式流程里，onSubmit 解决的 Promise 会让调用方在同一批微任务里打开下一个弹窗，
   随后 onSubmit 的调用点才执行 closeModal()——不指定目标的话，关掉的是刚打开的
   下一步，用户看到的就是「点下一步，窗口直接关回主页面」。 */
export function closeModal(target) {
  const o = target || $('#overlay');
  if (!o || !o.isConnected) return;
  // 先派发再移除：confirmModal 等待方靠这个事件把 Promise 落定为「取消」（P2-11）
  o.dispatchEvent(new CustomEvent('bm:dismiss'));
  o.remove();
}

export function confirmModal(msg) {
  return new Promise(resolve => {
    const ov = openModal('请确认', `<p style="line-height:1.7">${esc(msg)}</p>`, { okText: '确定' });
    let settled = false;
    const settle = value => {
      if (settled) return;
      settled = true;
      ov.removeEventListener('bm:dismiss', onDismiss);
      resolve(value);
    };
    // P2-11：原先只在点遮罩/关闭按钮时 resolve(false)，而全局 Esc 处理器走的是 closeModal()，
    // 于是按 Esc 关掉弹窗后 Promise 永不落定——所有 `if (!await confirmModal(...)) return;`
    // 的调用方会静默永久挂起，用户既得不到结果也没有任何提示。
    // closeModal 现在会派发 bm:dismiss，任何关闭途径都能让它落定为「取消」。
    const onDismiss = () => settle(false);
    ov.addEventListener('bm:dismiss', onDismiss);
    ov.querySelector('[data-ok]').addEventListener('click', () => { settle(true); closeModal(); });
    ov.addEventListener('click', ev => {
      if (ev.target === ov || ev.target.closest('[data-close]')) setTimeout(() => settle(false), 0);
    });
  });
}

/* 通用表单弹窗：fields=[{name,label,type,value,options,required,placeholder,hint,advanced}]
   advanced:true 的字段收进「高级选项」折叠区。这类字段都带可用默认值，
   平时不该出现在视野里——一次性铺开十几个输入框会让「必须填什么」这件事消失，
   使用者只能逐个猜。取值逻辑不受影响：折叠区里的控件同样在 DOM 中。

   type='static' 是只读展示块（html 字段直接插入），用来放实时预览这类不参与取值的内容。
   type='cron' 是扫描计划组合控件（频率 + 时刻），取值时拿到的是合成好的 cron 表达式。
   type='bytes' 是字节量组合控件（数值 + 单位），取值时拿到的是换算好的字节数。
   opts.onMount(ov, readValues) 在渲染完成后调用一次，给需要联动的表单挂事件。
   opts.validate(vals) 返回一句话表示"这组值不能一起提交"，返回空表示通过——
   单字段的必填在下面已经管了，跨字段的约束（比如时间窗口要么都填要么都空）放这里。 */
export function formModal(title, fields, onSubmit, okText = '保存', opts = {}) {
  const renderField = f => {
    let input;
    const v = f.value != null ? f.value : '';
    if (f.type === 'static') {
      return `<div class="frow" data-static="${esc(f.name)}">${f.label ? `<label>${esc(f.label)}</label>` : ''}${f.html || ''}${f.hint ? `<div class="hint">${esc(f.hint)}</div>` : ''}</div>`;
    }
    if (f.type === 'cron') {
      input = cronFieldHtml(f.name, v);
    } else if (f.type === 'bytes') {
      input = bytesFieldHtml(f.name, f.value == null ? '' : f.value, f.placeholder);
    } else if (f.type === 'select') {
      input = `<select name="${f.name}">${(f.options || []).map(o =>
        `<option value="${esc(o.v)}" ${String(o.v) === String(v) ? 'selected' : ''}>${esc(o.t)}</option>`).join('')}</select>`;
    } else if (f.type === 'textarea') {
      input = `<textarea name="${f.name}" placeholder="${esc(f.placeholder || '')}" ${f.rows ? `rows="${f.rows}"` : ''}>${esc(v)}</textarea>`;
    } else if (f.type === 'checkbox') {
      input = `<label class="frow inline" style="margin:0"><input type="checkbox" name="${f.name}" ${v ? 'checked' : ''}>${esc(f.labelText || '启用')}</label>`;
    } else {
      input = `<input type="${f.type || 'text'}" name="${f.name}" value="${esc(v)}" placeholder="${esc(f.placeholder || '')}" ${f.type === 'number' ? 'step="any"' : ''}>`;
    }
    return `<div class="frow"><label>${esc(f.label)}${f.required ? ' *' : ''}</label>${input}${f.hint ? `<div class="hint">${esc(f.hint)}</div>` : ''}</div>`;
  };
  const basic = fields.filter(f => !f.advanced);
  const advanced = fields.filter(f => f.advanced);
  const html = basic.map(renderField).join('')
    + (advanced.length
      ? `<details class="fadv"><summary>高级选项（${advanced.length} 项，均有默认值）</summary>${advanced.map(renderField).join('')}</details>`
      : '');
  const ov = openModal(title, html, { okText, wide: opts.wide, persistent: opts.persistent });
  initCronFields(ov);
  initBytesFields(ov);

  const readValues = () => {
    const vals = {};
    for (const f of fields) {
      if (f.type === 'static') continue;
      const el = ov.querySelector(`[name="${f.name}"]`);
      if (!el) continue;
      vals[f.name] = f.type === 'checkbox' ? el.checked : el.value.trim();
    }
    return vals;
  };

  if (typeof opts.onMount === 'function') opts.onMount(ov, readValues);

  ov.querySelector('[data-ok]').addEventListener('click', async () => {
    const vals = readValues();
    for (const f of fields) {
      if (f.type === 'static' || !f.required) continue;
      const el = ov.querySelector(`[name="${f.name}"]`);
      if (!el) continue;
      if (vals[f.name] === '' || vals[f.name] == null) {
        // 字段可能收在折叠区里；不展开的话提示指向一个看不见的输入框。
        el.closest('details')?.setAttribute('open', '');
        el.focus();
        toast(`「${f.label}」为必填项`, 'err'); return;
      }
    }
    if (typeof opts.validate === 'function') {
      const problem = opts.validate(vals);
      if (problem) {
        // 跨字段约束多半落在高级选项里，不展开的话人看不到自己被说的是哪一项。
        ov.querySelector('details.fadv')?.setAttribute('open', '');
        toast(problem, 'err');
        return;
      }
    }
    const btn = ov.querySelector('[data-ok]');
    btn.disabled = true;
    try { await onSubmit(vals); closeModal(ov); }
    catch (e) { errToast(e); btn.disabled = false; }
  });

  // 返回遮罩层，调用方可以监听 bm:dismiss 把「关掉了」和「提交了」区分开。
  return ov;
}

/* ── §6.3 详情抽屉：右侧滑入 / focus trap / Esc / 焦点归还 ── */
let drawerEl = null, drawerRestore = null, drawerOnClose = null;
function escCloseDrawer(e) { if (e.key === 'Escape') closeDrawer(); }
function trapTab(e) {
  if (e.key !== 'Tab' || !drawerEl) return;
  const f = [...drawerEl.dr.querySelectorAll('button,a[href],input,select,textarea,[tabindex]:not([tabindex="-1"])')].filter(x => !x.disabled && x.offsetParent !== null);
  if (!f.length) return;
  const first = f[0], last = f[f.length - 1];
  if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
  else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
}
export function openDrawer({ title, bodyHtml, wide = false, onClose = null }) {
  closeDrawer(true);
  drawerRestore = document.activeElement;
  drawerOnClose = onClose;
  const ov = document.createElement('div'); ov.className = 'drawer-ov';
  const dr = document.createElement('div');
  dr.className = 'drawer' + (wide ? ' wide' : '');
  dr.setAttribute('role', 'dialog'); dr.setAttribute('aria-modal', 'true');
  dr.innerHTML = `<div class="dhead"><h3>${esc(title)}</h3><button class="small" data-dclose aria-label="关闭抽屉">✕</button></div><div class="dbody">${bodyHtml}</div>`;
  document.body.appendChild(ov); document.body.appendChild(dr);
  requestAnimationFrame(() => { ov.classList.add('open'); dr.classList.add('open'); });
  ov.addEventListener('click', () => closeDrawer());
  dr.querySelector('[data-dclose]').addEventListener('click', () => closeDrawer());
  dr.addEventListener('keydown', trapTab);
  document.addEventListener('keydown', escCloseDrawer);
  drawerEl = { ov, dr };
  dr.querySelector('[data-dclose]').focus();
}
export function closeDrawer(skipOnClose = false) {
  if (!drawerEl) return;
  const { ov, dr } = drawerEl; drawerEl = null;
  document.removeEventListener('keydown', escCloseDrawer);
  const cb = drawerOnClose; drawerOnClose = null;
  ov.classList.remove('open'); dr.classList.remove('open');
  setTimeout(() => { ov.remove(); dr.remove(); }, 200);
  if (drawerRestore && drawerRestore.focus) drawerRestore.focus();
  drawerRestore = null;
  if (!skipOnClose && cb) cb();
}
export function drawerOpen() { return !!drawerEl; }

/* ── 轮询刷新用的行级增量更新（实施方案 W3）──────────────────────────────

   只在「同一张表每隔几秒刷新一次」的场景用；首屏、筛选、排序变化仍然走 tableHtml 整块重建。

   为什么值得单独写这三十行：整块 innerHTML 重建会把 DOM 节点全部换掉，于是
     一、CSS 过渡拿不到「上一个值」—— .xfer-bar span 上写着 transition:width，
         而它从来没有触发过一次，进度条一直是瞬移的；
     二、用户正在选中/准备复制的主机名被清空；
     三、hover、title 提示、键盘导航的 kbd-focus 全部重置。
   这四条都不是性能问题，是「界面看起来很廉价」的直接来源。

   刻意不引虚拟 DOM：这里要解决的只有「同构表格反复刷新」一个场景，
   一个按 key 的 map + insertBefore 就够，再多的抽象都是负债。 */
export function patchRows(tbody, rows, keyOf, renderCells, cols) {
  if (!tbody) return false;
  const existing = new Map([...tbody.children].map(tr => [tr.dataset.rowKey, tr]));
  let cursor = null;

  for (const row of rows) {
    const key = String(keyOf(row));
    let tr = existing.get(key);
    const cells = renderCells(row);

    if (tr && tr.children.length === cells.length) {
      existing.delete(key);
      // 只写内容真的变了的单元格。没变的 <td> 保持原节点不动——
      // 里面的 .xfer-bar 因此能拿到上一次的宽度，那条 transition 才有意义。
      for (let i = 0; i < cells.length; i++) {
        const td = tr.children[i];
        if (td && td.innerHTML !== cells[i]) td.innerHTML = cells[i];
      }
    } else {
      if (tr) existing.delete(key);
      tr = document.createElement('tr');
      tr.dataset.rowKey = key;
      tr.innerHTML = cells
        .map((html, i) => `<td${cols && cols[i] && cols[i].num ? ' class="num"' : ''}>${html}</td>`)
        .join('');
    }

    // 顺序对齐：服务端的排序会变（卡住的排最前），但整表重排会让所有行重新入场。
    // 只在位置真的不对时才 insertBefore。
    const next = cursor ? cursor.nextSibling : tbody.firstChild;
    if (next !== tr) tbody.insertBefore(tr, next);
    cursor = tr;
  }

  for (const stale of existing.values()) stale.remove();
  return true;
}

/* ── §6.2 表格：32px 行高 / 粘性表头 / 数字右对齐 / 排序列 aria-sort / 多选列 ── */
export function tableHtml(cols, rows, opts = {}) {
  if (!rows || !rows.length) return opts.empty || '<div class="empty">暂无数据</div>';
  const st = opts.stateKey ? App.state[opts.stateKey] : null;
  const sel = new Set(st && st.selected ? st.selected : []);
  const head = cols.map(c => {
    const cls = [c.num ? 'num' : '', c.sort ? 'sortable' : ''].filter(Boolean).join(' ');
    let inner = esc(c.l), attrs = '';
    if (c.sort) {
      const active = st && st.sortKey === c.k;
      const dir = active ? (st.sortDesc === false ? 'ascending' : 'descending') : 'none';
      attrs = ` aria-sort="${dir}"`;
      inner += `<span class="sarrow" aria-hidden="true">${active ? (st.sortDesc === false ? '↑' : '↓') : '↕'}</span>`;
      inner = `<span data-ui-action="sort" data-sort-key="${esc(opts.stateKey)}" data-sort-column="${esc(c.k)}" style="display:inline-block">${inner}</span>`;
    }
    return `<th${cls ? ` class="${cls}"` : ''}${attrs}>${inner}</th>`;
  }).join('');
  const selHead = opts.stateKey
    ? `<th class="tsel"><input type="checkbox" data-selall="${esc(opts.stateKey)}" data-ids="${esc(rows.map(r => r.id).join(','))}" aria-label="全选" ${rows.every(r => sel.has(r.id)) ? 'checked' : ''}></th>` : '';
  // opts.rowKey 给每行盖一个稳定标识，供 patchRows 在后续轮询里认出「还是这一行」。
  // 不传就不盖——静态表没有增量刷新的需求，多一个属性只是噪音。
  const body = rows.map(r => `<tr${opts.rowKey ? ` data-row-key="${esc(opts.rowKey(r))}"` : ''}>${opts.stateKey ? `<td class="tsel"><input type="checkbox" data-sel="${esc(r.id)}" data-selkey="${esc(opts.stateKey)}" aria-label="选择该行" ${sel.has(r.id) ? 'checked' : ''}></td>` : ''}${cols.map(c => `<td${c.num ? ' class="num"' : ''}>${c.render ? c.render(r) : esc(r[c.k] ?? '—')}</td>`).join('')}</tr>`).join('');
  return `<table><thead><tr>${selHead}${head}</tr></thead><tbody>${body}</tbody></table>`;
}

export function pagerHtml(id, st) {
  const pages = Math.max(1, Math.ceil(st.totalCount / st.pageSize));
  return `<div class="pager">
    <span>共 ${st.totalCount} 条 · 第 ${st.page}/${pages} 页</span>
    <button class="small" data-ui-action="page" data-page-key="${esc(id)}" data-page-delta="-1" ${st.page <= 1 ? 'disabled' : ''}>上一页</button>
    <button class="small" data-ui-action="page" data-page-key="${esc(id)}" data-page-delta="1" ${st.page >= pages ? 'disabled' : ''}>下一页</button>
  </div>`;
}

/* ── §6.5 骨架屏（形状同真实内容，无闪烁动画） ── */
export function skeleton(n = 4) {
  const w = [96, 82, 90, 74, 88, 80];
  let rows = '';
  for (let i = 0; i < n; i++) rows += `<div class="sk-row" style="width:${w[i % w.length]}%"></div>`;
  return `<div class="sk" aria-hidden="true">${rows}</div>`;
}

/* ── §6.5 空态三分：first=首次为空 / filter=筛选为空 / ok=正常的空（好消息） ── */
export function emptyState(kind, o = {}) {
  if (kind === 'ok') {
    return `<div class="empty ok" role="status"><span class="e-glyph" aria-hidden="true">✓</span><div class="e-title">${esc(o.title || '没有待处理事项')}</div>${o.sub ? `<div class="e-sub">${esc(o.sub)}</div>` : ''}</div>`;
  }
  if (kind === 'filter') {
    return `<div class="empty"><span class="e-glyph" aria-hidden="true">⌕</span><div class="e-title">${esc(o.title || '没有匹配的结果')}</div><div class="e-act"><button data-clearfilter="${esc(o.key || '')}">清除筛选</button></div></div>`;
  }
  return `<div class="empty"><span class="e-glyph" aria-hidden="true">${esc(o.glyph || '▦')}</span><div class="e-title">${esc(o.title || '暂无数据')}</div>${o.sub ? `<div class="e-sub">${esc(o.sub)}</div>` : ''}${o.actHtml ? `<div class="e-act">${o.actHtml}</div>` : ''}</div>`;
}
export function hasFilter(st, keys) { return keys.some(k => st[k] !== undefined && st[k] !== ''); }

/* ── §6.2 批量动作条：选中 ≥1 项时工具条原地变形 ── */
export function batchBarHtml(key) {
  const st = App.state[key];
  const n = st && st.selected ? st.selected.length : 0;
  if (!n) return '';
  const acts = (App.batchActs && App.batchActs[key]) || [];
  const rows = batchSelectedRows(key);
  return `<div class="batchbar" role="toolbar" aria-label="批量操作">
    <span class="count">已选 ${n} 项</span>
    ${acts.map((a, i) => {
      // 逐行判定能不能做。一个都轮不上就变灰并说明原因——已注销的机器选中后
      // 「立即备份 / 刷新指标 / 禁用 / 注销」原先全都亮着，点下去服务端一律 409：
      // 单行的按钮早就按 clientCaps 变灰了，批量条却是另一套，两处必须同一个判据。
      const usable = rows === null || !a.allow ? n : rows.filter(a.allow).length;
      const off = usable === 0;
      const title = off ? (a.why || '所选项目不支持这个操作') : '';
      return `<button class="small${a.primary ? ' primary' : ''}${a.danger ? ' danger' : ''}"${off ? ' disabled' : ''}${title ? ` title="${esc(title)}"` : ''} data-ui-action="batch" data-batch-key="${esc(key)}" data-batch-index="${i}">${esc(a.t)}${!off && usable < n ? `（${usable}）` : ''}</button>`;
    }).join('')}
    <div class="spacer"></div>
    <button class="small" data-ui-action="clear-selection" data-batch-key="${esc(key)}">取消选择</button>
  </div>`;
}

/* 选中的那几行的完整数据。视图把当前页的行放在 st.rows 里才判得了；
   放不齐（跨页选择、视图没提供）时返回 null，调用方一律按「都能做」处理——
   判不准的时候把按钮灰掉，比让人点一下得到一句错误更让人无从下手。 */
export function batchSelectedRows(key) {
  const st = App.state[key];
  const ids = (st && st.selected) || [];
  const rows = (st && st.rows) || null;
  if (!rows || !ids.length) return null;
  const picked = rows.filter(r => ids.includes(r.id));
  return picked.length === ids.length ? picked : null;
}

/* ── 搜索式选择器：输入关键字 → 服务端只返回匹配的前 N 条 → 点选。
   替代此前「一次拉 200 条塞进下拉框」的写法。那种写法在客户端超过 200 台时会
   静默丢掉后面的，而使用者完全看不出自己是在一个被截断的列表里挑——
   这里改成永远只展示「本次搜索的前 N 条」，并在还有更多时明说还有多少。

   search(keyword) 由调用方提供（ui.js 不依赖 api.js，避免模块环）：
   返回 { items: [{ v, t, sub, raw }], total }，total 是服务端的匹配总数。 */
export function searchPickerHtml(id, { multi = false, placeholder = '输入名称或主机名搜索' } = {}) {
  return `<div class="picker" data-picker="${esc(id)}">
    <input type="search" class="picker-input" placeholder="${esc(placeholder)}" autocomplete="off" aria-label="${esc(placeholder)}">
    ${multi ? '<div class="picker-chips" hidden></div>' : ''}
    <div class="picker-list" role="listbox" aria-busy="false"><div class="picker-empty">正在加载…</div></div>
  </div>`;
}

/* 返回 { selected() } —— 单选返回 raw 或 null，多选返回 raw 数组。
   调用方拿到的是搜索结果里的原始对象，不是 id：后续步骤（比如按 agentVersion
   决定能不能浏览目录）需要整条记录，只回 id 会逼调用方再查一次。 */
export function initSearchPicker(root, id, { search, multi = false, onChange = null } = {}) {
  const box = root.querySelector(`[data-picker="${id}"]`);
  const input = box.querySelector('.picker-input');
  const list = box.querySelector('.picker-list');
  const chips = box.querySelector('.picker-chips');
  const picked = new Map();          // v -> { v, t, sub, raw }
  let single = null;
  let seq = 0;                        // 只认最后一次请求的结果，防止慢响应盖掉新结果

  const notify = () => { if (typeof onChange === 'function') onChange(selected()); };
  const selected = () => (multi ? [...picked.values()].map(x => x.raw) : (single ? single.raw : null));

  const renderChips = () => {
    if (!chips) return;
    chips.hidden = picked.size === 0;
    chips.innerHTML = [...picked.values()].map(o =>
      `<span class="picker-chip">${esc(o.t)}<button type="button" class="picker-chip-x" data-drop="${esc(o.v)}" aria-label="移除 ${esc(o.t)}">×</button></span>`).join('');
  };

  const markRows = () => {
    for (const row of list.querySelectorAll('.picker-item')) {
      const on = multi ? picked.has(row.dataset.v) : (single && single.v === row.dataset.v);
      row.classList.toggle('is-on', !!on);
      row.setAttribute('aria-selected', on ? 'true' : 'false');
    }
  };

  const run = async () => {
    const mine = ++seq;
    const keyword = input.value.trim();
    list.setAttribute('aria-busy', 'true');
    let result;
    try { result = await search(keyword); }
    catch (e) {
      if (mine !== seq) return;
      list.setAttribute('aria-busy', 'false');
      list.innerHTML = `<div class="picker-empty">搜索失败：${esc(e && e.message ? e.message : String(e))}</div>`;
      return;
    }
    if (mine !== seq) return;
    list.setAttribute('aria-busy', 'false');
    const items = (result && result.items) || [];
    const total = result && Number.isFinite(result.total) ? result.total : items.length;
    if (!items.length) {
      list.innerHTML = `<div class="picker-empty">${keyword ? '没有匹配的结果，换个关键字试试' : '暂无可选项'}</div>`;
      return;
    }
    list.innerHTML = items.map(o =>
      `<div class="picker-item" role="option" tabindex="0" data-v="${esc(o.v)}">
        <span class="picker-item-t">${esc(o.t)}</span>${o.sub ? `<span class="picker-item-sub">${esc(o.sub)}</span>` : ''}
      </div>`).join('')
      + (total > items.length ? `<div class="picker-more">还有 ${total - items.length} 条未显示，继续输入关键字缩小范围</div>` : '');
    // 行数据挂在闭包里，点选时不必再查一次
    for (const row of list.querySelectorAll('.picker-item'))
      row._opt = items.find(o => String(o.v) === row.dataset.v);
    markRows();
  };

  const choose = row => {
    const opt = row._opt;
    if (!opt) return;
    if (multi) {
      if (picked.has(opt.v)) picked.delete(opt.v); else picked.set(opt.v, opt);
      renderChips();
    } else {
      single = opt;
    }
    markRows();
    notify();
  };

  let timer = null;
  input.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(run, 250); });
  list.addEventListener('click', ev => {
    const row = ev.target.closest('.picker-item');
    if (row) choose(row);
  });
  list.addEventListener('keydown', ev => {
    const row = ev.target.closest('.picker-item');
    if (row && (ev.key === 'Enter' || ev.key === ' ')) { ev.preventDefault(); choose(row); }
  });
  if (chips) chips.addEventListener('click', ev => {
    const x = ev.target.closest('[data-drop]');
    if (!x) return;
    picked.delete(x.dataset.drop);
    renderChips();
    markRows();
    notify();
  });

  run();
  return { selected };
}
